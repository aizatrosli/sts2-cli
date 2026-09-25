"""The GodotSharp stubs must cover every Godot member that gameplay code in sts2.dll references.

CoreCLR resolves member references when it JIT-compiles a method, so a single missing stub member
makes the whole calling game method throw (MissingMethodException) the first time it runs. In the
combat turn loop that shows up as "turn loop died" and a false game_over.
scripts/godot_stub_audit lists the gaps; UI-only namespaces are ignored because they never run
headless.
"""
import os
import shutil
import subprocess

import pytest

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOTNET = os.path.expanduser("~/.dotnet-arm64/dotnet")
if not os.path.isfile(DOTNET):
    DOTNET = shutil.which("dotnet") or DOTNET


def test_no_gameplay_reachable_stub_gaps():
    sts2 = os.path.join(REPO, "lib", "sts2.dll")
    stub = os.path.join(REPO, "src", "Sts2Headless", "bin", "Debug", "net9.0", "GodotSharp.dll")
    if not (os.path.isfile(sts2) and os.path.isfile(stub)):
        pytest.skip("run ./setup.sh and build first")
    proc = subprocess.run(
        [DOTNET, "run", "--project", os.path.join(REPO, "scripts", "godot_stub_audit"), "--",
         "--sts2", sts2, "--stub", stub],
        capture_output=True, text=True, timeout=600,
    )
    assert proc.returncode == 0, "Godot members used by gameplay code are missing from src/GodotStubs:\n" + proc.stdout
