"""Coverage for flows the other suites do not reach: act transitions and victory, the A10
double boss, load_save in manual flow, potions in combat, card reward alternatives, and
events that start a fight."""
import pytest

from test_gaps import fight, legal, to_map

STRONG_DECK = ["BLUDGEON"] * 6 + ["DEMON_FORM"] * 4  # beats even A10 Test Subject quickly


def boss_id(state, key="boss"):
    return state["context"][key]["id"].split(".")[-1]


def beat_boss_with_debug_room(game, state):
    """Fight the act's boss in a debug room, then take the terminal rewards screen's proceed."""
    state = fight(game, game.enter_room("combat", encounter=boss_id(state)), limit=5000)
    assert state["decision"] == "rewards" and state["is_terminal"]
    return game.act("proceed")


def step(game, state):
    """A simple deterministic player: first map choice, first playable card, skip rewards."""
    dec = state["decision"]
    actions = legal(state)
    if dec == "combat_play":
        plays = [a for a in actions if a["action"] == "play_card"]
        return game.send({"cmd": "action", **(plays[0] if plays else {"action": "end_turn"})})
    for pref in ("select_map_node", "proceed", "skip_card_reward", "skip_relic", "skip_select",
                 "open_chest", "choose_option", "select_bundle", "crystal_sphere_divine", "leave_room"):
        a = next((a for a in actions if a["action"] == pref), None)
        if a:
            return game.send({"cmd": "action", **a})
    template = next(a for a in actions if a.get("template"))
    n = template.get("min_select") or 0
    return game.act("select_cards", indices=",".join(map(str, range(n))))


class TestActs:
    def test_act_transitions_and_victory(self, game):
        state = to_map(game)
        game.set_player(hp=9999, max_hp=9999, deck=STRONG_DECK)
        acts = []
        for _ in range(3):
            acts.append(state["context"]["act_id"])
            state = beat_boss_with_debug_room(game, state)
            if state["decision"] == "game_over":
                break
            assert state["decision"] == "map_select"
            # A new act starts at its Ancient, the only node on offer.
            assert len(state["choices"]) == 1 and state["context"]["floor"] <= 1
        assert acts[1:] == ["HIVE", "GLORY"]
        assert state["decision"] == "game_over" and state["victory"] is True

    @pytest.mark.slow
    def test_a10_double_boss(self, game):
        state = to_map(game, ascension=10)
        game.set_player(hp=9999, max_hp=9999, deck=STRONG_DECK)
        for _ in range(2):
            state = beat_boss_with_debug_room(game, state)
            for _ in range(60):  # through the next act's Ancient to its map
                if state["decision"] == "map_select" and state["context"]["floor"] >= 1:
                    break
                state = step(game, state)
        assert state["context"]["act"] == 3 and state["context"].get("second_boss")
        assert boss_id(state) != boss_id(state, "second_boss")

        # Walk act 3's map to its boss: the second boss is only offered after the first.
        for _ in range(4000):
            if state["decision"] == "combat_play" and state["context"]["room_type"] == "Boss":
                break
            state = step(game, state)
        first_enemies = {e["id"] for e in state["enemies"]}
        state = fight(game, state, limit=5000)
        state = game.act("proceed")
        assert state["decision"] == "map_select"
        (choice,) = state["choices"]
        assert choice["type"] == "Boss"
        state = game.act("select_map_node", col=choice["col"], row=choice["row"])
        assert state["decision"] == "combat_play" and state["context"]["room_type"] == "Boss"
        assert {e["id"] for e in state["enemies"]} != first_enemies  # the other boss
        state = fight(game, state, limit=5000)
        state = game.act("proceed")
        assert state["decision"] == "game_over" and state["victory"] is True


class TestManualFlowSaves:
    def test_load_save_keeps_manual_flow(self, game, tmp_path):
        state = to_map(game)
        path = str(tmp_path / "manual.save")
        assert game.send({"cmd": "write_continue_save", "path": path})["success"]
        choices = state["choices"]

        state = game.send({"cmd": "load_save", "path": path, "flow": "manual"})
        assert state["decision"] == "map_select" and state["context"]["flow"] == "manual"
        assert state["choices"] == choices
        game.set_player(hp=9999, max_hp=9999)
        state = game.act("select_map_node", col=choices[0]["col"], row=choices[0]["row"])
        state = fight(game, state)
        assert state["decision"] == "rewards"  # manual flow's rewards screen


class TestPotionsInCombat:
    def test_targeted_and_self_potions(self, game):
        state = to_map(game)
        game.set_player(potions=["FIRE_POTION", "BLOCK_POTION"])
        choice = state["choices"][0]
        state = game.act("select_map_node", col=choice["col"], row=choice["row"])
        assert state["decision"] == "combat_play" and len(state["enemies"]) >= 2

        fire = [a["args"] for a in legal(state) if a["action"] == "use_potion" and a["args"]["potion_index"] == 0]
        assert {a.get("target_index") for a in fire} == set(range(len(state["enemies"])))
        hp = [e["hp"] for e in state["enemies"]]
        state = game.act("use_potion", potion_index=0, target_index=1)
        assert state["enemies"][1]["hp"] < hp[1] and state["enemies"][0]["hp"] == hp[0]

        block = next(p["index"] for p in state["player"]["potions"] if p["id"] == "POTION.BLOCK_POTION")
        state = game.act("use_potion", potion_index=block)
        assert state["player"]["block"] > 0
        assert not state["player"]["potions"]


class TestCardRewardAlternatives:
    def test_paels_wing_sacrifice(self, game):
        to_map(game)
        game.set_player(hp=9999, max_hp=9999, relics=["PAELS_WING"])
        state = fight(game, game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK"))
        deck_size = state["player"]["deck_size"]
        card = next(r["index"] for r in state["rewards"] if r["type"] == "card")
        state = game.act("claim_reward", reward_index=card)
        alt = next(a for a in state["alternatives"] if a["id"] == "SACRIFICE")
        state = game.act("select_card_reward_alternative", alternative_index=alt["index"])
        assert state["decision"] == "rewards"
        assert all(r["type"] != "card" for r in state["rewards"])
        assert state["player"]["deck_size"] == deck_size


class TestEventCombats:
    def test_event_fight_resumes_event(self, game):
        to_map(game)
        game.set_player(hp=9999, max_hp=9999)
        state = game.enter_room("event", event="BATTLEWORN_DUMMY")
        state = game.act("choose_option", option_index=0)
        assert state["decision"] == "combat_play"
        state = fight(game, state)
        assert state["decision"] == "event_choice" and state["event_name"] == "Battleworn Dummy"
        state = game.act("choose_option", option_index=state["options"][0]["index"])
        assert state["decision"] == "map_select"
