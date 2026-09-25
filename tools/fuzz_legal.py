#!/usr/bin/env python3
"""Lockstep fuzzer for the legal_actions contract.

Process A random-walks using only `legal_actions`; any rejection there is a soundness bug.
Process B follows A's exact command prefix. At each decision B is probed with actions that are
NOT listed (out-of-range indices, wrong-phase actions, bad select_cards sets, map teleports):

  * accepted unlisted action -> violation; B is rebuilt by replaying A's prefix, and every
    replayed response must match A's byte-for-byte (determinism check);
  * rejected unlisted action -> B's get_state must still equal A's observation (a rejected
    action must not change anything).

Usage: python3 tools/fuzz_legal.py [--character Silent] [--seed s] [--flow manual] [--steps 200]
Exit status is 1 when any violation was found. Findings are printed as JSON lines.
"""
import argparse
import json
import os
import random
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "tests"))
from conftest import Game  # noqa: E402


def canon(resp):
    return json.dumps(resp, sort_keys=True)


def key(action):
    return json.dumps({"action": action["action"], "args": action.get("args") or {}}, sort_keys=True)


def template_ok(tmpl, indices):
    """True when `indices` is a valid fill of a select_cards template."""
    toks = indices.split(",") if indices else []
    try:
        idx = [int(t) for t in toks]
    except ValueError:
        return False
    return (len(set(idx)) == len(idx) and all(0 <= i < tmpl["num_cards"] for i in idx)
            and (tmpl.get("min_select") or 0) <= len(idx) <= (tmpl.get("max_select") or 0))


def fill_template(tmpl, rng):
    n = tmpl["num_cards"]
    lo = tmpl.get("min_select") or 0
    hi = min(tmpl.get("max_select") or 0, n)
    k = rng.randint(lo, max(lo, hi))
    return {"action": "select_cards", "args": {"indices": ",".join(map(str, sorted(rng.sample(range(n), k))))}}


def probes(state, map_nodes):
    """Candidate actions for a decision, listed or not."""
    hand = state.get("hand") or []
    enemies = state.get("enemies") or []
    potions = (state.get("player") or {}).get("potions") or []
    out = [{"action": "select_map_node", "args": {"col": n["col"], "row": n["row"]}} for n in map_nodes]
    for i in range(len(hand) + 1):
        out.append({"action": "play_card", "args": {"card_index": i}})
        for t in (-1, 0, 1, len(enemies)):
            out.append({"action": "play_card", "args": {"card_index": i, "target_index": t}})
    for i in range(max(3, len(potions)) + 1):
        out.append({"action": "use_potion", "args": {"potion_index": i}})
        out.append({"action": "use_potion", "args": {"potion_index": i, "target_index": 0}})
        out.append({"action": "discard_potion", "args": {"potion_index": i}})
    for name, arg, n in (("choose_option", "option_index", len(state.get("options") or [])),
                         ("select_card_reward", "card_index", len(state.get("cards") or [])),
                         ("select_card_reward_alternative", "alternative_index", len(state.get("alternatives") or [])),
                         ("select_bundle", "bundle_index", len(state.get("bundles") or [])),
                         ("claim_reward", "reward_index", len(state.get("rewards") or [])),
                         ("pick_relic", "relic_index", len(state.get("relics") or [])),
                         ("buy_card", "card_index", len(state.get("cards") or [])),
                         ("buy_relic", "relic_index", len(state.get("relics") or [])),
                         ("buy_potion", "potion_index", len(state.get("potions") or []))):
        for i in {-1, 0, 1, n - 1, n, n + 3}:
            out.append({"action": name, "args": {arg: i}})
    for name in ("end_turn", "skip_card_reward", "skip_select", "proceed", "leave_room",
                 "remove_card", "open_chest", "skip_relic"):
        out.append({"action": name})
    nc = len(state.get("cards") or []) if state.get("decision") == "card_select" else 3
    mx = state.get("max_select") or 0
    for s in ("", "0,0", "-1", str(nc), "99", "x", "0", ",".join(map(str, range(nc))),
              ",".join(map(str, range(min(nc, mx + 1))))):
        out.append({"action": "select_cards", "args": {"indices": s}})
    for x, y, tool in ((0, 0, "big"), (0, 0, "small"), (20, 20, "small"), (0, 0, "hammer")):
        out.append({"action": "crystal_sphere_divine", "args": {"x": x, "y": y, "tool": tool}})
    return out


class Fuzzer:
    def __init__(self, args):
        self.args = args
        self.rng = random.Random(f"{args.character}-{args.seed}-{args.flow}")
        self.findings = []
        self.history = []     # actions sent to A
        self.responses = []   # A's responses, canonical, index = step

    def boot(self):
        game = Game()
        state = game.send({"cmd": "start_run", "character": self.args.character, "seed": self.args.seed,
                           "ascension": self.args.ascension, "flow": self.args.flow})
        if self.args.god:
            game.send({"cmd": "set_player", "max_hp": 9999, "hp": 9999})
            state = game.send({"cmd": "get_state"})
        return game, state

    def report(self, kind, **info):
        info["kind"] = kind
        self.findings.append(info)
        print(json.dumps(info)[:2000], flush=True)

    def rebuild(self, old):
        old.close()
        game, state = self.boot()
        if canon(state) != self.responses[0]:
            self.report("nondeterminism", step=0)
        for i, action in enumerate(self.history):
            resp = game.send({"cmd": "action", **action})
            if canon(resp) != self.responses[i + 1]:
                self.report("nondeterminism", step=i + 1, action=action)
                break
        return game

    def run(self):
        a, state = self.boot()
        b, state_b = self.boot()
        self.responses.append(canon(state))
        if canon(state_b) != canon(state):
            self.report("nondeterminism", step=0)
        step = probes_sent = 0
        limit = getattr(self.args, "max_violations", 0)
        while step < self.args.steps and state.get("type") == "decision" and state.get("decision") != "game_over":
            if limit and len(self.findings) >= limit:
                break
            decision = state["decision"]
            legal = state.get("legal_actions") or []
            listed = {key(x) for x in legal if not x.get("template")}
            tmpl = next((x for x in legal if x.get("template")), None)

            map_nodes = []
            if self.rng.random() < 0.3:
                m = b.send({"cmd": "get_map"})
                reachable = {(c["col"], c["row"]) for c in state.get("choices") or []}
                map_nodes = [n for row in m.get("rows") or [] for n in row
                             if (n["col"], n["row"]) not in reachable][:3]
            candidates = [p for p in probes(state, map_nodes) if key(p) not in listed
                          and not (tmpl and p["action"] == "select_cards" and template_ok(tmpl, p["args"]["indices"]))]
            self.rng.shuffle(candidates)
            for probe in candidates[:self.args.probes]:
                probes_sent += 1
                resp = b.send({"cmd": "action", **probe})
                if resp.get("type") != "error":
                    self.report("accepted_unlisted", step=step, decision=decision, action=probe,
                                result=resp.get("decision"))
                    b = self.rebuild(b)
                elif canon(b.send({"cmd": "get_state"})) != canon(state):
                    self.report("rejected_action_changed_state", step=step, decision=decision, action=probe,
                                message=resp.get("message"))
                    b = self.rebuild(b)

            if not legal:
                self.report("no_legal_actions", step=step, decision=decision)
                break
            action = self.rng.choice(legal)
            if action.get("template"):
                action = fill_template(action, self.rng)
            else:
                action = {"action": action["action"], **({"args": action["args"]} if "args" in action else {})}
            state = a.send({"cmd": "action", **action})
            if state.get("type") == "error":
                self.report("listed_action_rejected", step=step, decision=decision, action=action,
                            message=state.get("message"))
                break
            resp_b = b.send({"cmd": "action", **action})
            self.history.append(action)
            self.responses.append(canon(state))
            if canon(resp_b) != canon(state):
                self.report("nondeterminism", step=step + 1, action=action)
                b = self.rebuild(b)
            step += 1
        a.close()
        b.close()
        return {"steps": step, "probes": probes_sent, "violations": len(self.findings)}


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--character", default="Silent")
    ap.add_argument("--seed", default="fuzz")
    ap.add_argument("--flow", default="manual", choices=["manual", "auto"])
    ap.add_argument("--ascension", type=int, default=0)
    ap.add_argument("--god", action="store_true")
    ap.add_argument("--steps", type=int, default=200)
    ap.add_argument("--probes", type=int, default=12, help="unlisted probes per decision")
    ap.add_argument("--max-violations", type=int, default=0, help="stop after this many (0 = no limit)")
    args = ap.parse_args()
    summary = Fuzzer(args).run()
    print(json.dumps({"summary": summary}))
    return 1 if summary["violations"] else 0


if __name__ == "__main__":
    sys.exit(main())
