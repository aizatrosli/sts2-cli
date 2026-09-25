# Gap remediation plan

Source: the seven-area gap analysis of HEAD 447e141 (manual-flow fidelity, legal_actions,
robustness, content sweep, observations, stub/patch fidelity, RL API/tests/docs). Finding
numbers (F1…F26) refer to the ranked summary of that analysis. Repro scripts written during
the analysis live in the session scratchpad under `gap/<area>/`; Phase 0 moves the useful
ones into the repo as tests.

Each phase ends with the same gate (see "Validation gate" at the bottom) and one commit,
pushed to the working branch. Phases 1–3 fix wrong behavior and should land before any
observation/schema work, because they change trajectories and would invalidate data
collected on top of them.

| Phase | Theme | Findings | Size |
|---|---|---|---|
| 0 | Test harness that can catch these bugs | F13, F25 (part) | S |
| 1 | Action validation gate | F1, F2, F12 (part), F21 (part) | M |
| 2 | Game-rule fidelity | F3–F6, F8–F10, F15 (Jungle Maze), F21 (Winged Boots) | L |
| 3 | Robustness | F7, F11, F12, F26, patch self-check | M |
| 4 | Throughput | F20 | S–M |
| 5 | Observations and schema | F16–F19, F15 (FakeMerchant) | L |
| 6 | RL wrapper | F14 | M |
| 7 | Tooling, docs, CI, play.py | F22–F25 | M |

---

## Phase 0 — test harness first

Goal: every later fix lands with a test that failed before it.

- `tests/conftest.py`: drain the engine's stderr on a thread (or send it to a log file)
  instead of an unread `PIPE` — the pipe fills after ~600 steps and the engine blocks (F13).
  Same fix in `python/play.py:1415`.
- Add a read timeout to `Game._read` (raise instead of hanging the suite); add
  `pytest-timeout` if available, register the `slow` marker.
- Launch the built `Sts2Headless.dll` with `dotnet <dll>` instead of `dotnet run --no-build`
  in the test fixture (0.8 s → 0.12 s per game; tests get faster and stop depending on MSBuild).
- One shared dotnet resolver for tests (`DOTNET`, `DOTNET_ROOT`, `~/.dotnet`,
  `~/.dotnet-arm64`, `PATH`).
- `tests/test_gaps.py`: port the analysis repros as tests, marked `xfail(strict=True)`
  until their phase lands (strict so a fix that lands early flips them to failures and forces
  the marker off): map teleport, select_cards over max, skip on min>0, Knowledge Demon skip,
  Abundance skip, Ancient skip, double heal, repeated shop removal, chained selection wedge,
  CallingBell/Cauldron fixed loot, Neutralize + Vigor, potion destroyed on map, JUNGLE_MAZE
  Join Forces, ghost play_card after an error.
- `tools/fuzz_legal.py`: the lockstep legal-action fuzzer from the analysis (probes unlisted
  actions, checks determinism). Short version runs in pytest; long version is a manual tool.

## Phase 1 — action validation gate

Goal: `legal_actions` becomes the contract. Anything not in it returns `type: error` and
changes nothing.

Design:
- `AttachLegalActions` already runs after every command; keep the exported decision name and
  legal list as `_lastExport`. `get_state` refreshes it. Debug commands (`set_player`,
  `enter_room`, `set_draw_order`, `load_save`) invalidate it; if it is missing, recompute
  from `DetectDecisionPoint()` before validating.
- `ExecuteAction` checks the incoming `{action, args}` against `_lastExport` before dispatch:
  - exact match on action + args after normalization (ints from strings/floats with integral
    value, `leave_room` ≡ `proceed`, `target_index` absent ≡ the single listed target);
  - `select_cards` template: indices must parse, be distinct, in `[0, num_cards)`, and
    `min_select ≤ k ≤ max_select`;
  - otherwise `Error("Illegal action for decision <d>: …")` with the legal list attached.
- Keep per-handler checks as defense in depth (the gate only works if the exported list is
  right):
  - `DoMapSelect` (`RunSimulator.cs:932`): decision must be `map_select`, coord must be in the
    computed choices; validate before `ResetRoomFlowState()`. Reject `col`/`row` outside byte.
  - `HeadlessCardSelector.ResolvePendingByIndices` / `DoSelectCards` / `DoSkipSelect`:
    strict count/range/duplicate checks; `skip_select` only when `min_select == 0` (or
    cancelable, Phase 2).
  - `DoPlayCard`: reject while a selection is pending or outside the play phase, before any
    `PlayCardAction` is queued (fixes the ghost play).
  - `select_bundle`, `select_card_reward`, auto-flow `choose_option`, `buy_potion` with full
    slots, `end_turn` outside combat, `skip_*` with nothing pending: return errors instead of
    substituting or no-oping.
- `start_run` / `load_save`: validate character, ascension (0–10), seed type, act1, flow and
  save JSON *before* `CleanUp()`, so a rejected command keeps the current run.
- Always on, no opt-out. Repo clients that send unlisted actions (`play.py`,
  `play_full_run.py`, `agent/`) are fixed in this phase to send only listed actions.

Tests: the Phase 0 exploit tests flip to passing; the short fuzzer asserts zero accepted
unlisted actions and zero state change on rejected ones.

## Phase 2 — game-rule fidelity

Each item is small; land them as separate commits inside the phase.

1. **Ancient skip** — `MapSelectState` (`RunSimulator.cs:~2183`): with no visited coord
   in the act, offer only `StartingMapPoint`. Use `MapTravel.GetTravelablePointsFrom`
   for the general case, which also brings Winged Boots / Flight free travel (F21).
2. **Double heal** — delete `HealBetweenActs` and its three call sites
   (`Flow.cs:352`, `RunSimulator.cs:1883`, `:2580`); the Ancient's
   `BeforeEventStarted` heals. Test: HP at the act-2 map equals HP after the boss; the
   Ancient heals 100% (A0–1) / 80% (A2+) of missing HP.
3. **Mandatory and cancelable selections** — Harmony prefixes on the `CardSelectCmd.From*`
   entry points capture `canSkip` and `CardSelectorPrefs` (`Cancelable`, `Prompt`,
   `RequireManualConfirmation`) into `HeadlessCardSelector` before the selector is called:
   - `FromChooseACardScreen` with `canSkip == false` → `min_select = 1`
     (Knowledge Demon, Toolbox, Abundance);
   - `Cancelable` (Smith, Cook, shop removal) → list `skip_select` and document it as
     "cancel", which returns to the parent screen without spending anything;
   - stop auto-resolving single-option selections when the prefs ask for manual
     confirmation or allow cancel.
   (The prompt is exported in Phase 5; it is captured here.)
4. **Shop card removal** — call `removal.SetUsed()` after a successful removal in both
   flows; `CardShopRemovalsUsed` then prices the next shop.
5. **TestMode gameplay branches** — `CallingBell.GenerateRewards`,
   `Cauldron.GenerateRewards`, `MerchantPotionEntry` price jitter: transpile the
   `TestMode.IsOn`/`IsOff` check in those three methods to the real-game value.
   Add `tools/TestModeAudit` (Cecil): list every `TestMode.Is*` read outside `Nodes.*`,
   diff against a checked-in allowlist, fail on new sites — game updates add them silently.
   Note: fixing the shop RNG changes every seeded shop after the first potion; regenerate any
   baselines.
6. **Neutralize** — delete the `Neutralize.OnPlay` prefix (`RunSimulator.cs:3895–3999`);
   the original now runs headless. Test: Vigor is consumed by the first attack.
7. **Potions outside combat**
   - legal actions: on every non-combat decision, list `use_potion` for
     `Usage == AnyTime && PassesCustomUsabilityCheck` and `discard_potion` when
     `CanUseOrRemovePotions` (mirrors `NPotionPopup`);
   - `DoUsePotion`: apply the same predicate, return an error instead of silently
     discarding when the engine cancels, pass a null target for `TargetedNoCreature`;
   - Foul Potion at a merchant: make its merchant check work headless (patch the
     `NMerchantRoom` test to use the current room type).
8. **Null UI singletons** — add `JungleMazeAdventure.SafetyInNumbers` and
   `DigRestSiteOption.DoLocalPostSelectVfx` to the `HeadlessUiPatches` null-safe
   `NDebugAudioManager` call targets; guard `NCapstoneContainer.Instance.Close` in
   `RunManager.AbandonInternal` (Trial Double Down).
9. **Localization patches** — `LocTable.GetRawText` returns the key only when the entry is
   missing; drop the `HasEntry`/`IsLocalKey`/`LocString.Exists` overrides now that the real
   tables load (affects Ancient/Architect dialogue selection RNG).
10. **Seeds** — canonicalize with `SeedHelper.CanonicalizeSeed` as the lobby does, and echo
    the canonical seed; regenerate seed baselines.

## Phase 3 — robustness

1. **Chained selection wedge** — in `ResolvePending`/`CancelPending` capture the TCS,
   clear the fields, then `TrySetResult`; create selector TCSs with
   `RunContinuationsAsynchronously`. Audit the other TCS/pending pairs (card reward,
   bundles, rewards) for the same order.
2. **Memory leak** — stop `MockGodotFileIo` from recording writes (Harmony) or turn
   progress saving off after init. Test: RSS after 200 resets stays within +30 MB.
3. **Timeouts** — replace iteration-count sleep loops with `Stopwatch` deadlines; on
   expiry return an error or a decision with `"warnings": ["engine_stuck: …"]`, never a
   fabricated `game_over` (`DoEndTurn` last resort, `RunSimulator.cs:1224`). The card
   reward wait (`_rewardWait`, 300 s) waits until resolved or reset.
4. **Background failures** — auto-flow `DoChooseOption` reads `options[i]` on the main
   thread before `Task.Run`; failed background tasks surface as `warnings` on the next
   response instead of an unobserved-exception log.
5. **Thread safety** — `InlineSynchronizationContext`: lock or `ConcurrentQueue`,
   volatile `_executing`.
6. **stdout hygiene** — keep the real stdout for protocol lines and point `Console.Out` at
   stderr at startup (Sentry prints to stdout).
7. **Startup self-check** — every Harmony/transpiler patch reports its expected match count;
   a mismatch exits non-zero with the patch name. `setup.sh` IL patch: fail when the patch
   count is not the expected one, drop the dead YieldAwaiter patch, and evaluate replacing
   the `WaitUntilQueueIsEmpty…` IL rewrite with a Harmony prefix.
8. **Temp files** — delete MonoMod's `/tmp/mm-exhelper.so.*` after load or on exit.
9. **Debug command** — `set_player relics` adds relics through the owner-aware path
   (`RelicCmd.Obtain` / `AddRelicInternal`) and validates ids before clearing.
10. **Hypothesis to verify first:** Godot `CallDeferred` runs inline in the stub; if a test
    shows re-entrancy in `ActionQueueSet` cancel loops, queue deferred calls and drain them
    in `Pump`.

## Phase 4 — throughput

Target: ≥ 250 steps/s single env (now ~85).

- `SettleCombatActions` (`RunSimulator.cs:1583`): return as soon as the executor is idle
  and nothing is pending, instead of requiring 8 idle polls with `Sleep(2)`; wait on
  completion signals where the engine exposes them.
- Same treatment for `WaitForActionExecutor`, `WaitForTaskOrPending` and event-option
  waits (Phase 3 deadlines make this safe).
- Python clients launch the dll directly.
- Benchmark script in `tools/` (steps/s, p50/p95 per action) and a number in the docs.

## Phase 5 — observations and schema

Bump a `schema_version` in every response; keep old field names as aliases for one version
where a field is renamed.

1. **Formatted text** — initialize SmartFormat (`LocManager.LoadLocFormatters()` plus
   culture fields) and render text with the engine's own paths: cards
   `GetDescriptionForPile` / `GetDescriptionForUpgradePreview`, relics
   `DynamicDescription`, powers `SmartDescription`, events
   `DynamicVars.AddTo(option.Title/Description)` + `GetFormattedText()`. Strip BBCode
   (fix the regex for tags with spaces). Keep the raw template as `description_raw`.
2. **Events** — per-option formatted title/description, per-option vars, and
   `offers: [{kind, id, name}]` from the option hover tips.
3. **card_select** — `prompt` (captured in Phase 2), `source_pile`, `cancelable`, and a
   `combat` block (hand, enemies, energy, piles, powers) when the selection opens in combat.
4. **Combat state** — `draw_pile` (sorted, so order stays hidden), `discard_pile`,
   `exhaust_pile`, counts; enemy `id` and a combat-stable `slot`; intents with
   `card_count`, label/description; powers with `id`, `type`, model `Title`/description;
   `osty.powers`; `orb_slots` always.
5. **Shared serializers** — one `SerializeCard/Relic/Potion/Power` used by every decision:
   stable `id`, `upgraded`/`upgrade_level`, `base_cost`, `costs_x`, `unplayable`,
   enchantment/affliction everywhere; relic `counter`/`used_up`; potion slots including empty
   ones and capacity.
6. **Context** — `ascension`, `seed`, `flow`, `total_floor`, `second_boss`.
7. **Damage preview** — use `CalculatedHits`; `hits_known: false` for conditional or
   text-only repeats.
8. **Rest site, rewards, shop** — localized rest option title/description/disabled reason;
   `special_card` card, linked children, reward descriptions; shop `price` vs card `cost`.
9. **Names** — boss from `EncounterModel.Title`; fix `Test Subject #C{Count}` and
   temporary-power keys via the formatted text path.
10. **Map** — compact full map (nodes, edges, visited path, boss) in `map_select`.
11. **FakeMerchant** — `fake_merchant` decision over `FakeMerchant.Inventory` plus a
    "throw Foul Potion" action (starts its fight).
12. **Debug commands** — accepted only when the engine starts with `--debug` (or an env
    var); `sts2_env --god` passes it.

## Phase 6 — RL wrapper (`python/sts2_env.py`)

- Default seed: random when omitted; echo `seed`, `ascension`, `act1`, `flow` in the
  `start_run` response, `context` and `info`.
- Gymnasium-shaped: `reset(*, seed=None, options=None)` with character/ascension/act1 in
  `options` (keep the old keywords as a shim), `info` carries `legal_actions` and an
  `action_mask` over the listed actions, accept numpy integers, `metadata`.
- `step(int)` never picks a template blindly: templates sort last, and an index on a template
  raises a clear error; add a `select_cards` helper.
- Process lifecycle: launch the dll directly with `start_new_session=True`, kill the group on
  `close()`, context manager, `__del__`, atexit; after a timeout or engine death mark the env
  broken so the next call must `reset()` (which respawns); request ids echoed by the engine to
  drop late replies.
- `SubprocVecEnv`-style pool for N parallel envs.
- Optional reward shaping hooks (floor, act, HP), off by default.
- Tests for the wrapper, including a short god-mode random run per character asserting
  `rejected == 0`.

## Phase 7 — tooling, docs, CI, play.py

- `CLAUDE.md`: fix the regression loop (`done`), drop the macOS `STS2_GAME_DIR`, add the
  manual-flow god run, pytest and the fuzzer to the checklist; document the validation gate.
- `setup.sh`, `tests/*`, `agent/sts2_bridge.py`: shared dotnet resolver; bridge uses
  `src/Sts2Headless/Sts2Headless.csproj`.
- `agent/combat_helper.py:115`: potion name is a string (missed in the English-only change).
- `play.py`: shop input accepts `c0`/`r0`/`p0` and validates like other prompts; `leave leave`
  help text; `--continue` logs the right character; investigate Neow re-offer after continue;
  display the engine-formatted text from Phase 5 instead of its own template renderer.
- Docs: full decision list in README; document `load_save`, `write_continue_save`,
  `get_map`, `quit {path}` and the debug commands; response types.
- GitHub Actions: lint + tests that do not need game DLLs (the proprietary DLLs cannot go in
  CI); document a self-hosted runner for the full suite.
- `GodotStubAudit`: add a "gameplay-reachable UI" tier (UI methods reachable from
  gameplay within 2 call edges).
- Coverage additions: act transition and victory, A10 double boss, `load_save` in manual
  flow, potions in and out of combat, `select_card_reward_alternative`, events that start
  combats, Crystal Sphere to completion.

---

## Decisions needed before the affected phase

| Decision | Phase | Choice |
|---|---|---|
| Validation gate on by default (breaks clients that send unlisted actions) | 1 | **Decided: always on, no opt-out** |
| Canonicalize seeds like the lobby (`SeedHelper.CanonicalizeSeed`) so seeds match the real game; changes every existing seed's run | 2 | **Decided: yes** (Phase 2, item 10) |
| Cancel on cancelable selections reuses `skip_select` or adds `cancel_select` | 2 | Recommendation: reuse `skip_select` (no new action) |
| Breaking schema changes (renames, `null` → `[]`) | 5 | Add fields now, rename behind `schema_version` 2 |
| Sequential card selection for RL (pick one, then confirm) vs index-set template | 6 | Keep template in protocol; offer sequential mode in the wrapper |

## Out of scope / accepted

- Meta-progression (Wongo points, Vakuu visits, Architect dialogue by total wins): headless
  profile stays empty.
- Victory skips `RunManager.WinRun` / run history; no player decisions are lost.
- `play_full_run.py`'s weak AI (0 wins) is a test driver, not an engine bug.

## Validation gate (every phase)

1. `dotnet build src/Sts2Headless/Sts2Headless.csproj` — 0 errors.
2. `pytest tests` — all pass, Phase 0 `xfail`s for the phase flipped to real tests.
3. `python3 python/play_full_run.py 5 <char>` for all five characters — `Completed: 5/5`.
4. `python3 python/sts2_env.py 3 <char> --flow manual --god` for all five — completed,
   `rejected=0`.
5. `tools/fuzz_legal.py` short run — 0 accepted unlisted actions, deterministic replay.
6. GodotStubAudit `--fail` (and TestModeAudit from Phase 2 on).
