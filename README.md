# sts2-cli

A CLI for Slay the Spire 2.

Runs the real game engine headless in your terminal — all damage, card effects, enemy AI, relics, and RNG are identical to the actual game. Everything is unlocked from the start: all characters, cards, relics, potions, and ascension levels — no timeline progression required.

![demo](docs/demo_en.gif)

## Setup

Requirements:
- [Slay the Spire 2](https://store.steampowered.com/app/2868840/Slay_the_Spire_2/) on Steam
- [.NET 9+ SDK](https://dotnet.microsoft.com/download)
- Python 3.9+

```bash
git clone https://github.com/wuhao21/sts2-cli.git
cd sts2-cli
./setup.sh      # copies DLLs from Steam → IL patches → builds
```

Or just run `python3 python/play.py` — it auto-detects and sets up on first run.

Tested with **v0.111.0 (Steam public-beta, build 24724944)**. After updating the
installed game in Steam, rerun `./setup.sh` to refresh the engine DLLs, patches,
and official English localization, then rebuild. Other game versions
may require adapter changes.

For a compatibility check, run `python3 python/play_full_run.py 5 Ironclad`
(repeat for Silent, Defect, Regent, and Necrobinder). A completed run reaches
victory or defeat; crashes, stalls, and timeouts return a nonzero exit code.

## Play

```bash
python3 python/play.py                        # interactive
python3 python/play.py --ascension 10         # Ascension 10
python3 python/play.py --character Silent      # play as Silent
```

Type `help` in-game:

```
  help     — show help
  map      — show map
  deck     — show deck
  potions  — show potions
  relics   — show relics
  quit     — quit

  Map:     enter path number (0, 1, 2)
  Combat:  card index / e (end turn) / p0 (use potion)
  Reward:  card index / s (skip)
  Rest:    option index
  Event:   option index / leave
  Shop:    c0 (card) / r0 (relic) / p0 (potion) / rm (remove) / leave
```

## JSON Protocol

For programmatic control (AI agents, RL, etc.), communicate via stdin/stdout JSON:

```bash
dotnet run --project src/Sts2Headless/Sts2Headless.csproj
```

```json
{"cmd": "start_run", "character": "Ironclad", "seed": "test", "ascension": 0}
{"cmd": "action", "action": "play_card", "args": {"card_index": 0, "target_index": 0}}
{"cmd": "action", "action": "end_turn"}
{"cmd": "action", "action": "select_map_node", "args": {"col": 3, "row": 1}}
{"cmd": "action", "action": "skip_card_reward"}
{"cmd": "quit"}
```

The engine prints `{"type": "ready", "schema_version": 2, "debug_commands": false, ...}` on
start, then answers each command line with one JSON line. All names are in English.

| command | arguments | response |
|---|---|---|
| `start_run` | `character`, `seed` (optional; random when missing, canonicalized like the game), `ascension` 0–10, `flow` `auto`/`manual`, `act1` | first decision |
| `action` | `action`, `args` (one of the decision's `legal_actions`) | next decision, or `error` |
| `get_state` | — | the current decision again |
| `get_map` | — | `map` (all nodes, edges, visited path, boss) |
| `load_save` | `path` or `json`, `flow` | decision where the save resumes |
| `write_continue_save` | `path` | `save_result` (`success`, `path`, `size`, `room_type`) |
| `quit` | `path` (optional: save first) | `quit_result`, or `save_error` if the save failed (the engine keeps running) |
| `set_player` * | `hp`, `max_hp`, `gold`, `deck`, `relics`, `potions` (ids) | `ok` |
| `enter_room` * | `type` (`monster`/`combat`, `elite`, `event`, `rest`, `shop`, `treasure`), `encounter`, `event` | decision in that room |
| `set_draw_order` * | `cards` (ids, top first) | `ok` |

\* Debug commands, accepted only when the engine starts with `--debug` or `STS2_DEBUG_COMMANDS=1`.
Any command may carry a `request_id`, which the response echoes.

Response `type`s: `ready`, `decision` (with `decision` naming the screen and `legal_actions`),
`error` (`message`; an illegal action also names the `decision`), `map`, `ok`, `save_result`,
`save_error`, `quit_result`. Responses can carry `warnings` (engine errors during the step) and,
on `start_run`/`load_save`, `patch_warnings` (Harmony patches that did not apply).

Decisions: `map_select`, `combat_play`, `card_select`, `bundle_select`, `card_reward`,
`event_choice`, `rest_site`, `shop`, `fake_merchant`, `crystal_sphere`, `game_over`, and in
manual flow `rewards` and `treasure`. [docs/transitions.md](docs/transitions.md) lists each
decision's actions and fields.

### RL / UI-faithful mode

Every decision includes `legal_actions`, a list of ready-to-send action bodies for action masking. Pass `"flow": "manual"` to `start_run` to get the game UI's screen transitions as explicit decisions. That adds a `rewards` screen (`claim_reward` / `proceed`), treasure chests (`open_chest` / `pick_relic` / `skip_relic`), Proceed after rest sites, events and shops, and the boss → next-act transition. `{"cmd": "get_state"}` re-reads the current decision.

```json
{"cmd": "start_run", "character": "Ironclad", "seed": "test", "flow": "manual"}
{"cmd": "action", "action": "claim_reward", "args": {"reward_index": 0}}
{"cmd": "action", "action": "proceed"}
```

A run has three acts, as in single player. Act 1 is Overgrowth or Underdocks, rolled from the seed the way the game's lobby does; pass `"act1": "overgrowth"` or `"act1": "underdocks"` to pin it. Act 2 is the Hive and act 3 is Glory. `context.act_id` names the current act.

`python/sts2_env.py` wraps this in a Gymnasium-style `reset()` / `step()` API with action masks, a vectorized env and crash recovery. See [docs/transitions.md](docs/transitions.md) for the full state machine.

## Tests and CI

`CLAUDE.md` lists the validation gate every change must pass (regression runs for all
characters, manual-flow random-agent runs, `pytest tests`, the legal-action fuzzer).

GitHub Actions (`.github/workflows/ci.yml`) cannot run the engine: the game's DLLs are
proprietary and cannot be stored in the repository or in CI. It runs lint, the engine-free
tests (tests that need the game skip themselves when `lib/sts2.dll` or the engine build is
missing) and the builds of `src/GodotStubs` and the audit tools.

For the full suite in CI, register a self-hosted runner on a machine with the game installed:

1. Install the runner (repository Settings → Actions → Runners → New self-hosted runner) and
   give it a label, e.g. `sts2`.
2. On that machine install .NET 9 and Python 3 with `pytest`, and run
   `./setup.sh /path/to/game/data` once so `lib/` holds the game DLLs.
3. Add a job with `runs-on: [self-hosted, sts2]` and `STS2_GAME_DIR` set to the game's data
   directory. It runs `./setup.sh` (which reads `STS2_GAME_DIR`, so a game update is picked
   up), `dotnet build src/Sts2Headless/Sts2Headless.csproj` and the commands from the
   validation gate in `CLAUDE.md`. With `STS2_GAME_DIR` set, the stub tests also compare
   against the real `GodotSharp.dll`.

## Game Logs

Every run is automatically logged to `logs/` as a JSONL file (one JSON per line), recording each game state and action with timestamps. Logs older than 7 days are cleaned up automatically.

```bash
python3 python/play.py --no-log    # disable logging
```

**When filing a bug report, please attach the relevant log file from `logs/`** — it contains the full step-by-step game state needed to reproduce the issue.

## Supported Characters

| Character | Status |
|---|---|
| Ironclad | Fully playable |
| Silent | Fully playable |
| Defect | Fully playable |
| Necrobinder | Fully playable |
| Regent | Fully playable |

## Architecture

```
Your code (Python / JS / LLM)
    │  JSON stdin/stdout
    ▼
src/Sts2Headless (C#)
    │  RunSimulator.cs
    ▼
sts2.dll (game engine, IL patched)
  + src/GodotStubs (replaces GodotSharp.dll)
  + Harmony patches (localization)
```
