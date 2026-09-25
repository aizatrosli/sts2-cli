"""Tests for the manual (UI-faithful) flow, legal_actions and get_state."""
import pytest


def legal_names(state):
    return {a["action"] for a in state.get("legal_actions", [])}


def start_manual(game, seed):
    state = game.start(seed=seed, flow="manual")
    assert state.get("type") == "decision", state
    # Neow: pick the first option, then the finished event shows a single Proceed option.
    for _ in range(20):
        dec = state["decision"]
        if dec == "map_select":
            return state
        if dec == "event_choice":
            opts = [o for o in state["options"] if not o.get("is_locked")]
            state = game.act("choose_option", option_index=opts[0]["index"])
        elif dec == "card_select":
            state = game.select_min(state)
        elif dec == "card_reward":
            state = game.act("skip_card_reward")
        elif dec == "bundle_select":
            state = game.act("select_bundle", bundle_index=0)
        else:
            state = game.act("proceed")
    raise AssertionError(f"never reached the map: {state.get('decision')}")


def win_combat(game, state):
    for _ in range(400):
        dec = state.get("decision")
        if dec == "card_select":  # a card or enemy move asked for a choice mid-combat
            state = game.select_min(state)
        elif dec == "combat_play":
            state = game.auto_combat(state)
        else:
            return state
    raise AssertionError("combat did not end")


class TestFlowOption:
    def test_unknown_flow_rejected(self, game):
        state = game.start(seed="flow0", flow="bogus")
        assert state["type"] == "error"

    def test_legal_actions_on_every_decision(self, game):
        state = game.start(seed="flow1")
        assert state["legal_actions"], "auto flow decisions carry legal_actions too"
        for a in state["legal_actions"]:
            assert "action" in a

    def test_get_state_does_not_act(self, game):
        state = start_manual(game, "flow2")
        again = game.send({"cmd": "get_state"})
        assert again["decision"] == state["decision"] == "map_select"
        assert again["choices"] == state["choices"]


class TestNeowProceed:
    def test_finished_event_offers_proceed(self, game):
        state = game.start(seed="neow1", flow="manual")
        assert state["decision"] == "event_choice"
        opts = [o for o in state["options"] if not o.get("is_locked")]
        for _ in range(10):
            state = game.act("choose_option", option_index=opts[0]["index"])
            if state["decision"] != "event_choice" or state.get("is_finished"):
                break
            opts = [o for o in state["options"] if not o.get("is_locked")]
        # Any follow-up selection must be resolved before Proceed shows up.
        while state["decision"] in ("card_select", "card_reward", "bundle_select", "rewards"):
            if state["decision"] == "card_select":
                state = game.select_min(state)
            elif state["decision"] == "card_reward":
                state = game.act("skip_card_reward")
            elif state["decision"] == "bundle_select":
                state = game.act("select_bundle", bundle_index=0)
            else:
                state = game.act("proceed")
        assert state["decision"] == "event_choice"
        assert state["is_finished"] is True
        assert [o.get("is_proceed") for o in state["options"]] == [True]
        state = game.act("choose_option", option_index=0)
        assert state["decision"] == "map_select"


class TestRewardsScreen:
    def _combat_rewards(self, game, seed):
        start_manual(game, seed)
        game.set_player(hp=999, max_hp=999)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        state = win_combat(game, state)
        assert state["decision"] == "rewards", state.get("decision")
        assert state["is_terminal"] is True
        return state

    def test_claim_each_reward_then_proceed(self, game):
        state = self._combat_rewards(game, "rw1")
        types = [r["type"] for r in state["rewards"]]
        assert "gold" in types and "card" in types
        assert {"claim_reward", "proceed"} <= legal_names(state)

        gold_idx = types.index("gold")
        gold_before = state["player"]["gold"]
        amount = state["rewards"][gold_idx]["amount"]
        state = game.act("claim_reward", reward_index=gold_idx)
        assert state["decision"] == "rewards"
        assert state["player"]["gold"] == gold_before + amount
        assert "gold" not in [r["type"] for r in state["rewards"]]

        card_idx = [r["type"] for r in state["rewards"]].index("card")
        deck_before = state["player"]["deck_size"]
        state = game.act("claim_reward", reward_index=card_idx)
        assert state["decision"] == "card_reward"
        state = game.act("select_card_reward", card_index=0)
        assert state["decision"] == "rewards"
        assert state["player"]["deck_size"] == deck_before + 1

        state = game.act("proceed")
        assert state["decision"] == "map_select"

    def test_skipped_card_reward_stays_claimable(self, game):
        state = self._combat_rewards(game, "rw2")
        card_idx = [r["type"] for r in state["rewards"]].index("card")
        state = game.act("claim_reward", reward_index=card_idx)
        assert state["decision"] == "card_reward"
        state = game.act("skip_card_reward")
        assert state["decision"] == "rewards"
        assert "card" in [r["type"] for r in state["rewards"]]

    def test_proceed_leaves_unclaimed_rewards(self, game):
        state = self._combat_rewards(game, "rw3")
        gold_before = state["player"]["gold"]
        state = game.act("proceed")
        assert state["decision"] == "map_select"
        assert state["player"]["gold"] == gold_before


class TestTreasure:
    def test_open_pick_proceed(self, game):
        start_manual(game, "tr1")
        state = game.enter_room("treasure")
        assert state["decision"] == "treasure"
        assert state["chest_opened"] is False
        assert legal_names(state) == {"open_chest"}
        relics_before = len(state["player"]["relics"])

        state = game.act("open_chest")
        while state["decision"] == "rewards":  # extra chest rewards, if any relic grants them
            state = game.act("proceed")
        assert state["decision"] == "treasure" and state["chest_opened"] is True
        assert state["relics"], "chest should offer a relic"
        assert {"pick_relic", "skip_relic", "proceed"} <= legal_names(state)

        state = game.act("pick_relic", relic_index=0)
        while state["decision"] != "treasure":
            state = game.act("skip_select") if state["decision"] == "card_select" else game.act("proceed")
        assert len(state["player"]["relics"]) == relics_before + 1
        assert legal_names(state) == {"proceed"}
        state = game.act("proceed")
        assert state["decision"] == "map_select"

    def test_skip_relic(self, game):
        start_manual(game, "tr2")
        state = game.enter_room("treasure")
        relics_before = len(state["player"]["relics"])
        state = game.act("open_chest")
        while state["decision"] == "rewards":
            state = game.act("proceed")
        state = game.act("skip_relic")
        assert state["decision"] == "treasure" and not state.get("relics")
        state = game.act("proceed")
        assert state["decision"] == "map_select"
        assert len(state["player"]["relics"]) == relics_before


class TestRestSite:
    def test_proceed_only_after_choice(self, game):
        start_manual(game, "rs1")
        game.set_player(hp=30, max_hp=80)
        state = game.enter_room("rest_site")
        assert state["can_proceed"] is False
        assert "proceed" not in legal_names(state)
        assert game.act("proceed")["type"] == "error"

        heal = next(o for o in state["options"] if o["option_id"] == "HEAL")
        state = game.act("choose_option", option_index=heal["index"])
        assert state["decision"] == "rest_site"
        assert state["can_proceed"] is True
        assert state["player"]["hp"] > 30
        state = game.act("proceed")
        assert state["decision"] == "map_select"

    def test_cancelled_smith_keeps_options(self, game):
        start_manual(game, "rs2")
        state = game.enter_room("rest_site")
        smith = next(o for o in state["options"] if o["option_id"] == "SMITH")
        state = game.act("choose_option", option_index=smith["index"])
        assert state["decision"] == "card_select"
        state = game.act("skip_select")
        assert state["decision"] == "rest_site"
        assert state["can_proceed"] is False
        assert len(state["options"]) >= 2


class TestShopAndCrystalSphere:
    @pytest.mark.parametrize("flow", [None, "manual"])
    def test_buy_relic_returns_shop(self, game, flow):
        game.start(seed="shop1", flow=flow)
        game.set_player(gold=9999)
        state = game.enter_room("shop")
        relic = state["relics"][0]
        state = game.act("buy_relic", relic_index=relic["index"])
        assert state["type"] == "decision", state
        assert state["player"]["gold"] < 9999

    @pytest.mark.parametrize("flow", [None, "manual"])
    def test_crystal_sphere_minigame(self, game, flow):
        game.start(seed="cs1", flow=flow)
        game.set_player(gold=500)
        state = game.enter_room("event", event="CRYSTAL_SPHERE")
        state = game.act("choose_option", option_index=0)
        assert state["decision"] == "crystal_sphere"
        assert state["divinations_left"] > 0
        for _ in range(10):
            if state["decision"] != "crystal_sphere":
                break
            x, y = next((x, y) for y, row in enumerate(state["grid"]) for x, v in enumerate(row) if v == "?")
            state = game.act("crystal_sphere_divine", x=x, y=y, tool="big")
        assert state["decision"] != "crystal_sphere"


class TestActSelection:
    """Act 1 is rolled from the seed like the single-player lobby (Overgrowth or Underdocks)."""

    @pytest.mark.parametrize("act1,act_id", [("overgrowth", "OVERGROWTH"), ("underdocks", "UNDERDOCKS")])
    def test_act1_override(self, game, act1, act_id):
        state = game.start(seed="acts1", act1=act1)
        assert state["type"] == "decision", state
        assert state["context"]["act"] == 1
        assert state["context"]["act_id"] == act_id

    def test_unknown_act1_rejected(self, game):
        state = game.start(seed="acts2", act1="hive")
        assert state["type"] == "error"

    def test_random_act1_depends_on_seed(self, game):
        seen = set()
        for i in range(12):
            state = game.start(seed=f"actroll{i}")
            assert state["type"] == "decision", state
            seen.add(state["context"]["act_id"])
        assert seen == {"OVERGROWTH", "UNDERDOCKS"}


class TestRepeatedRuns:
    """RL envs reset many times in one process; every start_run must begin a clean run."""

    def test_same_seed_restarts_identically(self, game):
        first = start_manual(game, "reset1")
        game.start(seed="reset-other", flow="manual")
        again = start_manual(game, "reset1")
        assert again["choices"] == first["choices"]
        assert again["context"] == first["context"]
        assert again["player"] == first["player"]

    def test_restart_mid_combat(self, game):
        state = start_manual(game, "reset2")
        state = game.act("select_map_node", col=state["choices"][0]["col"], row=state["choices"][0]["row"])
        assert state["decision"] == "combat_play", state
        state = start_manual(game, "reset3")
        state = game.act("select_map_node", col=state["choices"][0]["col"], row=state["choices"][0]["row"])
        assert state["decision"] == "combat_play", state
        state = win_combat(game, state)
        assert state["decision"] == "rewards", state
