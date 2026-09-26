"""Pytest fixtures: Game process wrapper for unit tests."""

import collections
import json
import os
import queue
import subprocess
import sys
import threading
import pytest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECT = os.path.join(ROOT, "src", "Sts2Headless", "Sts2Headless.csproj")
READ_TIMEOUT = float(os.environ.get("STS2_TEST_READ_TIMEOUT", "120"))


sys.path.insert(0, os.path.join(ROOT, "python"))
from engine import engine_command, find_dotnet  # noqa: E402  (python/engine.py; find_dotnet re-exported)

DOTNET = find_dotnet()


class Game:
    """Wraps the headless C# process for testing."""

    def __init__(self, timeout=READ_TIMEOUT):
        env = os.environ.copy()
        # Match the interactive launcher's default: resolve dependencies from lib,
        # never silently borrow missing assemblies from the Steam installation.
        env["STS2_GAME_DIR"] = os.path.join(ROOT, "lib")
        self.timeout = timeout
        self.proc = subprocess.Popen(
            engine_command(DOTNET, debug=True), cwd=ROOT,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, bufsize=1, env=env,
        )
        # Drain both pipes on threads: an unread stderr pipe fills up and blocks the engine.
        self.stderr_lines = collections.deque(maxlen=2000)
        self.stdout_noise = []  # non-JSON lines on the protocol channel (should stay empty)
        self._lines = queue.Queue()
        threading.Thread(target=self._pump, args=(self.proc.stdout, self._lines.put), daemon=True).start()
        threading.Thread(target=self._pump, args=(self.proc.stderr, self.stderr_lines.append), daemon=True).start()
        ready = self._read()
        assert ready.get("type") == "ready", f"Expected ready, got: {ready}"

    @staticmethod
    def _pump(stream, sink):
        for line in stream:
            sink(line.rstrip("\n"))
        sink(None)

    def _read(self):
        while True:
            try:
                line = self._lines.get(timeout=self.timeout)
            except queue.Empty:
                raise TimeoutError(f"no response from game process within {self.timeout}s") from None
            if line is None:
                raise RuntimeError("EOF from game process")
            line = line.strip()
            if line.startswith("{"):
                return json.loads(line)
            if line:
                self.stdout_noise.append(line)

    def stderr_text(self):
        return "\n".join(l for l in self.stderr_lines if l is not None)

    def send(self, cmd):
        self.proc.stdin.write(json.dumps(cmd) + "\n")
        self.proc.stdin.flush()
        return self._read()

    def start(self, character="Ironclad", seed="test", ascension=0, flow=None, act1=None):
        cmd = {"cmd": "start_run", "character": character,
               "seed": seed, "ascension": ascension}
        if flow:
            cmd["flow"] = flow
        if act1:
            cmd["act1"] = act1
        return self.send(cmd)

    def act(self, action, **args):
        cmd = {"cmd": "action", "action": action}
        if args:
            cmd["args"] = args
        return self.send(cmd)

    def get_map(self):
        return self.send({"cmd": "get_map"})

    def set_player(self, **kwargs):
        cmd = {"cmd": "set_player", **kwargs}
        return self.send(cmd)

    def enter_room(self, room_type, **kwargs):
        cmd = {"cmd": "enter_room", "type": room_type, **kwargs}
        return self.send(cmd)

    def set_draw_order(self, cards):
        return self.send({"cmd": "set_draw_order", "cards": cards})

    def close(self):
        try:
            self.proc.stdin.write('{"cmd":"quit"}\n')
            self.proc.stdin.flush()
        except Exception:
            pass
        try:
            self.proc.terminate()
            self.proc.wait(timeout=5)
        except Exception:
            self.proc.kill()

    # --- Auto-play helpers ---

    def select_min(self, state):
        """Answer a card_select with the fewest cards allowed (skip when nothing is required)."""
        n = state.get("min_select") or 0
        if n == 0:
            return self.act("skip_select")
        return self.act("select_cards", indices=",".join(str(i) for i in range(n)))

    def auto_combat(self, state):
        """Play one card or end turn."""
        hand = state.get("hand", [])
        energy = state.get("energy", 0)
        playable = [c for c in hand if c.get("can_play") and c.get("cost", 99) <= energy]
        if playable:
            card = playable[0]
            args = {"card_index": card["index"]}
            if card.get("target_type") == "AnyEnemy":
                enemies = state.get("enemies", [])
                if enemies:
                    args["target_index"] = enemies[0]["index"]
            return self.act("play_card", **args)
        return self.act("end_turn")

    def auto_play_combat(self, state, max_steps=300):
        """Auto-play combat until it ends."""
        for _ in range(max_steps):
            if state.get("decision") != "combat_play":
                return state
            state = self.auto_combat(state)
        raise RuntimeError("Combat did not end")

    def skip_neow(self, state):
        """Skip the Neow event and all follow-up rewards until map_select."""
        for _ in range(20):
            dec = state.get("decision", "")
            if dec == "map_select":
                return state
            if dec == "event_choice":
                opts = [o for o in state["options"] if not o.get("is_locked")]
                state = self.act("choose_option", option_index=opts[0]["index"])
            elif dec == "card_reward":
                state = self.act("skip_card_reward")
            elif dec == "bundle_select":
                state = self.act("select_bundle", bundle_index=0)
            elif dec == "card_select":
                state = self.select_min(state)
            else:
                state = self.act("proceed")
        return state


@pytest.fixture
def game():
    """Each test gets an independent game process."""
    g = Game()
    yield g
    g.close()


def engine_available():
    """The engine needs the game's DLLs (./setup.sh copies them to lib/) and a build."""
    from engine import built_engine_dll
    return os.path.isfile(os.path.join(ROOT, "lib", "sts2.dll")) and built_engine_dll() is not None


def pytest_configure(config):
    config.addinivalue_line("markers", "slow: long-running end-to-end test")
    config.addinivalue_line("markers", "engine: needs the game DLLs and a built engine")


def pytest_collection_modifyitems(config, items):
    """Without the (proprietary) game DLLs, e.g. in CI, only the engine-free tests run."""
    if engine_available():
        return
    skip = pytest.mark.skip(reason="needs the game DLLs (./setup.sh) and a built engine")
    for item in items:
        if {"game", "env"} & set(getattr(item, "fixturenames", ())) or item.get_closest_marker("engine"):
            item.add_marker(skip)
