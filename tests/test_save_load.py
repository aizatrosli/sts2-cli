"""Regression tests for native save/load behavior."""

import pytest

from conftest import Game


@pytest.mark.engine
def test_load_map_save_does_not_retrigger_neow(tmp_path):
    save_path = tmp_path / "map_select.save"

    game = Game()
    try:
        state = game.start(seed="sl1")
        state = game.skip_neow(state)
        assert state["decision"] == "map_select"

        save_result = game.send({"cmd": "write_continue_save", "path": str(save_path)})
        assert save_result["type"] == "save_result"
        assert save_result["success"] is True
    finally:
        game.close()

    game = Game()
    try:
        state = game.send({"cmd": "load_save", "path": str(save_path)})
        assert state["decision"] == "map_select"
    finally:
        game.close()


def _choice_coords(state):
    return sorted((ch["col"], ch["row"], ch["type"]) for ch in state["choices"])


def _enemy_summary(state):
    return [(e["name"], e["max_hp"]) for e in state["enemies"]]


def _enter_first_combat(game, seed):
    state = game.start(seed=seed)
    state = game.skip_neow(state)
    assert state["decision"] == "map_select"
    pick = next(ch for ch in state["choices"] if ch["type"] == "Monster")
    state = game.act("select_map_node", col=pick["col"], row=pick["row"])
    assert state["decision"] == "combat_play"
    return state


@pytest.mark.engine
def test_map_save_reloads_same_choices_without_neow(tmp_path):
    save_path = tmp_path / "map.save"

    game = Game()
    try:
        state = game.skip_neow(game.start(seed="sl3"))
        assert state["decision"] == "map_select"
        choices = _choice_coords(state)
        floor = state["context"]["floor"]
        assert game.send({"cmd": "write_continue_save", "path": str(save_path)})["success"] is True
    finally:
        game.close()

    game = Game()
    try:
        state = game.send({"cmd": "load_save", "path": str(save_path)})
        assert state["decision"] == "map_select", state
        assert _choice_coords(state) == choices
        assert state["context"]["floor"] == floor
    finally:
        game.close()


@pytest.mark.engine
def test_mid_combat_save_resumes_same_encounter(tmp_path):
    save_path = tmp_path / "combat.save"

    game = Game()
    try:
        state = _enter_first_combat(game, "sl4")
        enemies = _enemy_summary(state)
        floor = state["context"]["floor"]
        # Change the combat state before saving: the save must still resume this room.
        state = game.act("end_turn")
        assert state["decision"] == "combat_play"
        result = game.send({"cmd": "write_continue_save", "path": str(save_path)})
        assert result["success"] is True
    finally:
        game.close()

    # Load twice (re-saving mid-combat after the first load) to prove the resume is stable.
    for _ in range(2):
        game = Game()
        try:
            state = game.send({"cmd": "load_save", "path": str(save_path)})
            assert state["decision"] == "combat_play", state
            assert _enemy_summary(state) == enemies
            assert state["context"]["floor"] == floor
            assert state["round"] == 1
            result = game.send({"cmd": "write_continue_save", "path": str(save_path)})
            assert result["success"] is True
        finally:
            game.close()


@pytest.mark.engine
def test_mid_combat_save_after_leaving_room_is_map_save(tmp_path):
    """Once the room is finished and the map is shown, a save offers the next nodes."""
    save_path = tmp_path / "after_combat.save"

    game = Game()
    try:
        state = _enter_first_combat(game, "sl5")
        state = game.auto_play_combat(state)
        for _ in range(20):
            dec = state.get("decision")
            if dec in ("map_select", "game_over"):
                break
            if dec == "card_reward":
                state = game.act("skip_card_reward")
            elif dec == "card_select" and state.get("min_select", 0) == 0:
                state = game.act("skip_select")
            else:
                state = game.act("proceed")
        if state.get("decision") != "map_select":
            pytest.skip(f"did not reach the map after combat: {state.get('decision')}")
        choices = _choice_coords(state)
        assert game.send({"cmd": "write_continue_save", "path": str(save_path)})["success"] is True
    finally:
        game.close()

    game = Game()
    try:
        state = game.send({"cmd": "load_save", "path": str(save_path)})
        assert state["decision"] == "map_select", state
        assert _choice_coords(state) == choices
    finally:
        game.close()


@pytest.mark.engine
def test_load_pre_neow_save_preserves_neow_choice(tmp_path):
    save_path = tmp_path / "pre_neow.save"

    game = Game()
    try:
        state = game.start(seed="sl2")
        assert state["decision"] == "event_choice"

        save_result = game.send({"cmd": "write_continue_save", "path": str(save_path)})
        assert save_result["type"] == "save_result"
        assert save_result["success"] is True
    finally:
        game.close()

    game = Game()
    try:
        state = game.send({"cmd": "load_save", "path": str(save_path)})
        assert state["decision"] == "event_choice"
    finally:
        game.close()


@pytest.mark.engine
def test_unknown_node_save_resumes_rolled_room(tmp_path):
    """A "?" node that rolled a non-event room comes back as that room: load_save must keep
    the run's "?" odds (EnterAct resets them to base, which rolls an event instead)."""
    from test_coverage import step

    save_path = str(tmp_path / "unknown.save")
    game = Game()
    try:
        state = game.send({"cmd": "start_run", "character": "Ironclad", "seed": "sw1", "flow": "manual"})
        game.set_player(hp=9999, max_hp=9999)
        state = game.send({"cmd": "get_state"})
        for _ in range(2500):
            was_unknown = state.get("decision") == "map_select" and state["choices"][0]["type"] == "Unknown"
            state = step(game, state)
            if was_unknown and state["context"]["room_type"] != "Event":
                break
        else:
            pytest.fail("no \"?\" node rolled a non-event room")
        room = (state["decision"], state["context"]["room_type"], state["context"]["floor"])
        assert game.send({"cmd": "write_continue_save", "path": save_path})["success"]
    finally:
        game.close()

    game = Game()
    try:
        state = game.send({"cmd": "load_save", "path": save_path, "flow": "manual"})
        assert (state["decision"], state["context"]["room_type"], state["context"]["floor"]) == room
    finally:
        game.close()
