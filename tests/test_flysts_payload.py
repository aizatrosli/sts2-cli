"""The flysts payload mode (start_run {payload: "flysts"}, docs/flysts_payload.md).

Needs a save profile (progress.save): $FLYSTS_PROFILE, else the flysts headless pin. The
fidelity check against logged live games is flysts's tools/cli_parity.py; these tests cover the
protocol, determinism, and that the default protocol is unaffected.
"""
import os

import pytest

PROFILE = os.environ.get("FLYSTS_PROFILE") or os.path.expanduser(
    "~/.local/share/flysts-headless/pinned/modded/profile2/saves/progress.save")

pytestmark = pytest.mark.skipif(not os.path.isfile(PROFILE), reason=f"no save profile at {PROFILE}")


def start(game, seed="FLYS1", mode="custom"):
    cmd = {"cmd": "start_run", "payload": "flysts", "character": "Ironclad", "game_mode": mode, "profile": PROFILE}
    if seed:
        cmd["seed"] = seed
    return game.send(cmd)


def act(game, action, **args):
    return game.send({"cmd": "flysts_action", "action": action, "args": args})


def test_start_reports_the_mod_payload(game):
    r = start(game)
    assert r["type"] == "flysts" and r["status"] == "ok"
    state = r["state"]
    assert state["state_type"] == "event" and state["event_id"] == "NEOW"
    assert state["run"] == {"act": 1, "floor": 1, "ascension": 0, "seed": "FLYS1", "game_mode": "custom", "modifiers": []}
    player = state["player"]
    assert player["character"] == "The Ironclad"
    assert [c["id"] for c in player["deck"]].count("STRIKE_IRONCLAD") == 5
    assert all(r["id"] == r["id"].upper() and "." not in r["id"] for r in player["relics"])
    assert len(player["potion_slots_detail"]) == player["potion_slots"]
    # the lite run_state object (GameState.PruneRunState)
    rs = state["run_state"]
    assert set(rs) >= {"odds", "current_act_index", "players", "acts"} and "run_time" not in rs


def test_same_seed_same_run(game):
    a = start(game, "PARITY")["state"]
    b = start(game, "PARITY")["state"]
    assert a == b
    assert start(game, "OTHER")["state"]["run_state"]["acts"] != a["run_state"]["acts"] or \
        start(game, "OTHER")["state"]["options"] != a["options"]


def test_unseeded_is_standard_mode(game):
    state = start(game, seed=None, mode="standard")["state"]
    assert state["run"]["game_mode"] == "standard" and state["run"]["seed"]


def test_refused_action_changes_nothing(game):
    before = start(game)["state"]
    r = act(game, "play_card", card_index=0)
    assert r["status"] == "error" and r["state"] == before
    r = act(game, "choose_event_option", index=99)
    assert r["status"] == "error" and "out of range" in r["error"] and r["state"] == before


def test_walk_to_first_combat(game):
    state = start(game)["state"]
    for _ in range(40):
        st = state["state_type"]
        if st in ("monster", "elite", "boss"):
            break
        if st == "event":
            r = act(game, "choose_event_option", index=next(o["index"] for o in state["options"] if not o["locked"]))
        elif st == "map":
            r = act(game, "choose_map_node", index=0)
        elif st == "choose_a_bundle":
            r = act(game, "select_bundle", index=0)
            assert r["state"]["can_confirm"] and r["state"]["bundles"][0]["selected"]
            r = act(game, "confirm")
        elif st == "card_reward":
            r = act(game, "select_card_reward", card_index=0)
        elif st in ("deck_card_select", "deck_upgrade", "deck_transform", "deck_enchant", "simple_card_select"):
            r = act(game, "select_deck_card", card_index=0)
            if r["state"]["state_type"] == st:
                r = act(game, "confirm")
        else:
            pytest.fail(f"unexpected state {st}")
        assert r["status"] == "ok", r
        state = r["state"]
    assert state["state_type"] in ("monster", "elite", "boss")
    assert state["is_play_phase"] and state["resolving"] is False
    assert state["run"]["floor"] >= 2
    m = state["monsters"][0]
    assert {"id", "move_id", "intents", "combat_id", "hittable"} <= set(m)
    hand = state["player"]["hand"]
    assert all({"id", "cost", "type", "playable", "keywords", "vars"} <= set(c) for c in hand)
    assert any("damage_vs" in c for c in hand) and any("block_now" in c for c in hand)
    r = act(game, "end_turn")
    assert r["status"] == "ok" and r["state"]["round"] == 2


def test_default_protocol_after_a_flysts_run(game):
    start(game)
    state = game.start()
    assert state["type"] == "decision" and "legal_actions" in state
    assert state["context"]["flow"] == "auto"
