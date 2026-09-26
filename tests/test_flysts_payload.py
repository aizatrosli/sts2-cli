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


def rest_options(state):
    return [("LEAVE" if o["leave"] else o["option_id"], o["enabled"]) for o in state["options"]]


def rest_site(game):
    start(game)
    game.send({"cmd": "enter_room", "type": "rest_site"})
    state = game.send({"cmd": "flysts_state"})["state"]
    assert rest_options(state) == [("HEAL", True), ("SMITH", True), ("LEAVE", False)]
    return state


def test_rest_site_after_smith_keeps_greyed_buttons(game):
    """The upgrade grid closing re-activates the room with no options left: Proceed enables at
    once while HideChoices' greyed buttons stay (NRestSiteRoom.OnActiveScreenUpdated); every
    logged live read after a Smith shows them."""
    rest_site(game)
    r = act(game, "choose_rest_option", index=1)
    assert r["status"] == "ok" and r["state"]["state_type"] == "deck_upgrade"
    r = act(game, "select_deck_card", card_index=0)
    if r["state"]["state_type"] == "deck_upgrade":
        r = act(game, "confirm")
    assert r["status"] == "ok" and r["state"]["state_type"] == "rest_site"
    assert rest_options(r["state"]) == [("HEAL", False), ("SMITH", False), ("LEAVE", True)]
    assert act(game, "choose_rest_option", index=0)["status"] == "error"
    assert act(game, "leave_rest_site")["state"]["state_type"] == "map"


def test_rest_site_after_heal_lists_only_proceed(game):
    rest_site(game)
    r = act(game, "choose_rest_option", index=0)
    assert r["status"] == "ok" and rest_options(r["state"]) == [("LEAVE", True)]


def test_smith_with_nothing_to_upgrade_is_listed_but_refused(game):
    """A model-disabled option keeps an enabled, unclickable button (NRestSiteButton): the mod
    lists it enabled and choosing it fails, as live (sight-live ep 44 t=117-119)."""
    start(game)
    for _ in range(30):
        game.send({"cmd": "enter_room", "type": "rest_site"})
        state = game.send({"cmd": "flysts_state"})["state"]
        assert rest_options(state) == [("HEAL", True), ("SMITH", True), ("LEAVE", False)]
        r = act(game, "choose_rest_option", index=1)
        if r["status"] == "error":
            break
        r = act(game, "select_deck_card", card_index=0)
        if r["state"]["state_type"] == "deck_upgrade":
            act(game, "confirm")
    else:
        pytest.fail("the deck never ran out of upgradable cards")
    assert "is disabled" in r["error"] and r["state"] == state


def test_content_catalog(game):
    cat = game.send({"cmd": "content_catalog"})
    assert cat["type"] == "content_catalog"
    assert len(cat["characters"]) == 5
    ids = {c["id"] for c in cat["cards"]}
    assert {"STRIKE_IRONCLAD", "BASH"} <= ids
    assert "NUTRITIOUS_OYSTER" in {r["id"] for r in cat["relics"]}
    assert "NEOW" in {e["id"] for e in cat["events"]}
    assert all({"id", "act", "room_type"} <= set(e) for e in cat["encounters"])


def test_fake_merchant_lists_no_options(game):
    """Its custom layout (NFakeMerchant) is no NEventLayout: the mod reads no option buttons,
    not even a finished event's Proceed (docs/flysts_payload.md, known differences)."""
    start(game)
    game.send({"cmd": "enter_room", "type": "event", "event": "FAKE_MERCHANT"})
    state = game.send({"cmd": "flysts_state"})["state"]
    assert state["state_type"] == "event" and state["event_id"] == "FAKE_MERCHANT"
    assert state["options"] == []
    r = act(game, "choose_event_option", index=0)
    assert r["status"] == "error" and r["state"] == state


def test_kaiser_crab_fight_runs(game):
    """Its arms (Crusher, Rocket) animate the boss body in the combat room's background; headless
    that threw on spawn and the fight ended at once (live FMT1751 floor 33, 2026-09-26)."""
    start(game)
    game.send({"cmd": "set_player", "hp": 9999, "max_hp": 9999})
    game.send({"cmd": "enter_room", "type": "combat", "encounter": "KAISER_CRAB_BOSS"})
    state = game.send({"cmd": "flysts_state"})["state"]
    assert state["state_type"] == "boss" and state["encounter_id"] == "KAISER_CRAB_BOSS"
    assert [m["id"] for m in state["monsters"]] == ["CRUSHER", "ROCKET"]
    hp = state["player"]["hp"]
    for _ in range(4):
        state = act(game, "end_turn")["state"]
    assert state["state_type"] == "boss" and state["round"] == 5 and state["player"]["hp"] < hp


def test_lords_parasol_buys_the_whole_shop(game):
    """Entering a shop with Lord's Parasol buys every card, relic and potion for free, then opens
    the card removal (not cancelable). Headless the relic loop threw on the top bar (2026-09-26).
    A bought relic can open its own screens first (FLYS1's shop sells Orrery: 5 card rewards)."""
    start(game)
    game.send({"cmd": "set_player", "relics": ["BURNING_BLOOD", "LORDS_PARASOL"], "gold": 0})
    game.send({"cmd": "enter_room", "type": "shop"})
    state = game.send({"cmd": "flysts_state"})["state"]
    removal = False
    for _ in range(40):
        st = state["state_type"]
        if st == "shop":
            break
        if st == "menu":
            r = act(game, "menu_select", option=state["options"][0])
        elif st == "card_reward":
            r = act(game, "select_card_reward", card_index=0)
        elif st == "deck_card_select":
            removal = True
            r = act(game, "select_deck_card", card_index=0)
            if r["state"]["state_type"] == "deck_card_select":
                r = act(game, "confirm")
        else:
            pytest.fail(f"unexpected state {st}")
        assert r["status"] == "ok", r
        state = r["state"]
    assert state["state_type"] == "shop" and removal
    assert not [i for i in state["items"] if i.get("kind") in ("card", "relic", "potion") and i.get("in_stock")]


def test_trial_double_down_changes_nothing(game):
    """Double Down opens the abandon-run popup, which the mod never sees or confirms: live the
    page stays (Accept / Double Down) and Accept then runs the trial (captured on the game)."""
    start(game)
    game.send({"cmd": "enter_room", "type": "event", "event": "TRIAL"})
    assert act(game, "choose_event_option", index=1)["status"] == "ok"  # Reject
    page = game.send({"cmd": "flysts_state"})["state"]
    assert [o["text_key"].split(".")[-1] for o in page["options"]] == ["ACCEPT", "DOUBLE_DOWN"]
    r = act(game, "choose_event_option", index=1)  # Double Down
    assert r["status"] == "ok" and r["state"]["state_type"] == "event"
    assert r["state"]["options"] == page["options"] and r["state"]["player"] == page["player"]
    r = act(game, "choose_event_option", index=0)  # Accept
    assert [o["text_key"].split(".")[-1] for o in r["state"]["options"]] == ["GUILTY", "INNOCENT"]
