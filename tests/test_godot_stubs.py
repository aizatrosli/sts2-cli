"""GodotStubs coverage: every GodotSharp member sts2.dll uses from game logic must exist.

A member missing from the stub throws MissingMethodException the first time game logic
calls it (typically "Combat #N turn loop died"). The optional checks compare against
the real GodotSharp.dll shipped with the game: set STS2_GODOTSHARP_DLL, or STS2_GAME_DIR
to the game data directory that contains it.
"""
import os
from pathlib import Path
import shutil
import subprocess

import pytest

ROOT = Path(__file__).resolve().parents[1]
DOTNET = os.path.expanduser("~/.dotnet-arm64/dotnet")
if not os.path.isfile(DOTNET):
    DOTNET = shutil.which("dotnet") or DOTNET
STS2_DLL = ROOT / "lib" / "sts2.dll"
STUB_DLL = ROOT / "src" / "Sts2Headless" / "bin" / "Debug" / "net9.0" / "GodotSharp.dll"


def _real_godotsharp():
    explicit = os.environ.get("STS2_GODOTSHARP_DLL")
    if explicit:
        return Path(explicit)
    game_dir = os.environ.get("STS2_GAME_DIR")
    if game_dir and (Path(game_dir) / "GodotSharp.dll").is_file():
        return Path(game_dir) / "GodotSharp.dll"
    return None


def _run_tool(project, *args):
    return subprocess.run(
        [DOTNET, "run", "--project", str(ROOT / "tools" / project), "--", *args],
        cwd=ROOT, capture_output=True, text=True, timeout=900,
    )


@pytest.mark.skipif(not STS2_DLL.is_file() or not STUB_DLL.is_file(),
                    reason="needs lib/sts2.dll (./setup.sh) and a built Sts2Headless")
def test_stub_covers_gameplay_references():
    args = ["--fail"]
    real = _real_godotsharp()
    if real and real.is_file():
        args += ["--reference", str(real)]  # also checks enum constants
    result = _run_tool("GodotStubAudit", *args)
    assert result.returncode == 0, result.stdout + result.stderr


@pytest.mark.skipif(not STUB_DLL.is_file(), reason="needs a built Sts2Headless")
def test_stub_value_types_match_real_godotsharp():
    real = _real_godotsharp()
    if not real or not real.is_file():
        pytest.skip("set STS2_GODOTSHARP_DLL or STS2_GAME_DIR to compare against the real GodotSharp.dll")
    result = _run_tool("GodotStubDiff", "--real", str(real))
    assert result.returncode == 0, result.stdout + result.stderr
