"""Locate dotnet and build the command that starts the headless engine.

Shared by play.py, play_full_run.py, sts2_env.py and the tests so they all find the same
runtime and start the engine the same (fast) way.
"""
import glob
import os
import shutil
import subprocess

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECT = os.path.join(REPO_ROOT, "src", "Sts2Headless", "Sts2Headless.csproj")


def find_dotnet(fallback="dotnet"):
    """dotnet executable: $DOTNET, $DOTNET_ROOT/dotnet, ~/.dotnet, ~/.dotnet-arm64, then PATH.
    Returns `fallback` when none of them runs."""
    candidates = [os.environ.get("DOTNET")]
    if os.environ.get("DOTNET_ROOT"):
        candidates.append(os.path.join(os.environ["DOTNET_ROOT"], "dotnet"))
    candidates += [os.path.expanduser("~/.dotnet/dotnet"), os.path.expanduser("~/.dotnet-arm64/dotnet"),
                   shutil.which("dotnet")]
    for c in candidates:
        if not c or not os.path.isfile(c) or not os.access(c, os.X_OK):
            continue
        try:
            if subprocess.run([c, "--version"], capture_output=True, timeout=10).returncode == 0:
                return c
        except (OSError, subprocess.TimeoutExpired):
            continue
    return fallback


def built_engine_dll():
    """Newest built Sts2Headless.dll, or None when the project has not been built."""
    dlls = glob.glob(os.path.join(REPO_ROOT, "src", "Sts2Headless", "bin", "*", "*", "Sts2Headless.dll"))
    return max(dlls, key=os.path.getmtime) if dlls else None


def engine_command(dotnet=None, debug=False):
    """Start the built dll directly: about 0.5 s faster per process than `dotnet run`.
    `debug` enables the debug commands (set_player, enter_room, set_draw_order)."""
    dotnet = dotnet or find_dotnet()
    dll = built_engine_dll()
    cmd = [dotnet, dll] if dll else [dotnet, "run", "--no-build", "--project", PROJECT, "--"]
    return cmd + (["--debug"] if debug else [])
