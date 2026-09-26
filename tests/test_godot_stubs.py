"""GodotStubs coverage: every GodotSharp member sts2.dll uses from game logic must exist.

A member missing from the stub throws MissingMethodException the first time game logic
calls it (typically "Combat #N turn loop died"). The optional checks compare against
the real GodotSharp.dll shipped with the game: set STS2_GODOTSHARP_DLL, or STS2_GAME_DIR
to the game data directory that contains it.
"""
import json
import os
from pathlib import Path
import subprocess

import pytest

from conftest import find_dotnet

ROOT = Path(__file__).resolve().parents[1]
DOTNET = find_dotnet()
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


@pytest.mark.skipif(not STS2_DLL.is_file(), reason="needs lib/sts2.dll (./setup.sh)")
def test_testmode_branches_reviewed():
    """Every TestMode read in gameplay code is on the reviewed allowlist (new ones need review)."""
    result = _run_tool("TestModeAudit", "--fail")
    assert result.returncode == 0, result.stdout + result.stderr


FIXTURE = ROOT / "tests" / "fixtures" / "godot_audit"


def _build(project):
    result = subprocess.run([DOTNET, "build", str(project)], cwd=ROOT, capture_output=True, text=True, timeout=900)
    assert result.returncode == 0, result.stdout + result.stderr


def _audit_fixture(tmp_path, *args):
    """Run GodotStubAudit on the fixture game against the fixture stub; return (exit code, report)."""
    _build(FIXTURE / "Game" / "FixtureGame.csproj")
    _build(FIXTURE / "Stub" / "GodotSharp.Stub.csproj")
    out = tmp_path / "audit.json"
    result = _run_tool(
        "GodotStubAudit",
        "--sts2", str(FIXTURE / "Game" / "bin" / "Debug" / "net9.0" / "FixtureGame.dll"),
        "--stub", str(FIXTURE / "Stub" / "bin" / "Debug" / "net9.0" / "GodotSharp.dll"),
        "--json", str(out), *args,
    )
    assert result.returncode in (0, 1), result.stdout + result.stderr
    return result.returncode, json.loads(out.read_text())


def _missing_tiers(report):
    tiers = {m["name"]: m["tier"] for m in report["members"] if not m["present"]}
    tiers.update({t["name"]: t["tier"] for t in report["types"] if not t["present"]})
    return tiers


def test_audit_reachable_ui_tier(tmp_path):
    """A UI-only reference is reachable-ui when gameplay calls its method within 2 edges."""
    code, report = _audit_fixture(tmp_path, "--fail")
    assert code == 1  # gameplay and reachable-ui gaps fail the audit
    assert _missing_tiers(report) == {
        "InGameplay": "gameplay",
        "OneHop": "reachable-ui",                # gameplay → UI
        "TwoHop": "reachable-ui",                # gameplay → UI → UI
        "ThreeHop": "ui",                        # 3 edges: beyond the default depth
        "ViaOverride": "reachable-ui",           # virtual call lands in an override
        "ViaOverrideOfOverride": "reachable-ui",
        "ViaInterface": "reachable-ui",          # interface call, implicit implementation
        "ViaExplicitInterface": "reachable-ui",  # interface call, explicit implementation
        "InAsync": "reachable-ui",               # body in the state machine's MoveNext
        "InLambda": "reachable-ui",              # lambda created by a reached UI method
        "InGeneric": "reachable-ui",             # method on a generic type instantiation
        "InCctor": "reachable-ui",               # static constructor of a reached type
        "Godot.MissingType": "reachable-ui",     # field type of a reached UI type
        "Orphan": "ui",                          # UI code gameplay never calls
        "ToolOnly": "tooling",
    }
    two_hop = next(m for m in report["members"] if m["name"] == "TwoHop")
    assert two_hop["path"] == ["Models.Card::OnPlay", "Nodes.NChain::A", "Nodes.NChain::B"]


def test_audit_depth_option(tmp_path):
    _, report = _audit_fixture(tmp_path, "--depth", "3")
    assert report["depth"] == 3
    assert _missing_tiers(report)["ThreeHop"] == "reachable-ui"
