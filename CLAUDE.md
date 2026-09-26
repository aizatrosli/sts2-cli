# CLAUDE.md

## Testing Requirements

Any code change MUST pass the full validation gate before claiming completion:

```bash
dotnet build src/Sts2Headless/Sts2Headless.csproj          # 0 errors

# 1. Regression: 5 games per character, ALL must complete (0 crashes/stuck)
for char in Ironclad Silent Defect Regent Necrobinder; do
    python3 python/play_full_run.py 5 "$char" 2>&1 | grep -E "Wins|Completed"
done

# 2. Manual flow with a random agent: every listed action must be accepted
for char in Ironclad Silent Defect Regent Necrobinder; do
    python3 python/sts2_env.py 3 "$char" --flow manual --god | grep -E "rejected|Completed"
done

# 3. Tests (stub audit/diff and TestMode audit included; the real-assembly parts need
#    STS2_GODOTSHARP_DLL or STS2_GAME_DIR)
python3 -m pytest tests -q

# 4. Validation gate fuzzing: unlisted actions must be rejected without changing state
python3 tools/fuzz_legal.py --flow manual --god --steps 400
python3 tools/fuzz_legal.py --flow auto --god --steps 400
```

Expected: `Completed: 5/5` for every character, `Completed: 3/3` with `rejected=0` on every
env run, all tests passing, and `"violations": 0` from the fuzzer.

## Localization

- The repo is English-only. Names and text come from the game's official English tables (`localization_eng/`, refreshed by `scripts/extract_localization.py`); never invent names — look them up
- Template variables like `{Damage}`, `{Block}`, `{MaxHp}` must be resolved to actual values before display

## Build

```bash
dotnet build src/Sts2Headless/Sts2Headless.csproj
```

The Python tools find dotnet through `python/engine.py` (`$DOTNET`, `$DOTNET_ROOT`,
`~/.dotnet`, `~/.dotnet-arm64`, then `PATH`); `setup.sh` checks the same places.

## Key Architecture

- `src/Sts2Headless/RunSimulator.cs` — game lifecycle, decision point detection, state serialization
- `src/Sts2Headless/Program.cs` — JSON command router
- `src/GodotStubs/` — replacement GodotSharp.dll (no-op Godot types)
- `tools/GodotStubAudit/`, `tools/GodotStubDiff/` — stub coverage audit and value-type differential test
- `python/play.py` — interactive terminal player
- `python/play_full_run.py` — batch testing tool (auto flow)
- `python/sts2_env.py` — Gymnasium-style RL wrapper (`Sts2Env`, `Sts2VecEnv`) + manual-flow random-agent test
- `python/engine.py` — shared dotnet resolver and engine command line
- `src/Sts2Headless/RunSimulator.Flow.cs` / `RunSimulator.LegalActions.cs` — manual (UI-faithful) flow, legal action enumeration
- `lib/` — game DLLs (not in repo, copied by setup.sh)
- `localization_eng/` — official English loc data

## Conventions

- **Event completion**: trust `localEvent.IsFinished`. Never gate on "option count unchanged after a choice" — events legitimately loop on the same page (Slippery Bridge Hold On) and post-selection continuations (heal/enchant/add-card) often run on the same options page; force-finishing kills them silently.
- **Async selection continuations**: any path whose effect can open a card_select / card_reward / bundle (event option, shop relic pickup, etc.) must run on `Task.Run(...)` and yield as soon as `_cardSelector.HasPending` / `HasPendingReward` / `_pendingBundles != null` appears. The Task completes naturally once the external `select_cards` feeds the selector's TCS. Reference shape: `DoChooseOption`, `DoBuyRelic`.
- **Continue saves inside a room**: `select_map_node` snapshots the map state just before entering the node (`CaptureRoomEntryCheckpoint`), tagged with `sts2cli_resume_map_coord`. `write_continue_save` inside that room writes the snapshot; `load_save` strips the tag, restores the map, and re-enters the node, so the same encounter/event comes back (like the game's `LoadIntoLatestMapCoord`). Rooms not entered via the map (debug `enter_room`, act transitions) fall back to the rolled-back map save. `load_save` calls `EnterAct` to rebuild the map, which resets the "?" node odds to base; `LoadSave` restores the saved odds afterwards, or a resumed "?" room rolls an event instead of its shop/fight/chest.
- **DynamicVar preview during serialization**: `UpdateDynamicVarPreview` mutates the live card. Bracket reads with `ClearPreview` **before and after** — leaving the card in preview state corrupts subsequent play actions (Momentum Strike `PlayCardAction` failure).

- **Manual flow** (`"flow": "manual"`, `RunSimulator.Flow.cs`): rewards go through the engine's own `RewardsSet.Offer()` → `RewardsSet.testSelector`, which parks the set as an open rewards screen; claims call `RewardsSetSynchronizer.SelectLocalReward` on `Task.Run` (card rewards block inside it). Room actions that can wait on the player (event options, rest-site options, chest opening, relic pickup) are tracked with `TrackBackground`; after any input resolves, return `ResumeBackgroundWork()` so parked work finishes before the next decision is exported. Keep auto-flow behavior unchanged; gate new behavior on `_manualFlow`.
- **Selections outside the play phase**: enemy moves can open a card selection mid enemy turn (Knowledge Demon's Curse of Knowledge). `DoEndTurn` must surface it, and resolving it must go through `ResolveSelectionAndResumeTurn` so the enemy turn continues with `SuppressYield`.
- **Null UI singletons**: `NGame.Instance`, `NEventRoom.Instance`, `NCombatRoom.Instance` etc. are null headless. When game logic dereferences one unguarded (event options, monster death hooks), the option throws halfway and the event loops. Fix it with a targeted Harmony patch (`HeadlessUiPatches.cs` transpilers, or `PatchCosmeticNoOp` for purely visual methods), not a flow special case. Missing GodotStubs members show up as `MissingMethodException` in the turn loop — add them to `src/GodotStubs` (see GodotStubs below).
- **GodotStubs**: match GodotSharp's signatures exactly (name, parameter and return types — sts2 binds by signature, so `void` vs `Error` or `T` vs `in T` is a miss) and its enum underlying types and constants (all engine enums are `long`; sts2 compiles the values in). Value types (`Vector2`, `Rect2`, `Transform2D`, `Color`, `Mathf`, …) must compute like Godot; visuals are no-ops. After changing stubs or updating the game, run:
  - `dotnet run --project tools/GodotStubAudit -- --reference <game dir>/GodotSharp.dll` — every GodotSharp type/member sts2.dll references that the stub lacks, grouped gameplay / reachable-ui / tooling / UI, plus the real declaration and enum mismatches. `reachable-ui` = used only by UI code, but by a UI method that gameplay calls within `--depth` call edges (default 2); each is printed with a gameplay → UI call path. The gameplay and reachable-ui tiers must be empty (`--fail` exits 1 otherwise). The tiering is tested on `tests/fixtures/godot_audit/` (no game needed).
  - `dotnet run --project tools/GodotStubDiff -- --real <game dir>/GodotSharp.dll` — fuzzes stub value types against the real assembly; must report 0 mismatches.
  - `tests/test_godot_stubs.py` runs both (the real-assembly parts need `STS2_GODOTSHARP_DLL` or `STS2_GAME_DIR`).
  - `scripts/verify_godot_stubs.sh` runs all of the above plus the regression gate, the manual-flow check and an old-vs-new trajectory comparison, and prints a pass/fail summary (`--quick` for the stub checks only).
- **legal_actions** (`RunSimulator.LegalActions.cs`) is computed from the exported decision and live state. Every entry it lists must be accepted by `ExecuteAction`; `python3 python/sts2_env.py N <char> --flow manual [--god]` reports any rejection.
- **Validation gate** (`RunSimulator.Validation.cs`): `ExecuteAction` only runs actions in the last exported legal set (always on, no opt-out). Anything else returns an error before touching state. Handlers still check their own inputs (defense in depth). A new action must be added to `ComputeLegalActions`, or the gate rejects it. `tools/fuzz_legal.py` probes unlisted actions and checks that rejected ones change nothing.
- **Selection prefs** (`SelectionPrefsPatches.cs`): prefixes on `CardSelectCmd.From*` record `canSkip` and `CardSelectorPrefs` (cancelable, prompt) because the selector hook drops them.
- **TestMode**: the engine runs with `TestMode.IsOn`. Gameplay methods that branch on it are forced to the real-game branch in `TestModePatches.cs`. `dotnet run --project tools/TestModeAudit -- --fail` checks every TestMode read in gameplay code against `tools/TestModeAudit/allowlist.txt`. After a game update, review each new site: patch it if it changes gameplay, otherwise allowlist it.
- **Behavior-neutral changes** (speed-ups, refactors): record seeded trajectories with the old build (`python3 tools/trajectory.py record base.jsonl`), rebuild, then `python3 tools/trajectory.py compare base.jsonl` must report every run identical. `python3 tools/bench.py` measures steps/s (about 245 after Phase 4, 175–180 with Phase 5's rendered text).
- **Waiting on the engine**: poll with `PollUntil` / `PollPause` (wall-clock deadline, yield then 1 ms), and wait on the actual task or `EngineIdle()`, never a fixed `Thread.Sleep`. A fixed sleep hides races, so the observation can be stale when the machine is slow (see the `skip_select` fix).
- **Tiered compilation is off** (`Sts2Headless.csproj`): with it on, a Harmony-patched method recompiled at tier 1 in the background could run unpatched later in a long process (seen: `MerchantPotionEntry.CalcCost`'s price roll, `FromDeckGeneric`'s prefs prefix). Keep it off; a nondeterminism that appears only after many runs in one process points here first.
- **Patch health** (`PatchReport.cs`): patch failures and expected-count mismatches go through `PatchReport.Warn/Expect` and reach clients as `patch_warnings`; background engine failures go through `PatchReport.EngineWarning` and reach clients as `warnings`. When adding a patch that matches N sites, add an `Expect`.
- **flysts payload mode** (`start_run {payload: "flysts"}`, `RunSimulator.Flysts*.cs`, `docs/flysts_payload.md`): emits the FlystsBridge mod's payload and takes its actions for the flysts trainer. Runs are built from a `progress.save` profile like the game's lobby (not `UnlockState.all`); UI state the mod read (selections, previews, confirm buttons) is modeled in `FlystsSelection`; the mod's auto-advanced screens (rewards, treasure, Crystal Sphere) are stepped through native actions. Fidelity is measured against logged live games with flysts's `tools/cli_parity.py` (every residual diff is classified; `tools/cli_determinism.py`, `cli_fuzz.py --god`, `cli_coverage.py` with `content_catalog` cover determinism, soak and content); keep the default protocol unchanged. UI state the mod reads off nodes follows the node, not the model (a rest button's enabled state, the greyed buttons after a Smith, no options on the Fake Merchant's custom layout). Hook-opened selections signal their player choice (the turn start runs on before the prompt), and potions used during a selection are queued, as live.
- **Map travel**: `TravelablePoints` mirrors `NMapScreen.RecalculateTravelability` (Ancient-only at act start, boss/second boss, `MapTravel.GetTravelablePointsFrom`). Healing between acts is the Ancient's own; the adapter adds none.

## Protocol notes

- `card_select` decision uses key `cards` (not `options`) and action `select_cards` with comma-separated `indices`.
- `AnyEnemy` cards/potions require `target_index` when ≥2 enemies are alive; with a single alive enemy the adapter auto-targets.
- `get_state` re-exports the current decision without acting.
- Observation text goes through the engine's own renderers (`RunSimulator.Serialize.cs`); card/relic/potion/power fields come from the shared `CardInfo`/`RelicInfo`/`PotionInfo`/`PowerInfo`. Field list: `docs/transitions.md` (schema_version 2).
- The Fake Merchant event is a `fake_merchant` decision (relic shop; Foul Potion starts his fight). Its Foul Potion throw and the shop throw are patched in `HeadlessUiPatches.cs`.
- `start_run` rolls act 1 (Overgrowth/Underdocks) from the seed like the real lobby (`ActModel.GetRandomList` with the `act_selection` RNG); optional `act1` = `random|overgrowth|underdocks`. Repeated `start_run`/`load_save` in one process must go through `CleanUp()` + `ResetRunScopedState()`; engine singletons (card selector scope, CombatManager event handlers) are registered once per process.
- The Crystal Sphere event's minigame screen is replaced by a `crystal_sphere` decision (`crystal_sphere_divine {x,y,tool}`) in both flows.
- Manual flow adds decisions `rewards` and `treasure` and actions `claim_reward`, `proceed`, `open_chest`, `pick_relic`, `skip_relic`, `select_card_reward_alternative`. See `docs/transitions.md`.
