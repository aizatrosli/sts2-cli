"""Tests for the RL wrapper (python/sts2_env.py)."""
import os

import pytest

from sts2_env import (VALID_CHARACTERS, Sts2Env, Sts2EnvBroken, Sts2Error, Sts2VecEnv,
                      expand_legal_actions, play_random, shaped_reward)


@pytest.fixture
def env():
    with Sts2Env(debug=True) as e:
        yield e


def template(n, lo, hi):
    return {"action": "select_cards", "template": True, "num_cards": n, "min_select": lo, "max_select": hi}


class Index:
    """An int-like that is not an int (numpy integers behave the same way)."""

    def __init__(self, i):
        self.i = i

    def __index__(self):
        return self.i


# --- pure helpers --------------------------------------------------------------------------

def test_expand_small_template_into_picks():
    legal = [template(3, 1, 1), {"action": "skip_select"}]
    actions = expand_legal_actions(legal)
    assert actions == [{"action": "skip_select"}] + [
        {"action": "select_cards", "args": {"indices": str(i)}} for i in range(3)]


def test_large_template_stays_last():
    big = template(30, 2, 2)  # 435 picks
    actions = expand_legal_actions([big, {"action": "proceed"}], max_expanded=64)
    assert actions == [{"action": "proceed"}, big]


def test_min_zero_template_skips_empty_pick():
    actions = expand_legal_actions([template(2, 0, 2)])
    assert [a["args"]["indices"] for a in actions] == ["0", "1", "0,1"]


def test_shaped_reward_is_off_by_default():
    prev = {"context": {"total_floor": 1, "act": 1}, "player": {"hp": 50, "max_hp": 100}}
    obs = {"context": {"total_floor": 2, "act": 1}, "player": {"hp": 40, "max_hp": 100}}
    assert shaped_reward()(prev, obs) == 0.0
    assert shaped_reward(floor=1.0, hp=1.0)(prev, obs) == pytest.approx(1.0 - 0.1)


def test_big_template_becomes_sequential_picks():
    e = Sts2Env()
    obs = {"decision": "card_select", "legal_actions": [template(30, 2, 3), {"action": "skip_select"}]}
    e.obs = obs
    info = e._info(obs)
    assert info["legal_actions"][0] == {"action": "skip_select"}
    assert len(info["legal_actions"]) == 1 + 30 and all(info["action_mask"])
    obs2, reward, terminated, truncated, info = e.step(info["legal_actions"].index(
        {"action": "pick_card", "args": {"index": 4}}))
    assert obs2 is obs and info["selected"] == [4]
    assert {"action": "confirm_selection"} not in info["legal_actions"]  # min 2
    assert {"action": "skip_select"} not in info["legal_actions"]
    assert len(info["legal_actions"]) == 29
    assert e._proc is None  # picks stay in the wrapper until the selection is sent
    with pytest.raises(IndexError):
        e.step(99)
    with pytest.raises(TypeError):
        e.step("0")


# --- against the engine --------------------------------------------------------------------

def test_random_seed_is_reported_and_reproducible(env):
    obs, info = env.reset()
    assert info["seed"] and info["seed"] == obs["context"]["seed"]
    assert info["action_mask"] == [True] * len(info["legal_actions"])
    again, _ = env.reset(seed=info["seed"])
    assert again["options"] == obs["options"]


def test_reset_options_and_legacy_keywords(env):
    obs, info = env.reset(seed=1, options={"character": "Defect", "ascension": 3, "act1": "underdocks"})
    assert (info["character"], info["ascension"], info["act1"]) == ("Defect", 3, "underdocks")
    assert obs["context"]["act_id"] == "UNDERDOCKS"
    obs, info = env.reset("Silent", "abc", 0)
    assert (info["character"], info["seed"], info["flow"]) == ("Silent", "ABC", "manual")


def test_int_like_index_and_expanded_selection(env):
    env.reset(seed=2)
    env.send({"cmd": "enter_room", "type": "rest"})
    obs = env.refresh()
    smith = next(o["index"] for o in obs["options"] if o["option_id"] == "SMITH")
    i = next(i for i, a in enumerate(env.legal_actions())
             if a["action"] == "choose_option" and a["args"]["option_index"] == smith)
    obs, _, _, _, info = env.step(Index(i))
    assert obs["decision"] == "card_select"
    picks = [a for a in info["legal_actions"] if a["action"] == "select_cards"]
    assert len(picks) == len(obs["cards"]) and all(info["action_mask"])
    obs, *_ = env.step(info["legal_actions"].index(picks[0]))
    assert obs["decision"] == "rest_site"


def test_sequential_selection_against_engine(env):
    env.reset(seed=6)
    env.send({"cmd": "set_player", "deck": ["STRIKE_IRONCLAD"] * 12})
    env.send({"cmd": "enter_room", "type": "rest"})
    env.max_expanded = 1  # force the sequential path on a one-card choice
    obs = env.refresh()
    smith = next(o["index"] for o in obs["options"] if o["option_id"] == "SMITH")
    obs, *_, info = env.step({"action": "choose_option", "args": {"option_index": smith}})
    assert obs["decision"] == "card_select"
    assert {"action": "pick_card", "args": {"index": 3}} in info["legal_actions"]
    obs, *_, info = env.step({"action": "pick_card", "args": {"index": 3}})  # max 1: sent at once
    assert obs["decision"] == "rest_site"
    assert sum(c["upgraded"] for c in obs["player"]["deck"]) == 1


def test_rejected_action_raises_and_keeps_state(env):
    obs, _ = env.reset(seed=3)
    with pytest.raises(Sts2Error):
        env.step({"action": "end_turn"})
    assert env.refresh()["decision"] == obs["decision"]


def test_engine_death_needs_reset(env):
    env.reset(seed=4)
    env._proc.kill()
    with pytest.raises(Sts2EnvBroken):
        env.step(0)
    with pytest.raises(Sts2EnvBroken):
        env.step(0)
    obs, info = env.reset(seed=4)
    assert env.step(0)[0]["type"] == "decision"


def test_close_ends_the_engine():
    e = Sts2Env()
    e.reset(seed=5)
    proc = e._proc
    e.close()
    assert proc.poll() is not None
    if os.name == "posix":
        with pytest.raises(ProcessLookupError):
            os.killpg(proc.pid, 0)


def test_engine_echoes_request_id(game):
    game.start(seed="rid")
    assert game.send({"cmd": "get_state", "request_id": 7})["request_id"] == 7
    assert game.send({"cmd": "get_state", "request_id": "x"})["request_id"] == "x"


def test_vec_env_autoreset():
    with Sts2VecEnv(2, max_steps=3) as venv:
        obs, infos = venv.reset(seed=10)
        assert [i["seed"] for i in infos] == ["10", "11"]
        for _ in range(3):
            obs, rewards, terminated, truncated, infos = venv.step([0, 0])
        assert truncated == [True, True]
        assert all("final_observation" in i and i["steps"] == 0 for i in infos)


@pytest.mark.parametrize("character", VALID_CHARACTERS)
def test_random_agent_actions_are_accepted(character):
    with Sts2Env(debug=True, max_steps=400) as e:
        r = play_random(e, character, f"wrap_{character}", god=True)
    assert r["rejected"] == 0
