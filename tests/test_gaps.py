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
    def test_map_select_rejects_unreachable_node(self, game):
        state = to_map(game)
        reachable = {(c["col"], c["row"]) for c in state["choices"]}
        rows = game.get_map()["rows"]
        far = next(n for row in rows for n in row if n["row"] >= 4 and (n["col"], n["row"]) not in reachable)
        resp = game.act("select_map_node", col=far["col"], row=far["row"])
        assert is_error(resp), resp.get("context")
        assert game.send({"cmd": "get_state"})["decision"] == "map_select"

    def test_map_select_rejected_in_combat(self, game):
        first_combat(game, ["STRIKE_SILENT"] * 5 + ["DEFEND_SILENT"] * 5)
        rows = game.get_map()["rows"]
        node = next(n for row in rows for n in row if n["row"] == 3)
        resp = game.act("select_map_node", col=node["col"], row=node["row"])
        assert is_error(resp)
        assert game.send({"cmd": "get_state"})["decision"] == "combat_play"

    @pytest.mark.parametrize("indices", ["0,1,2", "0,0", "", "99", "x"])
    def test_select_cards_bad_indices_rejected(self, game, indices):
        to_map(game)
        state = game.enter_room("rest")
        smith = next(o for o in state["options"] if "SMITH" in str(o).upper())
        state = game.act("choose_option", option_index=smith["index"])
        assert state["decision"] == "card_select" and state["max_select"] == 1
        upgraded = sum(c["upgraded"] for c in state["player"]["deck"])
        resp = game.act("select_cards", indices=indices)
        assert is_error(resp)
        after = game.send({"cmd": "get_state"})
        assert sum(c["upgraded"] for c in after["player"]["deck"]) == upgraded

    def test_skip_select_rejected_when_min_positive(self, game):
        state = first_combat(game, ["SURVIVOR"] * 5 + ["STRIKE_SILENT"] * 5)
        state = game.act("play_card", card_index=hand_index(state, "SURVIVOR"))
        assert state["decision"] == "card_select" and state["min_select"] >= 1
        assert "skip_select" not in legal_names(state)
        assert is_error(game.act("skip_select"))

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
    def test_act_start_only_offers_the_ancient(self, game):
        win_act1_boss(game)
        state = game.act("proceed")
        assert state["decision"] == "map_select" and state["context"]["act"] == 2
        assert [c["type"] for c in state["choices"]] == ["Ancient"]

    def test_no_extra_heal_between_acts(self, game):
        win_act1_boss(game, ascension=2)
        game.set_player(hp=20, max_hp=100)
        state = game.act("proceed")
        assert state["player"]["hp"] == 20
        ancient = next(c for c in state["choices"] if c["type"] == "Ancient")
        state = game.act("select_map_node", col=ancient["col"], row=ancient["row"])
        assert state["player"]["hp"] == 84  # 20 + 80% of 80 missing (A2 Weary Traveler)

    def test_abundance_choice_is_mandatory(self, game):
        to_map(game, character="Ironclad")
        if game.set_player(deck=["ABUNDANCE"])["type"] != "ok":
            pytest.skip("ABUNDANCE is not in this game build (added after v0.107.1)")
        state = first_combat(game, ["ABUNDANCE"] * 6, character="Ironclad")
        state = game.act("play_card", card_index=hand_index(state, "ABUNDANCE"))
        assert state["decision"] == "card_select"
        assert state["min_select"] == 1 and "skip_select" not in legal_names(state)

    def test_shop_removal_once_per_visit(self, game):
        to_map(game)
        game.set_player(gold=2000)
        state = game.enter_room("shop")
        state = game.act("remove_card")
        assert state["decision"] == "card_select"
        state = game.act("select_cards", indices="0")
        assert "remove_card" not in legal_names(state)
        assert is_error(game.act("remove_card"))

    def test_neutralize_consumes_vigor(self, game):
        state = first_combat(game, ["TERRAFORMING"] + ["NEUTRALIZE"] * 4)
        state = game.act("play_card", card_index=hand_index(state, "TERRAFORMING"))
        hp0 = state["enemies"][0]["hp"]
        state = game.act("play_card", card_index=hand_index(state, "NEUTRALIZE"), target_index=0)
        hp1 = state["enemies"][0]["hp"]
        state = game.act("play_card", card_index=hand_index(state, "NEUTRALIZE"), target_index=0)
        hp2 = state["enemies"][0]["hp"]
        assert hp0 - hp1 > hp1 - hp2 == 3  # Vigor boosts only the first attack

    def test_unusable_potion_is_not_consumed(self, game):
        to_map(game)
        game.set_player(potions=["FIRE_POTION"])
        assert is_error(game.act("use_potion", potion_index=0))
        potions = game.send({"cmd": "get_state"})["player"]["potions"]
        assert len(potions) == 1

    def test_anytime_potion_listed_on_map(self, game):
        to_map(game)
        game.set_player(potions=["FRUIT_JUICE"])
        state = game.send({"cmd": "get_state"})
        assert {"use_potion", "discard_potion"} <= legal_names(state)

    def test_foul_potion_at_shop_gives_gold(self, game):
        to_map(game)
        game.set_player(potions=["FOUL_POTION"])
        state = game.enter_room("shop")
        assert {"action": "use_potion", "args": {"potion_index": 0}} in legal(state)
        gold = state["player"]["gold"]
        state = game.act("use_potion", potion_index=0)
        assert state["decision"] == "shop" and state["player"]["gold"] > gold
        assert not state["player"]["potions"]

    def test_fake_merchant_shop(self, game):
        to_map(game)
        game.set_player(gold=300)
        state = game.enter_room("event", event="FAKE_MERCHANT")
        assert state["decision"] == "fake_merchant" and len(state["relics"]) == 6
        relic = state["relics"][0]
        state = game.act("buy_relic", relic_index=0)
        assert state["decision"] == "fake_merchant"
        assert state["player"]["gold"] == 300 - relic["cost"]
        assert relic["id"] in {r["id"] for r in state["player"]["relics"]}
        assert not state["relics"][0]["is_stocked"]
        assert {"action": "buy_relic", "args": {"relic_index": 0}} not in legal(state)
        assert game.act("proceed")["decision"] == "map_select"

    def test_fake_merchant_foul_potion_fight(self, game):
        to_map(game)
        game.set_player(gold=300, hp=9999, max_hp=9999, potions=["FOUL_POTION"])
        state = game.enter_room("event", event="FAKE_MERCHANT")
        state = game.act("buy_relic", relic_index=0)
        state = game.act("use_potion", potion_index=0)
        assert state["decision"] == "combat_play"
        state = fight(game, state)
        assert state["decision"] == "rewards", state.get("decision")
        relic_rewards = [r for r in state["rewards"] if r.get("type") == "relic"]
        assert len(relic_rewards) == 1 + 5  # the Rug plus every relic still on sale

    def test_jungle_maze_join_forces(self, game):
        to_map(game)
        gold = game.send({"cmd": "get_state"})["player"]["gold"]
        state = game.enter_room("event", event="JUNGLE_MAZE_ADVENTURE")
        join = next(o for o in state["options"] if o["title"] == "Join Forces")
        state = game.act("choose_option", option_index=join["index"])
        assert "Background task failed" not in game.stderr_text()
        assert state["player"]["gold"] > gold


class TestSeeds:
    def test_seed_is_canonicalized_like_the_lobby(self, game):
        a = game.start(seed="fido1")
        b = game.start(seed="  FID01 ")
        assert a["context"]["seed"] == b["context"]["seed"] == "F1D01"
        assert a["options"] == b["options"]

    def test_unseeded_runs_differ(self, game):
        seeds = {game.send({"cmd": "start_run", "character": "Ironclad"})["context"]["seed"] for _ in range(3)}
        assert len(seeds) == 3

    def test_testmode_patches_applied(self, game):
        game.start(seed="tm")
        assert "TestMode patches: 3/3" in game.stderr_text()


# --- Phase 3: robustness -----------------------------------------------------------------

class TestRobustness:
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
    @pytest.mark.engine
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

    def test_stdout_is_json_only(self, game):
        state = to_map(game)
        game.act("select_map_node", col=state["choices"][0]["col"], row=state["choices"][0]["row"])
        assert game.stdout_noise == []

    def test_no_patch_warnings(self, game):
        assert "patch_warnings" not in game.start(seed="pw")

    def test_set_player_relics_are_owned(self, game):
        to_map(game)
        resp = game.set_player(relics=["WINGED_BOOTS"])
        assert resp["type"] == "ok", resp
        assert [r["name"] for r in resp["player"]["relics"]] == ["Winged Boots"]
        state = game.enter_room("rest")  # hooks run over the relics; an unowned relic threw here
        assert state["type"] == "decision", state
        assert is_error(game.set_player(relics=["NOT_A_RELIC"]))
        assert [r["name"] for r in game.send({"cmd": "get_state"})["player"]["relics"]] == ["Winged Boots"]

    def test_winged_boots_free_travel(self, game):
        state = to_map(game)
        normal = {(c["col"], c["row"]) for c in state["choices"]}
        game.set_player(relics=["WINGED_BOOTS"])
        state = game.send({"cmd": "get_state"})
        row = state["choices"][0]["row"]
        whole_row = {(n["col"], n["row"]) for r in game.get_map()["rows"] for n in r if n["row"] == row}
        assert {(c["col"], c["row"]) for c in state["choices"]} == whole_row
        assert normal <= whole_row


class TestAutoFlowSync:
    def test_skip_select_waits_for_the_event(self, game):
        """Auto flow: skipping Hefty Tablet's card choice must return the finished Neow, not its first page."""
        state = game.start(character="Silent", seed="traj-silent", flow="auto")
        tablet = next((o for o in state["options"] if o["title"] == "Hefty Tablet"), None)
        if tablet is None:
            pytest.skip("seed no longer offers Hefty Tablet")
        state = game.act("choose_option", option_index=tablet["index"])
        assert state["decision"] == "card_select" and state["min_select"] == 0
        deck = state["player"]["deck_size"]
        state = game.act("skip_select")
        assert state["player"]["deck_size"] == deck + 1  # the Injury is already added
        assert not (state["decision"] == "event_choice"
                    and any(o["title"] == "Hefty Tablet" for o in state.get("options", [])))
