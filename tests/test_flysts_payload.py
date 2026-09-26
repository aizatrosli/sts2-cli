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


def start(game, seed="FLYS1", mode="custom", character="Ironclad"):
    cmd = {"cmd": "start_run", "payload": "flysts", "character": character, "game_mode": mode, "profile": PROFILE}
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
    assert "SHARP" in cat["enchantments"] and "HEXED" in cat["afflictions"]
    assert not [x for x in cat["enchantments"] + cat["afflictions"] if x.startswith("MOCK")]
    events = {e["id"]: e for e in cat["events"]}
    assert events["FAKE_MERCHANT"]["conditional"] and not events["NEOW"]["conditional"]


def test_content_catalog_event_unlocks_follow_the_profile(game, tmp_path):
    """Darv is a shared Ancient behind DARV_EPOCH (UnlockState.SharedAncients), Trash Heap an
    Event1Epoch event (ActModel.GenerateRooms)."""
    import json
    progress = json.load(open(PROFILE))
    for e in progress["epochs"]:
        if e["id"] in ("DARV_EPOCH", "EVENT1_EPOCH"):
            e["state"] = "not_obtained"
    locked = tmp_path / "progress.save"
    locked.write_text(json.dumps(progress))
    cat = game.send({"cmd": "content_catalog", "profile": str(locked)})
    events = {e["id"]: e["unlocked"] for e in cat["events"]}
    assert events["DARV"] is False and events["TRASH_HEAP"] is False
    assert events["OROBAS"] is True and events["NEOW"] is True


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


def _combat_view(state):
    player = state["player"]
    return ([(p["id"], p["amount"]) for p in player["powers"]],
            [[(p["id"], p["amount"]) for p in m["powers"]] for m in state["monsters"]])


def test_turn_start_hook_selection_opens_after_the_other_turn_start_hooks(game):
    """Gambling Chip's prompt opens once the turn start has run on (captured on the game): the
    hook signals its player choice, so Brimstone's Strength is already applied at the prompt and
    the prompt's action is running (`resolving`)."""
    start(game)
    game.send({"cmd": "set_player", "relics": ["BURNING_BLOOD", "GAMBLING_CHIP", "BRIMSTONE"]})
    game.send({"cmd": "enter_room", "type": "combat", "encounter": "NIBBITS_WEAK"})
    state = game.send({"cmd": "flysts_state"})["state"]
    assert state["hand_selection"] == "simple_select" and state["resolving"] is True
    assert _combat_view(state) == ([("STRENGTH_POWER", 2)], [[("STRENGTH_POWER", 1)]])
    r = act(game, "confirm")
    assert r["status"] == "ok" and r["state"]["is_play_phase"] and not r["state"]["hand_selection"]


def test_potion_used_during_a_selection_is_queued(game):
    """The mod enqueues a usable potion on any screen; the game runs it after the open selection
    (captured on the game: Strength Potion on Attack Potion's choose-a-card screen)."""
    start(game)
    game.send({"cmd": "set_player", "potions": ["ATTACK_POTION", "STRENGTH_POTION"]})
    game.send({"cmd": "enter_room", "type": "combat", "encounter": "SHRINKER_BEETLE_WEAK"})
    assert act(game, "use_potion", slot=0)["state"]["state_type"] == "choose_a_card"
    r = act(game, "use_potion", slot=1)
    assert r["status"] == "ok" and r["state"]["state_type"] == "choose_a_card"
    slot = r["state"]["player"]["potion_slots_detail"][1]
    assert slot["id"] == "STRENGTH_POTION" and slot["usable"] is False  # queued
    r = act(game, "choose_card", card_index=0)
    assert r["state"]["player"]["potion_slots_detail"][1]["id"] is None
    assert ("STRENGTH_POWER", 2) in _combat_view(r["state"])[0]


def test_lobby_refuses_a_locked_character_and_ascension_above_max(game, tmp_path):
    """What the mod's lobby refuses (MenuAutomation): a locked character, and an ascension above
    the character's max (0 until its ascension epoch is revealed)."""
    import json
    progress = json.load(open(PROFILE))
    for e in progress["epochs"]:
        if e["id"] == "SILENT1_EPOCH":
            e["state"] = "not_obtained"
    for c in progress["character_stats"]:
        c["max_ascension"] = 0
    locked = tmp_path / "progress.save"
    locked.write_text(json.dumps(progress))
    base = {"cmd": "start_run", "payload": "flysts", "seed": "LOCK1", "profile": str(locked)}
    r = game.send({**base, "character": "Silent"})
    assert r["type"] == "error" and r["message"] == "Character 'Silent' is locked"
    r = game.send({**base, "character": "Ironclad", "ascension": 1})
    assert r["type"] == "error" and r["message"] == "Ascension 1 out of range (max unlocked for this character: 0)"
    assert game.send({**base, "character": "Ironclad"})["type"] == "flysts"


def test_debug_commands_report_their_errors_and_change_nothing(game):
    before = start(game)["state"]
    for cmd in ({"cmd": "enter_room", "type": "combat", "encounter": "NOT_AN_ENCOUNTER"},
                {"cmd": "set_player", "relics": ["NOT_A_RELIC"]},
                {"cmd": "set_player", "deck": ["NOT_A_CARD", "BASH"], "gold": 1},
                {"cmd": "set_player", "potions": ["NOT_A_POTION"]}):
        r = game.send(cmd)
        assert r["type"] == "error" and "Unknown" in r["message"], (cmd, r)
    assert game.send({"cmd": "flysts_state"})["state"] == before


OSTY_CARDS = ["POKE", "UNLEASH", "SNAP", "PROTECTOR", "SQUEEZE"]


def _osty_hand(game, relics):
    start(game, character="Necrobinder")
    game.send({"cmd": "set_player", "relics": relics, "deck": OSTY_CARDS})
    game.send({"cmd": "enter_room", "type": "combat", "encounter": "SHRINKER_BEETLE_WEAK"})
    state = game.send({"cmd": "flysts_state"})["state"]
    assert len(state["monsters"]) == 1
    return state, {c["id"]: c for c in state["player"]["hand"]}


def test_osty_attacks_are_dealt_by_osty(game):
    """An Osty attack's damage_vs has Osty as the dealer (OstyDamageVar / CalculatedDamageVar
    FromOsty UpdateCardPreview): the player's Strength (Brimstone) doesn't count, and against a
    single enemy with no damage-taken modifiers it equals the card's own preview value."""
    plain_state, plain = _osty_hand(game, ["BOUND_PHYLACTERY"])
    strong_state, strong = _osty_hand(game, ["BOUND_PHYLACTERY", "BRIMSTONE"])
    assert ("STRENGTH_POWER", 2) in _combat_view(strong_state)[0]
    assert set(plain) == set(OSTY_CARDS)
    for cid in OSTY_CARDS:
        card = strong[cid]
        preview = card["vars"].get("calculateddamage", card["vars"].get("ostydamage"))
        assert card["damage_vs"] == [preview] == plain[cid]["damage_vs"], (cid, card, plain[cid])
    assert strong["POKE"]["damage_vs"] == [6]


def test_osty_attacks_without_osty(game):
    """No Osty in combat (no Bound Phylactery): the preview still runs, dealer null, like the game."""
    _, hand = _osty_hand(game, [])
    for cid in OSTY_CARDS:
        assert isinstance(hand[cid].get("damage_vs"), list), (cid, hand[cid])


def _relics(state):
    return [r["id"] for r in state["player"]["relics"]]


def test_enter_ancient_forces_an_option_like_the_console(game):
    """`enter_ancient` is the game's `ancient <id> <choice>` console command (DebugOption: option 0
    becomes the first possible option whose text key contains the choice, act filters aside)."""
    start(game)
    r = game.send({"cmd": "enter_ancient", "event": "DARV", "option": "VELVET_CHOKER"})
    assert r["state"]["event_id"] == "DARV" and r["state"]["options"][0]["relic"] == "VELVET_CHOKER"
    r = game.send({"cmd": "enter_ancient", "event": "DARV", "option": "NOPE"})
    assert r["type"] == "error" and r["message"] == "Invalid ancient option for DARV: NOPE"
    assert game.send({"cmd": "enter_ancient", "event": "TRIAL"})["type"] == "error"  # not an Ancient


def test_touch_of_orobas_upgrades_the_starter_then_gives_circlet(game):
    """TouchOfOrobas.cs: Burning Blood -> Black Blood; a second Touch finds no mapping for a
    starter that is already upgraded and gives Circlet."""
    start(game)
    game.send({"cmd": "enter_ancient", "event": "OROBAS", "option": "TOUCH_OF_OROBAS"})
    state = act(game, "choose_event_option", index=0)["state"]
    assert "BLACK_BLOOD" in _relics(state) and "BURNING_BLOOD" not in _relics(state)
    game.send({"cmd": "enter_ancient", "event": "OROBAS", "option": "TOUCH_OF_OROBAS"})
    state = act(game, "choose_event_option", index=0)["state"]
    assert "CIRCLET" in _relics(state) and "BLACK_BLOOD" not in _relics(state)


def test_obtain_relic_runs_its_pickup(game):
    """`obtain_relic` is `relic add` (RelicCmd.Obtain): Astrolabe's pickup opens its transform."""
    start(game)
    r = game.send({"cmd": "obtain_relic", "relic": "ASTROLABE"})
    assert r["state"]["state_type"] == "deck_transform" and "ASTROLABE" in _relics(r["state"])
    assert game.send({"cmd": "obtain_relic", "relic": "NOT_A_RELIC"})["message"] == "Unknown relic: NOT_A_RELIC"


def test_wongo_badge_at_2000_profile_points(game, tmp_path):
    """WelcomeToWongos.CheckObtainWongoBadge: a buy that takes the profile's points to 2000."""
    import json
    progress = json.load(open(PROFILE))
    progress["wongo_points"] = 1999
    prof = tmp_path / "progress.save"
    prof.write_text(json.dumps(progress))
    game.send({"cmd": "start_run", "payload": "flysts", "character": "Ironclad", "seed": "WONGO1",
               "game_mode": "custom", "profile": str(prof)})
    game.send({"cmd": "set_player", "gold": 300})
    state = game.send({"cmd": "enter_room", "type": "event", "event": "WELCOME_TO_WONGOS"})["state"]
    idx = [o["text_key"].split(".")[-1] for o in state["options"]].index("BARGAIN_BIN")
    state = act(game, "choose_event_option", index=idx)["state"]
    assert "WONGO_CUSTOMER_APPRECIATION_BADGE" in _relics(state)


def test_add_card_is_the_card_console_command(game):
    start(game, character="Necrobinder")
    game.send({"cmd": "enter_room", "type": "combat", "encounter": "SHRINKER_BEETLE_WEAK"})
    r = game.send({"cmd": "add_card", "card": "POKE"})
    assert r["state"]["player"]["hand"][-1]["id"] == "POKE" and r["state"]["player"]["hand"][-1]["damage_vs"]
    assert game.send({"cmd": "add_card", "card": "POKE", "pile": "Nowhere"})["type"] == "error"
    assert game.send({"cmd": "add_card", "card": "NOT_A_CARD"})["message"] == "Unknown card: NOT_A_CARD"
