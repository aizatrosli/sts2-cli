# flysts payload mode

`start_run {"payload": "flysts", ...}` runs the engine as a stand-in for the FlystsBridge
game mod that the flysts trainer (`~/flysts`, `flysts_agent/`) talks to. Replies carry the
mod's JSON state (`flysts_mod/GameState.cs`) and the engine accepts the mod's actions
(`flysts_mod/GameActions.cs`), so a checkpoint trained on the real game runs unchanged
(`flysts_agent/cli_env.py`, `train.py --backend cli`).

The default protocol (`decision` payloads, `legal_actions`, `action`) is unaffected. A run in
this mode is always a manual-flow run, and every engine step still goes through
`ExecuteAction` and the validation gate.

## Commands

| command | reply |
|---|---|
| `{"cmd":"start_run","payload":"flysts","character":"Ironclad","seed":"FLYS1","game_mode":"custom","profile":"<progress.save>","ascension":0}` | `{"type":"flysts","status":"ok","message":"Run started","state":{...}}` |
| `{"cmd":"flysts_state"}` | `{"type":"flysts","status":"ok","state":{...}}` |
| `{"cmd":"flysts_action","action":"play_card","args":{"card_index":0,"target":1}}` | `{"type":"flysts","status":"ok"\|"error","message"\|"error":...,"state":{...}}` |

- `profile` (required): a `progress.save`. It is loaded into `SaveManager.Progress` for every
  run (in memory; TestMode writes nothing to disk), so each seed replays identically.
- `game_mode`: `custom` (the game's seeded runs) or `standard`; default custom when a seed or
  ascension is given, standard otherwise (the flysts menus do the same).
- `seed`: optional; canonicalized like the game (`SeedHelper.CanonicalizeSeed`); a missing seed
  is rolled and reported as `state.run.seed`.
- A refused action returns `status: error` with the mod's error text and changes nothing.

## Run creation (FlystsProfile, `RunSimulator.Flysts.cs`)

Built the way the game builds a new run for that profile (`NGame.StartNewSingleplayerRun`,
`StartRunLobby.BeginRunLocally`), not like `start_run`'s test setup:

- unlock state from the profile (`SaveManager.GenerateUnlockStateFromProgress`, round-tripped
  through its serializable form as the lobby does), not `UnlockState.all`;
- the Neow and Custom-and-Seeds epochs are revealed if they are not (the mod does this on
  every boot);
- acts rolled from the seed with the game's rule, including the forced undiscovered alternative
  act that TestMode would skip (`ActModel.GetRandomList`);
- `RunState.CreateForNewRun` (the starting deck gets `AfterCreated`), the game mode as given,
  ascension capped at the character's max ascension in the profile;
- `StartedWithNeow` follows the Neow epoch (`SetStartedWithNeowFlag`), not forced;
- Standard mode applies the discovery-order boss changes (`ForceDiscoveryOrderModifications`
  around `GenerateRooms`, since TestMode would skip them);
- starting relics finalized before `Launch`, then `EnterAct(0)`.

## State (`RunSimulator.FlystsPayload.cs`)

A port of the mod's model reads: ids are `Id.Entry` (`STRIKE_IRONCLAD`), enums lower-case,
`run.floor` is `TotalFloor`, intent `value` is the total damage, `damage_vs`/`block_now` go
through `Hook.ModifyDamage`/`Hook.ModifyBlock`, `run_state` is the lite pruned save
(`GameState.PruneRunState`). Values the mod read off UI nodes are the value a settled live read
shows:

| field | headless value |
|---|---|
| `resolving` | the engine's own expression (`ActionExecutor.IsRunning \|\| !ActionQueueSet.IsEmpty`): false at a settled decision |
| `map.traveling` | false; `next_options` = travelable points sorted by (col, row) |
| event `enabled` | true (every logged live option read true; `locked` carries the lock) |
| rest options / leave `enabled` | the option's own flag, false while a chosen option resolves; leave after a choice |
| card `vars` | `ClearPreview` + `UpdateDynamicVarPreview(Normal, CurrentTarget)` as `NCard.UpdateVisuals` does, cleared again afterwards |
| hand selection (`hand_selection`, `selected`, `selectable`, `can_confirm`, `hand_prompt`/min/max) | `FlystsSelection` (below) |
| grid screens (`selected`, `selected_count`, `preview_open`, `can_confirm`, `min/max_select`, `prompt`, `enchantment(_amount)`) | `FlystsSelection`; prefs from `SelectionPrefsPatches` |
| game over `score` | `ScoreUtility.CalculateScore`; `options: ["main_menu"]` |

Grid card order is the screen's: the options' order, except the combat draw pile, which
`NCombatPileCardSelectScreen` sorts by rarity, title, then id.

The mod's non-decision screens are advanced like its `MenuAutomation`: rewards (claim the
first reward in order; potions only with a free slot; each card reward opened once, a skipped
one is left behind), treasure (open, take relic 0, proceed), Crystal Sphere (the first hidden
cell, x-major, with the current tool). The engine never parks on them.

## Selections (`FlystsSelection`)

sts2-cli answers a selection with one `select_cards`; the game's screens toggle picks and
confirm. `FlystsSelection` runs each screen's own rules (NPlayerHand simple/upgrade mode,
NDeckCardSelectScreen, NDeckUpgrade/Enchant/TransformSelectScreen, NSimpleCardSelectScreen,
NCombatPileCardSelectScreen, NChooseABundleSelectionScreen), keeps grid picks in a `HashSet`
like the screens (same result order), and sends the result through `select_cards` /
`select_bundle` when the screen would complete. In this mode the selector no longer
auto-resolves a single forced option (the game still shows that screen).

## Actions

`play_card {card_index, target}`, `end_turn`, `use_potion {slot, target}` (belt slot),
`choose_map_node {index}`, `shop_purchase {index}` (the inventory's `AllEntries` order),
`leave_shop`, `choose_event_option {index}`, `choose_rest_option {index}`, `leave_rest_site`,
`select_card_reward {card_index}`, `select_card_reward_alternative {index}`,
`select_bundle {index}`, `choose_card {card_index}`, `select_deck_card {card_index}`,
`select_hand_card {card_index}`, `confirm`, `cancel_selection`, `abandon_run`,
`set_fast_mode` (no-op). Targets are 0-based indices into the alive enemies.

## Known differences from the live game

- Timing artifacts of the live UI are absent: reads mid-resolution, map clicks during the
  travel animation, buttons disabled during animations, a relic counter held during its
  activation animation (Kusarigama's `IsActivating`).
- The Architect epilogue after the final boss is skipped (manual flow's victory).
- The Fake Merchant event reports its options as the mod does (none), so a trainer strands on
  it as it does live.
