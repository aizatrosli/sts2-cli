#!/usr/bin/env python3
"""
Gym-style environment over the sts2-cli JSON protocol, for RL training.

Every observation is the engine's decision JSON; it carries a `legal_actions` list whose
entries are ready-to-send action commands ({"action": ..., "args": {...}}). An agent either
passes one of those dicts to `step()` or just its index in `legal_actions`.

Use `flow="manual"` (the default here) to get the same screen transitions as the game UI:
rewards screen (claim_reward / proceed), treasure chest (open_chest / pick_relic / skip_relic),
rest-site and event Proceed, and the boss → next-act transition.

    env = Sts2Env()
    obs, info = env.reset(character="Silent", seed="abc", ascension=0)
    while True:
        obs, reward, terminated, truncated, info = env.step(0)   # first legal action
        if terminated or truncated:
            break
    env.close()

No gymnasium dependency; the API mirrors gymnasium.Env (reset/step/close).

Run as a script to smoke-test the protocol with a random agent:

    python3 python/sts2_env.py 5 Ironclad --flow manual
"""

import argparse
import json
import os
import queue
import random
import subprocess
import sys
import threading

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECT = os.path.join(REPO_ROOT, "src", "Sts2Headless", "Sts2Headless.csproj")
VALID_CHARACTERS = ["Ironclad", "Silent", "Defect", "Regent", "Necrobinder"]


from engine import engine_command  # noqa: E402  (python/engine.py)


class Sts2Error(RuntimeError):
    """The simulator rejected a command (payload in .response)."""

    def __init__(self, response):
        super().__init__(response.get("message", "simulator error"))
        self.response = response


def default_reward(prev_obs, obs):
    """+1 on victory, -1 on defeat, 0 otherwise. Replace via Sts2Env(reward_fn=...)."""
    if obs.get("decision") == "game_over":
        return 1.0 if obs.get("victory") else -1.0
    return 0.0


class Sts2Env:
    def __init__(self, flow="manual", timeout=30.0, max_steps=5000,
                 reward_fn=default_reward, verbose=False):
        self.flow = flow
        self.timeout = timeout
        self.max_steps = max_steps
        self.reward_fn = reward_fn
        self.verbose = verbose
        self._proc = None
        self._lines = None
        self.obs = None
        self.steps = 0

    # ── process plumbing ──

    def _ensure_process(self):
        if self._proc is not None and self._proc.poll() is None:
            return
        self._proc = subprocess.Popen(
            engine_command(), cwd=REPO_ROOT,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=None if self.verbose else subprocess.DEVNULL,
            text=True, bufsize=1,
        )
        self._lines = queue.Queue()

        def pump(stream, sink):
            for line in stream:
                sink.put(line)
            sink.put(None)

        threading.Thread(target=pump, args=(self._proc.stdout, self._lines), daemon=True).start()
        ready = self._read()
        if ready.get("type") != "ready":
            raise RuntimeError(f"unexpected handshake: {ready}")

    def _read(self):
        while True:
            try:
                line = self._lines.get(timeout=self.timeout)
            except queue.Empty:
                raise TimeoutError(f"no simulator response within {self.timeout}s") from None
            if line is None:
                raise RuntimeError("simulator exited")
            if line.startswith("{"):
                return json.loads(line)

    def send(self, cmd):
        """Send a raw protocol command and return the parsed response."""
        self._ensure_process()
        self._proc.stdin.write(json.dumps(cmd) + "\n")
        self._proc.stdin.flush()
        return self._read()

    # ── gym API ──

    def reset(self, character="Ironclad", seed=None, ascension=0, options=None):
        cmd = {"cmd": "start_run", "character": character, "ascension": ascension,
               "flow": self.flow}
        if seed is not None:
            cmd["seed"] = str(seed)
        if options:
            cmd.update(options)
        obs = self.send(cmd)
        if obs.get("type") == "error":
            raise Sts2Error(obs)
        self.obs = obs
        self.steps = 0
        return obs, {}

    def refresh(self):
        """Re-read the current decision point without acting (e.g. after a rejected action)."""
        obs = self.send({"cmd": "get_state"})
        if obs.get("type") == "error":
            raise Sts2Error(obs)
        self.obs = obs
        return obs

    def legal_actions(self, obs=None):
        return (obs or self.obs or {}).get("legal_actions", [])

    def step(self, action):
        """`action`: an index into obs["legal_actions"], or an action dict {"action", "args"}."""
        if isinstance(action, int):
            action = self.legal_actions()[action]
        if action.get("template"):
            raise ValueError("fill in template args (e.g. select_cards indices) before stepping")
        cmd = {"cmd": "action", "action": action["action"]}
        if action.get("args"):
            cmd["args"] = action["args"]
        obs = self.send(cmd)
        if obs.get("type") == "error":
            raise Sts2Error(obs)
        prev, self.obs = self.obs, obs
        self.steps += 1
        terminated = obs.get("decision") == "game_over"
        truncated = not terminated and self.steps >= self.max_steps
        return obs, self.reward_fn(prev, obs), terminated, truncated, {}

    def close(self):
        if self._proc is None:
            return
        try:
            self._proc.stdin.write(json.dumps({"cmd": "quit"}) + "\n")
            self._proc.stdin.flush()
            self._proc.wait(timeout=5)
        except Exception:
            self._proc.kill()
        self._proc = None


# ── random-agent smoke test ──

def random_action(obs, rng):
    """Uniformly pick a legal action, filling in the select_cards template."""
    legal = obs.get("legal_actions") or []
    if not legal:
        brief = {k: v for k, v in obs.items() if k not in ("player", "legal_actions")}
        raise RuntimeError(f"no legal actions at decision {obs.get('decision')!r}: {json.dumps(brief)[:1500]}")
    # Prefer anything over end_turn/proceed a little, so runs exercise more of the game.
    action = rng.choice(legal)
    if action.get("template"):
        n = action.get("num_cards", 0)
        lo = max(0, action.get("min_select") or 0)
        hi = min(n, action.get("max_select") or n)
        k = rng.randint(lo, hi) if hi >= lo else lo
        picks = sorted(rng.sample(range(n), k)) if k <= n else list(range(n))
        action = {"action": "select_cards", "args": {"indices": ",".join(map(str, picks))}}
    return action


def play_random(env, character, seed, ascension=0, god=False, verbose=False):
    rng = random.Random(seed)
    obs, _ = env.reset(character=character, seed=seed, ascension=ascension)
    if god:
        env.send({"cmd": "set_player", "max_hp": 9999, "hp": 9999})
        obs = env.refresh()
    decisions = {}
    errors = 0
    while True:
        decisions[obs.get("decision")] = decisions.get(obs.get("decision"), 0) + 1
        action = random_action(obs, rng)
        if verbose:
            print(f"  [{env.steps}] {obs.get('decision')} -> {action}")
        try:
            obs, reward, terminated, truncated, _ = env.step(action)
        except Sts2Error as e:
            # A legal action must never be rejected; count it and pick again.
            errors += 1
            print(f"  REJECTED legal action {action}: {e}")
            if errors > 20:
                raise
            obs = env.refresh()
            continue
        if terminated or truncated:
            decisions[obs.get("decision")] = decisions.get(obs.get("decision"), 0) + 1
            ctx = obs.get("context") or {}
            return {
                "completed": terminated,
                "victory": bool(obs.get("victory")),
                "steps": env.steps,
                "act": ctx.get("act"),
                "floor": ctx.get("floor"),
                "decisions": decisions,
                "rejected": errors,
            }


def main():
    ap = argparse.ArgumentParser(description="Random-agent smoke test for Sts2Env")
    ap.add_argument("num_runs", type=int)
    ap.add_argument("character", nargs="?", default="Ironclad", choices=VALID_CHARACTERS)
    ap.add_argument("--flow", default="manual", choices=["auto", "manual"])
    ap.add_argument("--ascension", type=int, default=0)
    ap.add_argument("--god", action="store_true", help="9999 HP so runs reach later acts")
    ap.add_argument("--seed-prefix", default="env")
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    completed = 0
    seen = {}
    for i in range(args.num_runs):
        env = Sts2Env(flow=args.flow, verbose=args.verbose)
        seed = f"{args.seed_prefix}_{i + 1}"
        try:
            r = play_random(env, args.character, seed, args.ascension, args.god, args.verbose)
        except Exception as e:  # noqa: BLE001 — report and keep going
            r = {"completed": False, "error": str(e), "steps": env.steps}
        finally:
            env.close()
        completed += 1 if r.get("completed") else 0
        for k, v in (r.get("decisions") or {}).items():
            seen[k] = seen.get(k, 0) + v
        status = "WIN" if r.get("victory") else ("LOSS" if r.get("completed") else "ERROR")
        print(f"Run {i + 1}: {status} seed={seed} steps={r.get('steps')} act={r.get('act')} "
              f"floor={r.get('floor')} rejected={r.get('rejected')} {r.get('error') or ''}")
    print(f"Decisions seen: {json.dumps(seen, sort_keys=True)}")
    print(f"Completed: {completed}/{args.num_runs}")
    return 0 if completed == args.num_runs else 1


if __name__ == "__main__":
    sys.exit(main())
