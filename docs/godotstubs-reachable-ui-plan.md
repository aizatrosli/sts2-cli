# GodotStubs reachable-UI plan

Goal: no GodotSharp type or member that gameplay code can reach is missing from
`src/GodotStubs`. "Reachable" means UI code (`MegaCrit.Sts2.Core.Nodes.*`, …) called from
gameplay code within two call edges. A gap there throws `MissingMethodException` in the turn
loop, the same as a gameplay-tier gap does.

Done when:
- GodotStubAudit reports 0 gameplay gaps and 0 reachable-ui gaps against the game's
  `GodotSharp.dll`.
- GodotStubDiff reports 0 mismatches against the game's `GodotSharp.dll`.
- The CLAUDE.md regression gate passes: `Completed: 5/5` for every character.

## Where it stands (branch `claude/tender-ritchie-bdt7k0`, commit de007d1)

Done, in a cloud session without the game installed:
- Stubs added for all 16 items named in the task: `Godot.Curve2D`,
  `Godot.Input/MouseModeEnum`, `Control.AddThemeColorOverride`, `Control.RotationDegrees`,
  `Control.SetGlobalPosition`, `Control.PropertyName.Size`, `Curve2D.GetBakedLength`,
  `Node2D.GetAngleTo`, `ParticleProcessMaterial.Color`, `Path2D.Curve`,
  `PropertyTweener.FromCurrent`, `Texture2D.GetWidth` and `Viewport.GuiGetFocusOwner`.
- `Line2D.AddPoint(Vector2, int index = -1)` replaces the `int?` overload.
- New `Vector2`/`Mathf` `BezierInterpolate` and `CubicInterpolate`, which Curve2D baking
  uses.
- Signatures were checked by reflection against the NuGet `GodotSharp 4.5.1` package, the
  same version the stub declares. All 51 new or changed declarations are identical.
- GodotStubDiff against that NuGet DLL: 515 members compared, 0 mismatches.

Then, with the game's Windows data directory (`setup.sh <dir>` in the cloud session):
1. Phase 1 added the `reachable-ui` tier to the audit.
2. Phase 2: against the pre-change stub (`4f53751`) the tier lists 6 types and 22 members.
   That covers the original 16 items, plus `Input.set_MouseMode` (one of the two unnamed
   members). It also finds 12 more gaps: `Control.FocusBehaviorRecursiveEnum` /
   `SetFocusBehaviorRecursive`, `CurveXyzTexture`, `SubViewport.UpdateMode` / `Size` /
   `Size2DOverride` / `RenderTargetUpdateMode`, `CpuParticles2D.EmissionSphereRadius`,
   `Curve2D.SampleBakedWithRotation`, `SceneTree.CreateTween` and
   `Viewport.GuiReleaseFocus`. All are stubbed now. `SampleBakedWithRotation` uses Godot's
   baked Bezier tangents, via new `Vector2.BezierDerivative`/`Slerp` and
   `Mathf.BezierDerivative`. The audit now reports 0 gameplay, 0 reachable-ui and 0 enum
   issues (`--fail` exits 0).
3. Phase 3: the game's `GodotSharp.dll` is byte-identical to NuGet `GodotSharp 4.5.1`.
   GodotStubDiff against it compares 519 members with 0 mismatches.
4. Phase 4: see the result under Phase 4 below.

---

## Phase 1: add the `reachable-ui` tier to GodotStubAudit — done

Implemented as designed below, with two differences: the fixture lives in
`tests/fixtures/godot_audit/` (a fake GodotSharp built as a "real" and a "stub" variant, plus
a fake game assembly), and a static constructor of a called type is also an edge. Tested by
`test_audit_reachable_ui_tier` / `test_audit_depth_option` in `tests/test_godot_stubs.py`,
which need no game files. Not yet run on the real `sts2.dll` (Phase 2).

Design (all in `tools/GodotStubAudit/Program.cs`):
- **Call graph.** While `ScanBody` walks IL, record an edge from the current MethodDef for
  every `call`, `callvirt`, `newobj`, `ldftn` or `ldvirtftn` operand that resolves to an
  sts2 MethodDef. That covers a direct MethodDef token, or a MethodSpec whose generic method
  is a MethodDef.
  - Virtual calls: for `callvirt` to a virtual method, also add edges to every override in
    sts2 (the MethodImpl table, plus same-name, same-signature overrides in derived types).
    This over-approximates, which is the safe direction.
  - Async and iterator methods: the body runs in the compiler-generated state machine's
    `MoveNext`. Add a stub → `MoveNext` edge by reading `AsyncStateMachineAttribute` /
    `IteratorStateMachineAttribute`, so the stub doesn't count as an extra hop.
  - Lambdas: `ldftn` into a compiler-generated closure type is already an edge. The
    closure's namespace comes from `OuterNamespace`, as it does today.
- **Per-method origins.** Next to the per-namespace `Origins` counts, keep the set of
  MethodDefs that reference each Godot type or member.
- **Classification.** A reference that would be tier `ui` becomes `reachable-ui` when any
  referencing UI method is within two edges of a gameplay-tier method. Find this with a
  bounded BFS from all gameplay methods, depth ≤ 2, recording predecessors.
  - Add `--depth N` (default 2).
  - Order the tiers gameplay < reachable-ui < tooling < ui, so `TierOfAll` still takes the
    most severe.
- **Output.**
  - Add a `reachable-ui` column to the summary table.
  - The default listing prints the gameplay tier and the reachable-ui tier. Each
    reachable-ui entry gets its real declaration (with `--reference`) and one shortest
    gameplay → UI path, e.g.
    `Models.Cards.X.OnPlay → Nodes.Vfx.NFoo.Create → Godot.Curve2D.GetBakedLength()`.
  - JSON gets `tier: "reachable-ui"` and a `path` array.
- **`--fail`** also exits 1 on a reachable-ui gap, because it crashes gameplay the same way.
- **Test.** A small fixture assembly under `tools/GodotStubAudit/Fixture/` (a gameplay
  class calling a `…Nodes.` class that calls a missing Godot member, one hop and three hops
  deep) with a unit test that asserts the one-hop reference is `reachable-ui` and the
  three-hop reference is `ui`. This runs without the game, so it can go in
  `tests/test_godot_stubs.py` unconditionally.

Size: M. This phase can be done in a cloud session, since the fixture replaces sts2.dll.

## Phases 2–4 in one command

`scripts/verify_godot_stubs.sh` runs Phases 2–4 below and prints a summary, with logs in
`logs/godot-stubs-verify/<timestamp>/`:

```bash
STS2_GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64" \
    scripts/verify_godot_stubs.sh            # --quick: stub checks only
```

- The trajectory step builds the base revision (`--base`, default `4f53751`) in a temporary
  git worktree and records with it. Differing runs are reported as `REVIEW`, not `FAIL`: see
  Phase 4.
- The full audit listing is saved as `audit-all.log` and `audit.json`. Those two files are
  the input for fixing anything Phase 2 finds.

## Phase 2: audit against the real game — done

```bash
dotnet build src/Sts2Headless/Sts2Headless.csproj
G="$STS2_GAME_DIR"
dotnet run --project tools/GodotStubAudit -- --reference "$G/GodotSharp.dll" --fail
dotnet run --project tools/GodotStubAudit -- --reference "$G/GodotSharp.dll" --all --json audit.json
```

- Expect 0 gameplay and 0 reachable-ui gaps. For each remaining entry (the two unnamed
  members, or anything the new call graph finds beyond the original 16), add it following
  the GodotStubs rules. Take the exact declaration from the audit's `real:` line: visuals
  are no-ops, and anything that computes a value computes like Godot.
- Check the enum section is empty. It now covers `Input.MouseModeEnum`.
- Read the printed call paths for the Curve2D and Path2D entries.
  - `Path2D.Curve` starts as an empty `Curve2D`, not Godot's default of null, because
    headless nodes come from `new T()` with no scene data.
  - If a gameplay path reads points from the curve and expects scene content, decide
    whether a Harmony no-op on that UI method (`PatchCosmeticNoOp`) is better than the stub.

## Phase 3: value-type diff against the game's GodotSharp — done

```bash
dotnet run --project tools/GodotStubDiff -- --real "$G/GodotSharp.dll"
STS2_GAME_DIR="$G" python3 -m pytest tests/test_godot_stubs.py -q
```

Expect 0 mismatches. If the game's build differs from NuGet 4.5.1 (a custom engine build),
fix the stub to match the game's DLL, not the NuGet one.

## Phase 4: regression gate

1. **Full runs.** The CLAUDE.md loop, 5 runs per character, `Completed: 5/5` each.
2. **Manual flow.** `python3 python/sts2_env.py 20 <char> --flow manual` for each
   character: no rejected legal actions and no `warnings` or `patch_warnings`.
3. **Trajectories.** Record with the pre-change build (`4f53751`), then compare with the
   new build:
   ```bash
   python3 tools/trajectory.py record base.jsonl   # on 4f53751
   python3 tools/trajectory.py compare base.jsonl  # on de007d1 + Phase 1–2
   ```
   A run is expected to differ only where the old build hit a `MissingMethodException` on
   one of these members. Explain each differing run from its engine warnings before
   accepting it.
4. **Test suite.** `python3 -m pytest tests -q`.

## Phase 5: docs

- CLAUDE.md, GodotStubs bullet:
  - Describe the `reachable-ui` tier and `--depth`.
  - Change "the gameplay tier must be empty" to "the gameplay and reachable-ui tiers must be
    empty".
- Delete this file once the phases have landed. The CLAUDE.md bullet carries what remains.

## Follow-ups (not needed for "done")

- **Base types that differ from GodotSharp.** `Control.PropertyName` derives
  `Node.PropertyName` (real: `CanvasItem.PropertyName`). `PropertyTweener` derives
  `object` (real: `Tweener`), and `Texture2D` derives `Resource` (real: `Texture`). Member
  lookup still works, but a cast or `is` against the real base fails. Add a base-type check
  to the audit for types sts2 references.
- **Incoherent Node2D state.** In the stub, `Position`, `GlobalPosition`, `RotationDegrees`
  and `GlobalTransform` are independent auto-properties. `GetAngleTo` uses
  `GlobalTransform`, as Godot does, so it ignores a `GlobalPosition` that was set
  separately. Making Node2D derive its transform from position, rotation and scale would fix
  that, but it can change trajectories and needs the Phase 4 compare.

## Open decisions

1. `--fail` fails on reachable-ui gaps. Implemented in Phase 1.
2. `Path2D.Curve` stays an empty curve. Phase 2's path shows why: it is read by
   `NCardFlyPowerVfx.PlayAnim` (reached from `CardModel.PlayPowerCardFlyVfx`), which calls
   `GetBakedLength` on it. Godot's null default would throw there; the empty curve gives
   length 0.
