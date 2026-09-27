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
- `{"cmd":"content_catalog","profile":"<progress.save>"}` (no run needed; with `profile`, no run in progress): every card,
  relic, potion (with pool, rarity and, given a profile, `unlocked` for a singleplayer run from
  it), event (per act / shared / ancient, `conditional` when it overrides `IsAllowed`, and given
  a profile `unlocked`: its act is unlocked and no unrevealed Event1-3 epoch holds it back, or
  for an Ancient, an unlocked act offers it), encounter, monster, power, and the non-mock
  enchantments and afflictions in ModelDb. flysts's `tools/cli_coverage.py` uses it as the
  coverage denominator.
- The debug commands (`enter_room`, `set_player`, `set_draw_order`, `enter_ancient`,
  `obtain_relic`, `add_card`; engine `--debug`) work in this mode: the payload re-reads the
  decision after them. A failing one returns its own error (unknown encounter, event, relic,
  card or potion) and changes nothing: `set_player` resolves every id before it touches the
  player. The last three are the game's dev console commands `ancient <id> [choice]`
  (AncientEventModel.DebugOption forces option 0, act filters aside), `relic add <id>`
  (RelicCmd.Obtain: the pickup runs, unlike `set_player`'s silent add) and `card <id> [pile]`,
  so flysts's `tools/live_capture.py` runs the same steps on the game and the engine.

## Run creation (FlystsProfile, `RunSimulator.Flysts.cs`)

Built the way the game builds a new run for that profile (`NGame.StartNewSingleplayerRun`,
`StartRunLobby.BeginRunLocally`), not like `start_run`'s test setup:

- unlock state from the profile (`SaveManager.GenerateUnlockStateFromProgress`, round-tripped
  through its serializable form as the lobby does), not `UnlockState.all`;
- the Neow and Custom-and-Seeds epochs are revealed if they are not (the mod does this on
  every boot);
- acts rolled from the seed with the game's rule, including the forced undiscovered alternative
  act that TestMode would skip (`ActModel.GetRandomList`);
- what the mod's lobby refuses is refused with its text: a character the profile has not
  unlocked (`UnlockState.Characters`: "Character 'X' is locked"), and an ascension above the
  lobby's max, which is the character's max ascension once its ascension epoch (the 4th) is
  revealed and 0 before ("Ascension N out of range (max unlocked for this character: M)");
- `RunState.CreateForNewRun` (the starting deck gets `AfterCreated`), the game mode as given,
  ascension capped at the character's max ascension in the profile;
- `StartedWithNeow` follows the Neow epoch (`SetStartedWithNeowFlag`), not forced;
- Standard mode applies the discovery-order boss changes (`ForceDiscoveryOrderModifications`
  around `GenerateRooms`, since TestMode would skip them);
- starting relics finalized before `Launch`, then `EnterAct(0)`.

## State (`RunSimulator.FlystsPayload.cs`)

A port of the mod's model reads: ids are `Id.Entry` (`STRIKE_IRONCLAD`), enums lower-case,
`run.floor` is `TotalFloor`, intent `value` is the total damage, `damage_vs`/`block_now` go
through `Hook.ModifyDamage`/`Hook.ModifyBlock` (an Osty attack, `OstyDamageVar` or
`CalculatedDamageVar.FromOsty`, with Osty as the dealer, like its card preview), `run_state` is the lite pruned save
(`GameState.PruneRunState`). Values the mod read off UI nodes are the value a settled live read
shows:

| field | headless value |
|---|---|
| `resolving` | the engine's own expression (`ActionExecutor.IsRunning \|\| !ActionQueueSet.IsEmpty`): false at a settled decision; also on the map, shop, rest site and event screens, where an out-of-combat potion resolves (`GameState.WithResolving`, 2026-09-27) |
| `map.traveling` | false; `next_options` = travelable points sorted by (col, row) |
| event `enabled` | true (every logged live option read true; `locked` carries the lock) |
| rest options / leave `enabled` | the BUTTON's state, as the mod reads it: true (a model-disabled option such as Smith with nothing to upgrade keeps an enabled, unclickable button; choosing it fails "is disabled"), false while a chosen option resolves; leave after a choice |
| rest site after a choice | the options still in `room.Options` (a chosen one leaves it when it succeeds, `RestSiteSynchronizer.ChooseOption`), then leave. The mod lists only those buttons (`GameState.RestSiteButtons`, 2026-09-27): the greyed buttons that stay until the post-select VFX ends are not reported, so the read does not depend on its timing (captured live, sim and animated) |
| card `vars` | `ClearPreview` + `UpdateDynamicVarPreview(Normal, CurrentTarget)` as `NCard.UpdateVisuals` does, cleared again afterwards. The mod runs the same refresh and puts the display values back (2026-09-27): live, in NonInteractiveMode, the hand's faces were not refreshed after a power changed |
| relic `counter` | `DisplayAmount`. The mod reads it with the relic's `_isActivating` cleared (2026-09-27): 21 relics show their threshold during the 1 s activation flash, which the engine's instant `Cmd.Wait` never shows |
| Fake Merchant | a `shop` (`fake_merchant: true`) of the event's `MerchantInventory` (six fake relics) and leave, as the mod reads his custom layout (`GameState.ActiveFakeMerchant`, 2026-09-27); `shop_purchase` buys, `leave_shop` is his Proceed (the map). After a Foul Potion starts his fight, an event with no options until the combat |
| Foul Potion outside combat | thrown with no creature, as the game's own drop on the merchant (`NPotionHolder` → `EnqueueManualUse(null)`): +100 gold in a shop, the Fake Merchant's fight at his event (captured live 2026-09-27) |
| hand selection (`hand_selection`, `selected`, `selectable`, `can_confirm`, `hand_prompt`/min/max) | `FlystsSelection` (below) |
| grid screens (`selected`, `selected_count`, `preview_open`, `can_confirm`, `min/max_select`, `prompt`, `enchantment(_amount)`) | `FlystsSelection`; prefs from `SelectionPrefsPatches` |
| game over `score` | `ScoreUtility.CalculateScore`; `options: ["continue"]`, then `["main_menu"]` after `menu_select continue` (`NGameOverScreen`: continue is the enabled button once the screen is up, and opens the summary whose end enables main menu); `main_menu` then answers that `start_run` begins the next run |
| game over `cause`, `run_time` | the mod's `NRunHistory.GetGameOverType` order (win `FalseVictory`, `AbandonedRun`, `CombatDeath`, `EventDeath`, else `None`) applied to the run, since the engine has no `RunHistory`; `RunManager.RunTime` (wall-clock seconds) |

In combat the player also carries its character resources (added to the mod and the engine
together, 2026-09-27): `orb_slots` and `orbs` `[{id, passive, evoke}]` (the orb queue in
order; `PassiveVal` / `EvokeVal`, which include Focus) and `pets` `[{id, hp, max_hp, block,
alive, powers}]` (`PlayerCombatState.Pets`: Osty, Byrdpip; not in `monsters`). Hand cards carry
`calculated_hits` (a `CalculatedHits` var's `CalculatedVar.Calculate`, computed at the read like
`OnPlay` does: Finisher, Rattle, Barrage, ...; null for other cards) and `glow_gold` /
`glow_red` (`CardModel.ShouldGlowGold/Red`: Spite after HP loss this turn, an Osty card with Osty
missing); relics carry `status` (`RelicModel.Status`: `normal`, `active` = about to trigger,
`disabled`). Captured on the game: Defect's orbs after Zap and Dualcast, Necrobinder's Osty,
Byrdpip, Finisher's hits and Spite's glow after Bloodletting.

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

A selection opened by a hook (a `HookPlayerChoiceContext`: Gambling Chip, Toolbox, Choices
Paradox, Tools of the Trade, Tyranny, Foregone Conclusion, Entropy at turn start; retain
prompts at turn end) signals its player choice before the selector, as the game's own path does
before its UI (`SignalPlayerChoiceBegun` / `SignalPlayerChoiceEnded`). The rest of the hook then
runs as a `GenericHookGameAction`, so the turn start carries on first (`AfterSideTurnStart`,
orbs, the executor unpaused) and the prompt shows that state with `resolving` true (captured
on the game: Brimstone's Strength is applied at Gambling Chip's prompt). The default protocol
keeps the selector's blocking order.

## Actions

`play_card {card_index, target}`, `end_turn`, `use_potion {slot, target}` (belt slot),
`choose_map_node {index}`, `shop_purchase {index}` (the inventory's `AllEntries` order),
`leave_shop`, `choose_event_option {index}`, `choose_rest_option {index}`, `leave_rest_site`,
`select_card_reward {card_index}`, `select_card_reward_alternative {index}`,
`select_bundle {index}`, `choose_card {card_index}`, `skip_choice` (a choose-a-card screen's
skip button, when `can_skip`), `select_deck_card {card_index}`, `select_hand_card
{card_index}`, `confirm`, `cancel_selection`, `abandon_run`, `menu_select` (only on a
`combat_rewards` / `treasure_room` / `crystal_sphere` menu, which shows only if an auto-advance
step failed: one step of it), `set_fast_mode` (no-op). Targets are 0-based indices into the
alive enemies.

`abandon_run` is `RunManager.AbandonInternal`: the player is killed (`CreatureCmd.Kill` with
force), so the game-over payload reads hp 0 and `cause: AbandonedRun` (captured on the game).

`use_potion` while a selection is open (a hand prompt, a choose-a-card or pile screen, a card
reward) enqueues the potion like the mod (`PotionModel.EnqueueManualUse`) when its usability rule
passes: the slot reads `usable: false` while queued and the potion runs after the selection
(captured on the game: Strength Potion on Attack Potion's screen).

## Known differences from the live game

- Timing artifacts of the live UI are absent: reads mid-resolution, map clicks during the
  travel animation, buttons disabled during animations, the game-over screen before its
  buttons enable (an abandon read right away shows none).
- The Architect epilogue after the final boss is skipped (manual flow's victory).
- `end_turn` while a hand prompt is open (Burning Pact's exhaust): live the mod answers ok and
  the game queues the end of turn behind the prompt (`is_play_phase` false, the prompt still
  open); the engine refuses it ("Not in play phase"). Captured on the game 2026-09-27. The
  trainer's mask offers only the hand cards and confirm during a hand prompt
  (`Policy._build_action_mask`), so no trainer action reaches this.
- Trial's "Double Down" opens the abandon-run popup, which the mod never sees or confirms: the
  page stays (Accept / Double Down) and Accept then runs the trial as usual (captured on the
  game). The default protocol still abandons the run.
