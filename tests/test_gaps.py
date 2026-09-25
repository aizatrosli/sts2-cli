"""Regression tests for the gap analysis (docs/remediation-plan.md).

Each known bug is a strict xfail tagged with the phase that fixes it: a fix that lands flips
the test to XPASS, which fails the suite until the marker is removed.
"""
import pytest


def gap(phase, reason):
    return pytest.mark.xfail(strict=True, reason=f"{reason} (remediation phase {phase})")


# --- helpers -----------------------------------------------------------------------------

def legal(state):
    return state.get("legal_actions") or []


def legal_names(state):
    return {a["action"] for a in legal(state)}


def to_map(game, character="Ironclad", seed="gaps", flow="manual", ascension=0):
    """Start a run and resolve Neow with listed actions until the map."""
    state = game.start(character=character, seed=seed, flow=flow, ascension=ascension)
    for _ in range(60):
        if state.get("decision") == "map_select":
            return state
        actions = legal(state)
        pick = next((a for pref in ("proceed", "skip_card_reward", "skip_relic", "skip_select")
                     for a in actions if a["action"] == pref), None)
        if pick is None:
            pick = actions[0]
            if pick.get("template"):
                n = pick.get("min_select") or 0
                pick = {"action": "select_cards", "args": {"indices": ",".join(map(str, range(n)))}}
        state = game.send({"cmd": "action", **pick})
    raise AssertionError(f"never reached the map: {state.get('decision')}")


def first_combat(game, deck, character="Silent", flow="manual"):
    """Map, then replace the deck, then take the first (always Monster) node."""
    state = to_map(game, character=character, flow=flow)
    assert game.set_player(deck=deck)["type"] == "ok"
    choice = state["choices"][0]
    state = game.act("select_map_node", col=choice["col"], row=choice["row"])
    assert state["decision"] == "combat_play", state
    return state


def hand_index(state, card_id):
    return next(c["index"] for c in state["hand"] if card_id in c["id"])


def fight(game, state, limit=3000):
    """Play the first listed card (or end turn); answer selections with their minimum."""
    for _ in range(limit):
        dec = state.get("decision")
        if dec == "card_select":
            n = max(state.get("min_select") or 0, 1 if state.get("min_select") else 0)
            state = (game.act("select_cards", indices=",".join(map(str, range(n)))) if n
                     else game.act("skip_select"))
        elif dec == "combat_play":
            plays = [a for a in legal(state) if a["action"] == "play_card"]
            a = plays[0] if plays else {"action": "end_turn"}
            state = game.send({"cmd": "action", **a})
        else:
            return state
    raise AssertionError("combat did not end")


def is_error(resp):
    return resp.get("type") == "error"


# --- Phase 1: actions outside legal_actions are rejected and change nothing ---------------

class TestValidation:
    @gap(1, "select_map_node accepts unreachable nodes")
    def test_map_select_rejects_unreachable_node(self, game):
        state = to_map(game)
        reachable = {(c["col"], c["row"]) for c in state["choices"]}
        rows = game.get_map()["rows"]
        far = next(n for row in rows for n in row if n["row"] >= 4 and (n["col"], n["row"]) not in reachable)
        resp = game.act("select_map_node", col=far["col"], row=far["row"])
        assert is_error(resp), resp.get("context")
        assert game.send({"cmd": "get_state"})["decision"] == "map_select"

    @gap(1, "select_map_node works mid-combat")
    def test_map_select_rejected_in_combat(self, game):
        state = first_combat(game, ["STRIKE_SILENT"] * 5 + ["DEFEND_SILENT"] * 5)
        rows = game.get_map()["rows"]
        node = next(n for row in rows for n in row if n["row"] == 3)
        resp = game.act("select_map_node", col=node["col"], row=node["row"])
        assert is_error(resp)
        assert game.send({"cmd": "get_state"})["decision"] == "combat_play"

    @gap(1, "select_cards ignores min/max, duplicates and junk")
    @pytest.mark.parametrize("indices", ["0,1,2", "0,0", "", "99", "x"])
    def test_select_cards_bad_indices_rejected(self, game, indices):
        to_map(game)
        state = game.enter_room("rest")
        smith = next(o for o in state["options"] if "SMITH" in str(o).upper())
        state = game.act("choose_option", option_index=smith["index"])
        assert state["decision"] == "card_select" and state["max_select"] == 1
        resp = game.act("select_cards", indices=indices)
        assert is_error(resp)
        after = game.send({"cmd": "get_state"})
        assert sum(c["upgraded"] for c in after["player"]["deck"]) == 0

    @gap(1, "skip_select is accepted when a pick is required")
    def test_skip_select_rejected_when_min_positive(self, game):
        state = first_combat(game, ["SURVIVOR"] * 5 + ["STRIKE_SILENT"] * 5)
        state = game.act("play_card", card_index=hand_index(state, "SURVIVOR"))
        assert state["decision"] == "card_select" and state["min_select"] >= 1
        assert "skip_select" not in legal_names(state)
        assert is_error(game.act("skip_select"))

    @gap(1, "a rejected play_card still resolves after the selection")
    def test_rejected_play_card_has_no_effect(self, game):
        def discard_a_survivor(state):
            idx = next(c["index"] for c in state["cards"] if "SURVIVOR" in c["id"])
            game.act("select_cards", indices=str(idx))
            return game.send({"cmd": "get_state"})

        state = first_combat(game, ["SURVIVOR"] * 5 + ["STRIKE_SILENT"] * 5)
        state = game.act("play_card", card_index=hand_index(state, "SURVIVOR"))
        assert state["decision"] == "card_select"
        clean = discard_a_survivor(state)
        # Same setup again (same seed), this time with an illegal play_card during the selection.
        state = first_combat(game, ["SURVIVOR"] * 5 + ["STRIKE_SILENT"] * 5)
        state = game.act("play_card", card_index=hand_index(state, "SURVIVOR"))
        strike = next(c["index"] for c in state["cards"] if "STRIKE" in c["id"])
        assert is_error(game.act("play_card", card_index=strike, target_index=0))
        probed = discard_a_survivor(state)
        assert [e["hp"] for e in probed["enemies"]] == [e["hp"] for e in clean["enemies"]]
        assert probed["energy"] == clean["energy"]

    @gap(1, "a rejected start_run destroys the current run")
    def test_rejected_start_run_keeps_run(self, game):
        to_map(game)
        assert is_error(game.start(character="NotACharacter"))
        assert game.send({"cmd": "get_state"}).get("decision") == "map_select"


# --- Phase 2: game rules -----------------------------------------------------------------

def win_act1_boss(game, ascension=0):
    to_map(game, ascension=ascension)
    game.set_player(hp=9999, max_hp=9999)
    state = fight(game, game.enter_room("combat", encounter="CEREMONIAL_BEAST_BOSS"))
    assert state["decision"] == "rewards", state.get("decision")
    return state


class TestGameRules:
    @gap(2, "act start offers nodes past the Ancient")
    def test_act_start_only_offers_the_ancient(self, game):
        win_act1_boss(game)
        state = game.act("proceed")
        assert state["decision"] == "map_select" and state["context"]["act"] == 2
        assert [c["type"] for c in state["choices"]] == ["Ancient"]

    @gap(2, "HealBetweenActs heals on top of the Ancient's own heal")
    def test_no_extra_heal_between_acts(self, game):
        win_act1_boss(game, ascension=2)
        game.set_player(hp=20, max_hp=100)
        state = game.act("proceed")
        assert state["player"]["hp"] == 20
        ancient = next(c for c in state["choices"] if c["type"] == "Ancient")
        state = game.act("select_map_node", col=ancient["col"], row=ancient["row"])
        assert state["player"]["hp"] == 84  # 20 + 80% of 80 missing (A2 Weary Traveler)

    @gap(2, "choose-a-card screens that cannot be skipped are skippable")
    def test_abundance_choice_is_mandatory(self, game):
        state = first_combat(game, ["ABUNDANCE"] * 6, character="Ironclad")
        state = game.act("play_card", card_index=hand_index(state, "ABUNDANCE"))
        assert state["decision"] == "card_select"
        assert state["min_select"] == 1 and "skip_select" not in legal_names(state)

    @gap(2, "shop card removal can be bought repeatedly")
    def test_shop_removal_once_per_visit(self, game):
        to_map(game)
        game.set_player(gold=2000)
        state = game.enter_room("shop")
        state = game.act("remove_card")
        assert state["decision"] == "card_select"
        state = game.act("select_cards", indices="0")
        assert "remove_card" not in legal_names(state)
        assert is_error(game.act("remove_card"))

    @gap(2, "Neutralize patch bypasses attack hooks (Vigor never consumed)")
    def test_neutralize_consumes_vigor(self, game):
        state = first_combat(game, ["TERRAFORMING"] + ["NEUTRALIZE"] * 4)
        state = game.act("play_card", card_index=hand_index(state, "TERRAFORMING"))
        hp0 = state["enemies"][0]["hp"]
        state = game.act("play_card", card_index=hand_index(state, "NEUTRALIZE"), target_index=0)
        hp1 = state["enemies"][0]["hp"]
        state = game.act("play_card", card_index=hand_index(state, "NEUTRALIZE"), target_index=0)
        hp2 = state["enemies"][0]["hp"]
        assert hp0 - hp1 > hp1 - hp2 == 3  # Vigor boosts only the first attack

    @gap(2, "a combat-only potion used on the map is silently destroyed")
    def test_unusable_potion_is_not_consumed(self, game):
        to_map(game)
        game.set_player(potions=["FIRE_POTION"])
        assert is_error(game.act("use_potion", potion_index=0))
        potions = game.send({"cmd": "get_state"})["player"]["potions"]
        assert len(potions) == 1

    @gap(2, "drinkable potions are never listed outside combat")
    def test_anytime_potion_listed_on_map(self, game):
        to_map(game)
        game.set_player(potions=["FRUIT_JUICE"])
        state = game.send({"cmd": "get_state"})
        assert {"use_potion", "discard_potion"} <= legal_names(state)

    @gap(2, "Jungle Maze 'Join Forces' throws on a null audio singleton")
    def test_jungle_maze_join_forces(self, game):
        to_map(game)
        gold = game.send({"cmd": "get_state"})["player"]["gold"]
        state = game.enter_room("event", event="JUNGLE_MAZE_ADVENTURE")
        join = next(o for o in state["options"] if o["title"] == "Join Forces")
        state = game.act("choose_option", option_index=join["index"])
        assert "Background task failed" not in game.stderr_text()
        assert state["player"]["gold"] > gold


# --- Phase 3: robustness -----------------------------------------------------------------

class TestRobustness:
    @gap(3, "a selection opened while resolving another is wiped (combat wedges)")
    def test_chained_selection_does_not_wedge(self, game):
        to_map(game, character="Silent")
        game.set_player(hp=9999, max_hp=9999, deck=["TOOLS_OF_THE_TRADE"] * 5 + ["DEFEND_SILENT"] * 5)
        state = game.enter_room("combat", encounter="KNOWLEDGE_DEMON_BOSS")
        for _ in range(80):
            dec = state.get("decision")
            if dec == "combat_play":
                assert legal(state), "combat_play with no legal actions"
                tools = [c for c in state["hand"] if "TOOLS" in c["id"] and c["can_play"]
                         and c["cost"] <= state["energy"]]
                state = (game.act("play_card", card_index=tools[0]["index"]) if tools
                         else game.act("end_turn"))
            elif dec == "card_select":
                n = state["min_select"] or 1
                state = game.act("select_cards", indices=",".join(map(str, range(n))))
            else:
                break


class TestFuzz:
    @gap(1, "unlisted actions are accepted or change state")
    @pytest.mark.parametrize("flow", ["manual", "auto"])
    def test_short_fuzz_finds_no_violations(self, flow):
        import argparse
        import os
        import sys
        sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "tools"))
        from fuzz_legal import Fuzzer
        args = argparse.Namespace(character="Silent", seed=f"fuzz-{flow}", flow=flow, ascension=0,
                                  god=True, steps=60, probes=6, max_violations=1)
        summary = Fuzzer(args).run()
        assert summary["steps"] > 0
        assert summary["violations"] == 0
