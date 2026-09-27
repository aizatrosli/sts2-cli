"""Tests for rest site / campfire."""
import pytest


class TestRestSiteStructure:
    def test_rest_site_fields(self, game):
        state = game.start(seed="rs1")
        game.skip_neow(state)
        state = game.enter_room("rest_site")
        assert state["decision"] == "rest_site"
        for opt in state["options"]:
            assert "index" in opt
            assert "option_id" in opt
            assert "is_enabled" in opt

    def test_has_heal_and_smith(self, game):
        state = game.start(seed="rs2")
        game.skip_neow(state)
        state = game.enter_room("rest_site")
        ids = {o["option_id"] for o in state["options"]}
        assert "HEAL" in ids
        assert "SMITH" in ids


class TestRestSiteActions:
    def test_heal_restores_hp(self, game):
        state = game.start(seed="rsa1")
        game.skip_neow(state)
        game.set_player(hp=30, max_hp=80)
        state = game.enter_room("rest_site")
        heal = next((o for o in state["options"] if o["option_id"] == "HEAL" and o["is_enabled"]), None)
        assert heal, "HEAL not available"
        hp_before = state["player"]["hp"]
        state = game.act("choose_option", option_index=heal["index"])
        new_hp = state.get("player", {}).get("hp", hp_before)
        assert new_hp > hp_before

    def test_heal_caps_at_max(self, game):
        state = game.start(seed="rsa2")
        game.skip_neow(state)
        game.set_player(hp=79, max_hp=80)
        state = game.enter_room("rest_site")
        heal = next((o for o in state["options"] if o["option_id"] == "HEAL" and o["is_enabled"]), None)
        if not heal:
            pytest.skip("HEAL not available at near-full HP")
        state = game.act("choose_option", option_index=heal["index"])
        assert state.get("player", {}).get("hp", 0) <= 80

    def test_smith_triggers_card_select(self, game):
        state = game.start(seed="rsa3")
        game.skip_neow(state)
        state = game.enter_room("rest_site")
        smith = next((o for o in state["options"] if o["option_id"] == "SMITH" and o["is_enabled"]), None)
        assert smith, "SMITH not available"
        state = game.act("choose_option", option_index=smith["index"])
        assert state["decision"] == "card_select"
        assert len(state.get("cards", [])) > 0


def test_rest_option_that_opens_a_card_reward_exports_it(game):
    """Auto flow: Dream Catcher's heal offers a card reward; that is the next decision (it used to
    return a stale map_select with the reward still pending), and picking it leaves for the map."""
    game.skip_neow(game.start(seed="dream1"))
    game.set_player(relics=["DREAM_CATCHER"], hp=20)
    deck = len(game.send({"cmd": "get_state"})["player"]["deck"])
    state = game.enter_room("rest_site")
    heal = next(o for o in state["options"] if o["option_id"] == "HEAL")
    state = game.act("choose_option", option_index=heal["index"])
    assert state["decision"] == "card_reward", state
    state = game.act("select_card_reward", card_index=0)
    assert state["decision"] == "map_select", state
    assert len(state["player"]["deck"]) == deck + 1


def test_cancelled_smith_finishes_before_the_next_option(game):
    """Auto flow: Smith, then skipping its grid, returns to the rest site; the Smith option task
    must finish there. Left pending, it resumed after the next rest site's Heal had cleared the
    options and threw inside RestSiteSynchronizer.ChooseOption (its log line re-reads
    options[index]): an unobserved task exception. Reproduced by the random agent of
    tools/trajectory.py (Silent auto, steps 62-64: Smith, skip_select, Heal); start_run collects
    the torn-down run's tasks, so a failure shows up on its response."""
    import random
    rng = random.Random("Silent:auto:traj-silent")
    game.send({"cmd": "start_run", "character": "Silent", "seed": "traj-silent", "flow": "auto"})
    game.send({"cmd": "set_player", "hp": 9999, "max_hp": 9999})
    state = game.send({"cmd": "get_state"})
    trail = []
    for _ in range(66):
        a = rng.choice(state["legal_actions"])
        if a.get("template"):
            n = a["num_cards"]
            lo, hi = a.get("min_select") or 0, min(a.get("max_select") or 0, n)
            k = rng.randint(lo, max(lo, hi))
            a = {"action": "select_cards", "args": {"indices": ",".join(map(str, sorted(rng.sample(range(n), k))))}}
        else:
            a = {k: v for k, v in a.items() if k in ("action", "args")}
        trail.append((state.get("decision"), a["action"]))
        state = game.send({"cmd": "action", **a})
        assert not state.get("warnings"), state.get("warnings")
    assert ("card_select", "skip_select") in trail   # the Smith skip is in the path
    r = game.send({"cmd": "start_run", "character": "Silent", "seed": "rsa4", "flow": "auto"})
    assert not r.get("warnings"), r.get("warnings")
