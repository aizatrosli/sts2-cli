#!/usr/bin/env python3
"""
Gymnasium-style environment over the sts2-cli JSON protocol, for RL training.

Every observation is the engine's decision JSON. `info["legal_actions"]` lists the moves as
ready-to-send action dicts ({"action": ..., "args": {...}}); an agent passes either one of
those dicts to `step()` or its index in that list. `info["action_mask"]` marks which indices
`step(int)` accepts.

Card selections (`select_cards`) come from the engine as one template entry. The wrapper
turns it into indexable actions: one entry per valid pick when there are at most
`max_expanded` picks (every one-card choice), otherwise a sequential selection the UI way —
`pick_card {index}` one card at a time, then `confirm_selection` (sent automatically once
`max_select` cards are picked). `env.select_cards([...])` answers any selection directly.

Use `flow="manual"` (the default here) to get the same screen transitions as the game UI:
rewards screen (claim_reward / proceed), treasure chest (open_chest / pick_relic / skip_relic),
rest-site and event Proceed, and the boss → next-act transition.

    with Sts2Env(character="Silent") as env:
        obs, info = env.reset(seed=42)
        while True:
            action = random.choice([i for i, ok in enumerate(info["action_mask"]) if ok])
            obs, reward, terminated, truncated, info = env.step(action)
            if terminated or truncated:
                break

No gymnasium dependency; the API mirrors gymnasium.Env (reset/step/close). `Sts2VecEnv`
runs several engines in parallel.

Run as a script to smoke-test the protocol with a random agent:

    python3 python/sts2_env.py 5 Ironclad --flow manual
"""

import argparse
import atexit
import concurrent.futures
import itertools
import json
import math
import operator
import os
import queue
import random
import signal
import subprocess
import sys
import threading
import weakref

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VALID_CHARACTERS = ["Ironclad", "Silent", "Defect", "Regent", "Necrobinder"]


from engine import engine_command  # noqa: E402  (python/engine.py)


class Sts2Error(RuntimeError):
    """The simulator rejected a command (payload in .response). The game state is unchanged."""

    def __init__(self, response):
        super().__init__(response.get("message", "simulator error"))
        self.response = response


class Sts2EnvBroken(RuntimeError):
    """The engine timed out or exited. The env must be reset() (which starts a new engine)."""


def default_reward(prev_obs, obs):
    """+1 on victory, -1 on defeat, 0 otherwise. Replace via Sts2Env(reward_fn=...)."""
    if obs.get("decision") == "game_over":
        return 1.0 if obs.get("victory") else -1.0
    return 0.0


def shaped_reward(floor=0.0, act=0.0, hp=0.0, base=default_reward):
    """Reward shaping hook: `base` plus `floor` per floor climbed, `act` per act entered and
    `hp` times the change in HP as a fraction of max HP. All weights default to 0 (off)."""

    def reward(prev_obs, obs):
        r = base(prev_obs, obs)
        prev_ctx = (prev_obs or {}).get("context") or {}
        ctx = obs.get("context") or {}
        if floor and prev_ctx.get("total_floor") is not None and ctx.get("total_floor") is not None:
            r += floor * (ctx["total_floor"] - prev_ctx["total_floor"])
        if act and prev_ctx.get("act") is not None and ctx.get("act") is not None:
            r += act * (ctx["act"] - prev_ctx["act"])
        prev_p, p = (prev_obs or {}).get("player") or {}, obs.get("player") or {}
        if hp and prev_p.get("max_hp") and p.get("max_hp") and "hp" in prev_p and "hp" in p:
            r += hp * (p["hp"] / p["max_hp"] - prev_p["hp"] / prev_p["max_hp"])
        return r

    return reward


def expand_legal_actions(legal, max_expanded=64):
    """Plain actions first (engine order), then the picks of expanded select_cards templates,
    then the templates too large to expand (Sts2Env turns those into sequential picks)."""
    plain, picks, templates = [], [], []
    for a in legal:
        if not a.get("template"):
            plain.append(a)
            continue
        n = a.get("num_cards") or 0
        lo = max(1, a.get("min_select") or 0)  # choosing nothing is skip_select
        hi = min(n, a.get("max_select") or 0)
        count = sum(math.comb(n, k) for k in range(lo, hi + 1))
        if 0 < count <= max_expanded:
            for k in range(lo, hi + 1):
                for combo in itertools.combinations(range(n), k):
                    picks.append({"action": "select_cards", "args": {"indices": ",".join(map(str, combo))}})
        else:
            templates.append(a)
    return plain + picks + templates


_live_envs = weakref.WeakSet()


@atexit.register
def _close_live_envs():
    for env in list(_live_envs):
        env.close()


class Sts2Env:
    metadata = {"render_modes": []}

    def __init__(self, flow="manual", timeout=30.0, max_steps=5000,
                 reward_fn=default_reward, verbose=False, debug=False, *,
                 character="Ironclad", ascension=0, act1=None, max_expanded=64):
        # debug=True enables the engine's debug commands (set_player etc.), e.g. for god mode.
        self.debug = debug
        self.flow = flow
        self.timeout = timeout
        self.max_steps = max_steps
        self.reward_fn = reward_fn
        self.verbose = verbose
        self.character = character
        self.ascension = ascension
        self.act1 = act1
        self.max_expanded = max_expanded
        self._proc = None
        self._lines = None
        self._request_id = 0
        self._broken = None
        self._actions = []
        self._selection = None  # (template, picked indices) during a sequential selection
        self._run = {}
        self.obs = None
        self.steps = 0
        _live_envs.add(self)

    # ── process plumbing ──

    def _spawn(self):
        self._kill()
        self._proc = subprocess.Popen(
            engine_command(debug=self.debug), cwd=REPO_ROOT,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=None if self.verbose else subprocess.DEVNULL,
            text=True, bufsize=1,
            # Own process group, so close() also reaches anything the engine started.
            start_new_session=(os.name == "posix"),
        )
        self._lines = queue.Queue()

        def pump(stream, sink):
            try:
                for line in stream:
                    sink.put(line)
            except (OSError, ValueError):
                pass
            sink.put(None)

        threading.Thread(target=pump, args=(self._proc.stdout, self._lines), daemon=True).start()
        ready = self._read(None)
        if ready.get("type") != "ready":
            raise RuntimeError(f"unexpected handshake: {ready}")

    def _read(self, request_id):
        while True:
            try:
                line = self._lines.get(timeout=self.timeout)
            except queue.Empty:
                raise TimeoutError(f"no simulator response within {self.timeout}s") from None
            if line is None:
                raise RuntimeError("simulator exited")
            if not line.startswith("{"):
                continue
            msg = json.loads(line)
            # A reply to an earlier request that timed out is dropped.
            if request_id is not None and msg.get("request_id") != request_id:
                continue
            msg.pop("request_id", None)
            return msg

    def _kill(self):
        proc, self._proc = self._proc, None
        if proc is None:
            return
        if proc.poll() is None:
            try:
                proc.stdin.write(json.dumps({"cmd": "quit"}) + "\n")
                proc.stdin.flush()
                proc.wait(timeout=5)
            except (OSError, ValueError, subprocess.TimeoutExpired):
                pass
        if proc.poll() is None or os.name == "posix":
            try:
                if os.name == "posix":
                    os.killpg(proc.pid, signal.SIGKILL)
                else:
                    proc.kill()
            except (OSError, ProcessLookupError):
                pass
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            pass
        for stream in (proc.stdin, proc.stdout):
            try:
                stream.close()
            except (OSError, ValueError):
                pass

    def send(self, cmd):
        """Send a raw protocol command and return the parsed response."""
        if self._broken:
            raise Sts2EnvBroken(f"engine is unusable ({self._broken}); call reset()")
        try:
            if self._proc is None or self._proc.poll() is not None:
                self._spawn()
            self._request_id += 1
            self._proc.stdin.write(json.dumps({**cmd, "request_id": self._request_id}) + "\n")
            self._proc.stdin.flush()
            return self._read(self._request_id)
        except (TimeoutError, RuntimeError, OSError, ValueError) as e:
            self._broken = str(e) or type(e).__name__
            self._kill()
            raise Sts2EnvBroken(f"engine is unusable ({self._broken}); call reset()") from e

    # ── gym API ──

    def reset(self, character=None, seed=None, ascension=None, options=None, *, act1=None):
        """Start a run. `seed` (any str/int) is the game seed; None picks a random one, which
        `info["seed"]` reports. `options` may set character, ascension, act1 and flow."""
        opts = dict(options or {})
        character = character or opts.pop("character", None) or self.character
        ascension = ascension if ascension is not None else opts.pop("ascension", self.ascension)
        act1 = act1 or opts.pop("act1", None) or self.act1
        flow = opts.pop("flow", self.flow)
        cmd = {"cmd": "start_run", "character": character, "ascension": int(ascension), "flow": flow}
        if seed is not None:
            cmd["seed"] = str(seed)
        if act1:
            cmd["act1"] = act1
        cmd.update(opts)
        # A reset always recovers: a broken or dead engine is replaced.
        if self._broken or self._proc is None or self._proc.poll() is not None:
            self._broken = None
            self._spawn()
        obs = self.send(cmd)
        if obs.get("type") == "error":
            raise Sts2Error(obs)
        ctx = obs.get("context") or {}
        self._run = {"character": character, "seed": ctx.get("seed"), "ascension": ctx.get("ascension", ascension),
                     "act1": act1 or "random", "flow": ctx.get("flow", flow)}
        self.obs = obs
        self.steps = 0
        return obs, self._info(obs)

    def _info(self, obs, picked=None):
        actions = expand_legal_actions(obs.get("legal_actions") or [], self.max_expanded)
        big = next((a for a in actions if a.get("template")), None)
        self._selection = None
        if big is not None:
            # Too many picks to list: select one card at a time, like the UI.
            picked = list(picked or [])
            self._selection = (big, picked)
            actions = [a for a in actions if not a.get("template")]
            if picked:  # mid-selection: only picks and confirm, as the screen allows
                actions = []
            actions += [{"action": "pick_card", "args": {"index": i}}
                        for i in range(big.get("num_cards") or 0) if i not in picked]
            if len(picked) >= max(1, big.get("min_select") or 0):
                actions.append({"action": "confirm_selection"})
        self._actions = actions
        info = {
            **self._run,
            "decision": obs.get("decision"),
            "steps": self.steps,
            "legal_actions": actions,
            "action_mask": [True] * len(actions),
        }
        if self._selection:
            info["selected"] = list(self._selection[1])
        return info

    def refresh(self):
        """Re-read the current decision point without acting (e.g. after a rejected action)."""
        obs = self.send({"cmd": "get_state"})
        if obs.get("type") == "error":
            raise Sts2Error(obs)
        self.obs = obs
        self._info(obs)
        return obs

    def legal_actions(self):
        """The actions `step(int)` indexes into (templates expanded, see module docs)."""
        return self._actions

    def step(self, action):
        """`action`: an index into `legal_actions()` / `info["legal_actions"]` (ints of any kind,
        numpy included), or an action dict {"action", "args"}."""
        if not isinstance(action, dict):
            try:
                i = operator.index(action)
            except TypeError:
                raise TypeError(f"action must be an int index or an action dict, not {type(action).__name__}") from None
            if not 0 <= i < len(self._actions):
                raise IndexError(f"action index {i} out of range ({len(self._actions)} legal actions)")
            action = self._actions[i]
        if action.get("template"):
            raise ValueError(
                f"legal action is a select_cards template: pick {action.get('min_select')}-"
                f"{action.get('max_select')} of {action.get('num_cards')} cards with env.select_cards([...])")
        if action["action"] in ("pick_card", "confirm_selection"):
            return self._sequential(action)
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
        return obs, self.reward_fn(prev, obs), terminated, truncated, self._info(obs)

    def _sequential(self, action):
        if self._selection is None or action not in self._actions:
            raise ValueError(f"{action['action']} is only valid during a listed sequential selection")
        template, picked = self._selection
        if action["action"] == "pick_card":
            picked = picked + [action["args"]["index"]]
            if len(picked) < (template.get("max_select") or 0):
                return self.obs, 0.0, False, False, self._info(self.obs, picked)
        return self.select_cards(sorted(picked))

    def select_cards(self, indices):
        """Answer a card selection with these card indices."""
        return self.step({"action": "select_cards", "args": {"indices": ",".join(str(int(i)) for i in indices)}})

    def close(self):
        self._kill()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()

    def __del__(self):
        try:
            if getattr(self, "_proc", None) is not None:
                self._kill()
        except Exception:  # noqa: BLE001 — interpreter shutdown
            pass


class Sts2VecEnv:
    """N engines stepped in parallel (threads; each engine is its own process).

    With `autoreset` (default) a finished env starts a new run right away: its step result is
    the first observation of the new run, and `info["final_observation"]` / `final_info` hold
    the last ones of the finished run. Rejected actions raise Sts2Error for that env only after
    all envs have stepped.
    """

    def __init__(self, num_envs, autoreset=True, **env_kwargs):
        self.envs = [Sts2Env(**env_kwargs) for _ in range(num_envs)]
        self.num_envs = num_envs
        self.autoreset = autoreset
        self._pool = concurrent.futures.ThreadPoolExecutor(max_workers=num_envs)
        self._options = None

    def _map(self, fn, *iterables):
        futures = [self._pool.submit(fn, *args) for args in zip(*iterables)]
        concurrent.futures.wait(futures)  # every env finishes its step before any error is raised
        return [f.result() for f in futures]

    def reset(self, seed=None, options=None):
        """`seed`: None (random runs), a list with one seed per env, or an int (env i gets seed+i)."""
        if seed is None or isinstance(seed, (str, bytes)):
            seeds = [seed] * self.num_envs if seed is None else [f"{seed}_{i}" for i in range(self.num_envs)]
        elif isinstance(seed, (list, tuple)):
            seeds = list(seed)
        else:
            seeds = [operator.index(seed) + i for i in range(self.num_envs)]
        self._options = options
        results = self._map(lambda env, s: env.reset(seed=s, options=options), self.envs, seeds)
        return [r[0] for r in results], [r[1] for r in results]

    def _step_one(self, env, action):
        obs, reward, terminated, truncated, info = env.step(action)
        if self.autoreset and (terminated or truncated):
            final_obs, final_info = obs, info
            obs, info = env.reset(options=self._options)
            info = {**info, "final_observation": final_obs, "final_info": final_info}
        return obs, reward, terminated, truncated, info

    def step(self, actions):
        results = self._map(self._step_one, self.envs, actions)
        return tuple(list(x) for x in zip(*results))

    def close(self):
        for env in self.envs:
            env.close()
        self._pool.shutdown(wait=False)

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()


# ── random-agent smoke test ──

def random_action(legal, rng):
    """Uniformly pick a legal action, filling in any select_cards template."""
    if not legal:
        raise RuntimeError("no legal actions")
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
        legal = env.legal_actions()
        if not legal:
            brief = {k: v for k, v in obs.items() if k not in ("player", "legal_actions")}
            raise RuntimeError(f"no legal actions at decision {obs.get('decision')!r}: {json.dumps(brief)[:1500]}")
        action = random_action(legal, rng)
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
        seed = f"{args.seed_prefix}_{i + 1}"
        with Sts2Env(flow=args.flow, verbose=args.verbose, debug=args.god) as env:
            try:
                r = play_random(env, args.character, seed, args.ascension, args.god, args.verbose)
            except Exception as e:  # noqa: BLE001 — report and keep going
                r = {"completed": False, "error": str(e), "steps": env.steps}
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
