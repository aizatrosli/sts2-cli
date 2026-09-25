#!/usr/bin/env bash
# verify_godot_stubs.sh — phases 2–4 of docs/godotstubs-reachable-ui-plan.md in one go.
# Needs the game: lib/ from ./setup.sh, and STS2_GAME_DIR (or STS2_GODOTSHARP_DLL) for the
# game's own GodotSharp.dll.
#
#   STS2_GAME_DIR=".../data_sts2_macos_arm64" scripts/verify_godot_stubs.sh [options]
#     --runs N               games per character for the regression and manual-flow checks (default 5)
#     --quick                stub checks only: build, audit, diff, stub tests
#     --skip-trajectories    skip the old-vs-new trajectory comparison
#     --base REV             build to record base trajectories with (default 4f53751, before the stub changes)
#
# Logs go to logs/godot-stubs-verify/<timestamp>/. Exit 1 if any check fails.
set -u

RUNS=5
QUICK=0
TRAJ=1
BASE=4f53751
while [ $# -gt 0 ]; do
    case "$1" in
        --runs) RUNS="$2"; shift 2 ;;
        --quick) QUICK=1; shift ;;
        --skip-trajectories) TRAJ=0; shift ;;
        --base) BASE="$2"; shift 2 ;;
        -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO"
CHARS="Ironclad Silent Defect Regent Necrobinder"

REAL="${STS2_GODOTSHARP_DLL:-${STS2_GAME_DIR:+$STS2_GAME_DIR/GodotSharp.dll}}"
if [ -z "$REAL" ] || [ ! -f "$REAL" ]; then
    echo "Set STS2_GAME_DIR to the game data directory that contains GodotSharp.dll (or STS2_GODOTSHARP_DLL to the file)." >&2
    exit 2
fi
if [ ! -f lib/sts2.dll ]; then
    echo "lib/sts2.dll not found: run ./setup.sh first." >&2
    exit 2
fi
DOTNET="$(python3 -c 'import sys; sys.path.insert(0, "python"); from engine import find_dotnet; print(find_dotnet())')"
export DOTNET

OUT="$REPO/logs/godot-stubs-verify/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$OUT"
SUMMARY="$OUT/summary.txt"
FAILED=0

# record NAME STATUS DETAIL
record() {
    printf '%-34s %-7s %s\n' "$1" "$2" "$3" | tee -a "$SUMMARY"
    [ "$2" = FAIL ] && FAILED=1
    return 0
}

# check NAME LOG CMD... — run CMD, log it, PASS on exit 0
check() {
    local name="$1" log="$OUT/$2"; shift 2
    echo "── $name"
    if "$@" >"$log" 2>&1; then record "$name" PASS "$log"; else record "$name" FAIL "$log"; fi
}

check "build Sts2Headless" build.log "$DOTNET" build src/Sts2Headless/Sts2Headless.csproj
check "audit (gameplay + reachable-ui)" audit.log \
    "$DOTNET" run --project tools/GodotStubAudit -- --reference "$REAL" --fail
"$DOTNET" run --project tools/GodotStubAudit -- --reference "$REAL" --all --json "$OUT/audit.json" >"$OUT/audit-all.log" 2>&1
check "value-type diff (GodotStubDiff)" diff.log \
    "$DOTNET" run --project tools/GodotStubDiff -- --real "$REAL"
if python3 -c 'import pytest' 2>/dev/null; then
    check "tests/test_godot_stubs.py" stub-tests.log \
        env STS2_GODOTSHARP_DLL="$REAL" python3 -m pytest tests/test_godot_stubs.py -q
else
    record "tests/test_godot_stubs.py" SKIP "pytest not installed (pip install pytest)"
fi

if [ "$QUICK" = 0 ]; then
    for char in $CHARS; do
        check "full runs $char ($RUNS)" "full-$char.log" python3 python/play_full_run.py "$RUNS" "$char"
    done
    for char in $CHARS; do
        log="manual-$char.log"
        check "manual flow $char ($RUNS)" "$log" python3 python/sts2_env.py "$RUNS" "$char" --flow manual
        # Completed N/N is not enough: a listed legal action must never be rejected.
        if grep -E 'rejected=[1-9]' "$OUT/$log" >/dev/null; then
            record "  legal actions accepted ($char)" FAIL "rejections in $OUT/$log"
        fi
    done

    if [ "$TRAJ" = 1 ]; then
        echo "── trajectories: base $BASE vs HEAD"
        WT="$(mktemp -d)/base"
        if git worktree add --detach "$WT" "$BASE" >"$OUT/traj-base-build.log" 2>&1 \
            && ln -s "$REPO/lib" "$WT/lib" \
            && "$DOTNET" build "$WT/src/Sts2Headless/Sts2Headless.csproj" >>"$OUT/traj-base-build.log" 2>&1 \
            && python3 "$WT/tools/trajectory.py" record "$OUT/base.jsonl" >"$OUT/traj-record.log" 2>&1; then
            python3 tools/trajectory.py compare "$OUT/base.jsonl" >"$OUT/traj-compare.log" 2>&1
            n=$(grep -c DIFFERS "$OUT/traj-compare.log")
            if [ "$n" = 0 ]; then
                record "trajectories vs $BASE" PASS "all identical"
            else
                # Expected only where the old build hit a MissingMethodException on a stubbed member.
                record "trajectories vs $BASE" REVIEW "$n run(s) differ: $OUT/traj-compare.log"
            fi
        else
            record "trajectories vs $BASE" FAIL "base build/record failed: $OUT/traj-base-build.log, traj-record.log"
        fi
        git worktree remove --force "$WT" >/dev/null 2>&1
    fi
fi

echo
echo "== summary ($OUT) =="
cat "$SUMMARY"
if [ "$FAILED" = 0 ]; then echo "all checks passed"; else echo "FAILED — see the logs above"; fi
exit "$FAILED"
