"""Debug commands (engine started with --debug): a refused command changes nothing."""


def test_set_player_refuses_a_bad_value_before_writing(game):
    game.skip_neow(game.start(seed="dbg1"))
    before = game.send({"cmd": "get_state"})["player"]
    r = game.send({"cmd": "set_player", "hp": 7, "gold": "lots", "relics": ["VAJRA"]})
    assert r["type"] == "error" and "gold" in r["message"], r
    after = game.send({"cmd": "get_state"})["player"]
    assert (after["hp"], after["gold"], [x["id"] for x in after["relics"]]) == \
        (before["hp"], before["gold"], [x["id"] for x in before["relics"]])


def test_room_commands_refused_while_a_selection_is_open(game):
    """Astrolabe's pickup opens a transform selection; entering another room (or obtaining a relic
    that could open its own screen) would leave that screen in front of the new room."""
    game.skip_neow(game.start(seed="dbg2"))
    state = game.send({"cmd": "obtain_relic", "relic": "ASTROLABE"})
    assert state["decision"] == "card_select", state
    for cmd in ({"cmd": "enter_room", "type": "rest_site"}, {"cmd": "obtain_relic", "relic": "VAJRA"},
                {"cmd": "enter_ancient", "event": "DARV"}):
        r = game.send(cmd)
        assert r["type"] == "error" and "a selection is pending" in r["message"], (cmd, r)
    assert game.send({"cmd": "get_state"})["decision"] == "card_select"
