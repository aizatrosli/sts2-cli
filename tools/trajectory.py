#!/usr/bin/env python3
"""Record or compare seeded random-agent trajectories.

Used to prove that engine changes that should not change behavior (speed-ups, refactors) leave
every observation byte-identical: record with the old build, compare with the new one.

  python3 tools/trajectory.py record out.jsonl [--runs Ironclad:manual:s1 ...] [--steps 400]
  python3 tools/trajectory.py compare out.jsonl
"""
import argparse
import json
import os
import random
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "tests"))
from conftest import Game  # noqa: E402

DEFAULT_RUNS = [f"{c}:{f}:traj-{c.lower()}" for c in ("Ironclad", "Silent", "Defect", "Regent", "Necrobinder")
                for f in ("manual", "auto")]


def play(spec, steps):
    character, flow, seed = spec.split(":")
    rng = random.Random(spec)
    game = Game()
    out = []
    t0 = time.time()
    try:
        state = game.send({"cmd": "start_run", "character": character, "seed": seed, "flow": flow})
        game.send({"cmd": "set_player", "hp": 9999, "max_hp": 9999})
        state = game.send({"cmd": "get_state"})
        out.append(state)
        for _ in range(steps):
            legal = state.get("legal_actions") or []
            if not legal or state.get("decision") == "game_over":
                break
            action = rng.choice(legal)
            if action.get("template"):
                n = action["num_cards"]
                lo, hi = action.get("min_select") or 0, min(action.get("max_select") or 0, n)
                k = rng.randint(lo, max(lo, hi))
                action = {"action": "select_cards", "args": {"indices": ",".join(map(str, sorted(rng.sample(range(n), k))))}}
            else:
                action = {k: v for k, v in action.items() if k in ("action", "args")}
            state = game.send({"cmd": "action", **action})
            out.append(state)
    finally:
        game.close()
    return out, time.time() - t0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", choices=["record", "compare"])
    ap.add_argument("path")
    ap.add_argument("--runs", nargs="*", default=DEFAULT_RUNS)
    ap.add_argument("--steps", type=int, default=400)
    args = ap.parse_args()
    if args.mode == "record":
        with open(args.path, "w") as f:
            for spec in args.runs:
                traj, secs = play(spec, args.steps)
                f.write(json.dumps({"spec": spec, "steps": args.steps, "traj": traj}, sort_keys=True) + "\n")
                print(f"{spec}: {len(traj)} observations, {len(traj) / secs:.0f} steps/s")
        return 0
    bad = 0
    for line in open(args.path):
        rec = json.loads(line)
        traj, secs = play(rec["spec"], rec["steps"])
        old, new = rec["traj"], json.loads(json.dumps(traj, sort_keys=True))
        diff = next((i for i, (a, b) in enumerate(zip(old, new)) if a != b), None)
        if diff is None and len(old) != len(new):
            diff = min(len(old), len(new))
        status = "identical" if diff is None else f"DIFFERS at step {diff}"
        bad += diff is not None
        print(f"{rec['spec']}: {status} ({len(new)} observations, {len(new) / secs:.0f} steps/s)")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
