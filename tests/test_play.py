"""Tests for CLI play helpers."""

from __future__ import annotations

import importlib.util
import pathlib
import sys
import os
import subprocess

import pytest


ROOT = pathlib.Path(__file__).resolve().parents[1]
PLAY_PATH = ROOT / "python" / "play.py"

sys.path.insert(0, str(ROOT / "python"))
spec = importlib.util.spec_from_file_location("play_module_for_tests", PLAY_PATH)
play = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(play)


def test_quit_save_defaults_to_save_dir(monkeypatch):
    monkeypatch.setattr("builtins.input", lambda prompt="": "y")

    path = play._quit_with_save(None, "Ironclad", "seed123")

    assert path is not None
    assert path.startswith(play.SAVE_DIR)
    assert path.endswith(".save")


@pytest.mark.engine
@pytest.mark.parametrize("character", ["Ironclad", "Silent", "Defect", "Regent", "Necrobinder"])
def test_interactive_launcher_without_steam_fallback(character):
    """Exercise the actual user entry point, including its default DLL search path."""
    env = os.environ.copy()
    env.pop("STS2_GAME_DIR", None)
    result = subprocess.run(
        [sys.executable, str(PLAY_PATH), "--character", character,
         "--seed", "cli_5005", "--no-log"],
        input="quit\nn\n", capture_output=True, text=True,
        cwd=ROOT, env=env, timeout=30,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert "Neow" in result.stdout, result.stdout + result.stderr
    assert "TypeInitializationException" not in result.stdout


def test_setup_repairs_missing_module_dependency(monkeypatch):
    isfile = os.path.isfile
    dependency = os.path.join(play.LIB_DIR, "Sentry.Godot.dll")
    # Every other DLL is present (also when lib/ is empty, e.g. in CI).
    monkeypatch.setattr(os.path, "isfile", lambda path: path != dependency and (
        isfile(path) or os.path.dirname(path) == play.LIB_DIR))
    monkeypatch.setattr(play, "_not_shipped", set)  # a game build that ships the dependency
    monkeypatch.setattr(play, "_find_game_dir", lambda: "/test/steam/data")
    monkeypatch.setattr(play, "_build", lambda: True)  # the build step is not under test
    getmtime = os.path.getmtime
    monkeypatch.setattr(os.path, "getmtime", lambda path: 0 if os.path.dirname(path) == play.LIB_DIR else getmtime(path))
    calls = []
    monkeypatch.setattr(play.subprocess, "run", lambda args, **kwargs: calls.append((args, kwargs)))
    monkeypatch.delenv("STS2_GAME_DIR", raising=False)
    play.ensure_setup()
    assert calls == [(["bash", os.path.join(play.ROOT, "setup.sh"), "/test/steam/data"],
                      {"cwd": play.ROOT, "check": True})]
