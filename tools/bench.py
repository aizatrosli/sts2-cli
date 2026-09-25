#!/usr/bin/env python3
"""Engine throughput benchmark: random legal actions, per-action latency and steps/s.

  python3 tools/bench.py [--characters Silent Defect] [--flows manual auto] [--steps 500]

Steps/s counts engine response time only (process startup excluded).
"""
import argparse
import collections
import os
import random
import statistics
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "tests"))
from conftest import Game  # noqa: E402


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--characters", nargs="*", default=["Silent", "Defect"])
    ap.add_argument("--flows", nargs="*", default=["manual", "auto"])
    ap.add_argument("--steps", type=int, default=500)
    args = ap.parse_args()

    times = collections.defaultdict(list)
    for character in args.characters:
        for flow in args.flows:
            game = Game()
            rng = random.Random(character + flow)
            game.send({"cmd": "start_run", "character": character, "seed": "bench", "flow": flow})
            game.send({"cmd": "set_player", "hp": 9999, "max_hp": 9999})
            state = game.send({"cmd": "get_state"})
            for _ in range(args.steps):
                legal = state.get("legal_actions") or []
                if not legal or state.get("decision") == "game_over":
                    break
                action = rng.choice(legal)
                if action.get("template"):
                    n = action.get("min_select") or 0
                    action = {"action": "select_cards", "args": {"indices": ",".join(map(str, range(n)))}}
                action = {k: v for k, v in action.items() if k in ("action", "args")}
                t0 = time.perf_counter()
                state = game.send({"cmd": "action", **action})
                times[action["action"]].append((time.perf_counter() - t0) * 1000)
            game.close()

    total = sum(sum(v) for v in times.values())
    for name, v in sorted(times.items(), key=lambda kv: -sum(kv[1])):
        p95 = sorted(v)[int(len(v) * 0.95)]
        print(f"{name:32s} n={len(v):5d} p50={statistics.median(v):6.1f}ms p95={p95:6.1f}ms share={sum(v) / total:5.1%}")
    print(f"steps/s: {sum(len(v) for v in times.values()) / (total / 1000):.0f}")


if __name__ == "__main__":
    main()
