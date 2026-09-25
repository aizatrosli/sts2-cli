# Screen transitions & RL interface

The JSON protocol can run in two **flows**, chosen per run with the `flow` field of
`start_run` / `load_save`:

| flow | behavior |
|---|---|
| `auto` (default) | Legacy behavior. Gold/potion/relic rewards are auto-collected, treasure relics auto-picked, and the adapter moves to the map after rest sites, events, shops and card rewards. Existing clients (`play.py`, `play_full_run.py`) use this. |
| `manual` | Every transition the game UI makes you click through is an explicit decision: rewards screen, card reward, treasure chest, Proceed buttons, act change. Use this for RL, so the agent faces the same choices a player does. |

```json
{"cmd": "start_run", "character": "Silent", "seed": "abc", "ascension": 0, "flow": "manual"}
```

## `legal_actions`

In both flows every `"type": "decision"` response has a `legal_actions` list. Each entry is
exactly the body of an `action` command, so an agent can send it back unchanged:

```json
"legal_actions": [
  {"action": "play_card", "args": {"card_index": 0, "target_index": 1}},
  {"action": "use_potion", "args": {"potion_index": 0}},
  {"action": "end_turn"}
]
```

`select_cards` (choose k of n) is combinatorial, so it appears as one **template** entry with
`"template": true`, `min_select`, `max_select` and `num_cards`. Fill in `args.indices`
(comma-separated) before sending it.

`{"cmd": "get_state"}` returns the current decision point again without acting. Use it to
re-sync after an `error` response.

## Manual flow state machine

```
                 ┌────────────── select_map_node ───────────────┐
                 ▼                                              │
  map_select ──► combat_play ──(combat won)──► rewards ──proceed─┤
      ▲             │  ▲                        │  ▲            │
      │   end_turn/ │  │ play_card/use_potion   │  │ claim_reward (card)
      │   ...       ▼  │                        ▼  │            │
      │         card_select                   card_reward       │
      │                                                         │
      ├── event_choice ── ... ── (finished) Proceed (option 0) ──┤
      ├── rest_site ── choose_option ── [more options] ── proceed ┤
      ├── shop ── buy_* / remove_card ── proceed ─────────────────┤
      └── treasure ── open_chest ── pick_relic | skip_relic ── proceed
  boss rewards ── proceed ──► next act (Ancient event) | 2nd boss (map) | game_over(victory)
```

### Decisions and their actions (manual flow)

| decision | what it mirrors | actions |
|---|---|---|
| `map_select` | map screen | `select_map_node {col,row}` |
| `combat_play` | your turn in combat | `play_card {card_index[,target_index]}`, `use_potion {potion_index[,target_index]}`, `discard_potion {potion_index}`, `end_turn` |
| `card_select` | any card choice screen (upgrade, remove, choose-a-card, including mid-enemy-turn choices like Knowledge Demon's curse) | `select_cards {indices}`, `skip_select` when `min_select` is 0 |
| `bundle_select` | Scroll Boxes pack choice | `select_bundle {bundle_index}` |
| `rewards` | the rewards screen (after combat, after opening a chest, rest-site/event reward sets) | `claim_reward {reward_index}`, `proceed`, and `discard_potion` when a potion reward is blocked by full slots |
| `card_reward` | card reward screen opened by claiming a card reward | `select_card_reward {card_index}`, `skip_card_reward`, `select_card_reward_alternative {alternative_index}` (e.g. Reroll) |
| `event_choice` | event / Ancient dialogue | `choose_option {option_index}`. A finished event shows one option `{"index": 0, "is_proceed": true}`. |
| `rest_site` | campfire | `choose_option {option_index}`; `proceed` once `can_proceed` is true (after a successful choice). Options stay available when a relic allows more than one. |
| `shop` | merchant | `buy_card`, `buy_relic`, `buy_potion`, `remove_card`, `proceed` |
| `treasure` | treasure room | `open_chest`, then `pick_relic {relic_index}` / `skip_relic`, then `proceed` (`proceed` while the relic is on offer skips it) |
| `crystal_sphere` | Crystal Sphere event minigame (both flows) | `crystal_sphere_divine {x, y, tool}` with `tool` `big` (3×3) or `small` (1 cell), on a hidden cell. `grid[y][x]` is `"?"` for hidden, `""` for cleared, or the revealed item type (`relic`, `potion`, `card_reward`, `curse`, `gold`). Uncovered items are granted as a rewards screen once `divinations_left` reaches 0. |
| `game_over` | death / victory | none |

`leave_room` is accepted as an alias of `proceed` in manual flow.

### Rewards screen details

* `rewards[i].type` is one of `gold`, `potion`, `relic`, `card`, `special_card`, `card_removal`, `linked`.
* Card reward contents stay hidden until the reward is claimed, as in the UI.
* A claimed reward leaves the list. A card reward that was skipped stays, so you can reopen it.
* A potion reward is `"enabled": false` while potion slots are full. Discard a potion or leave it.
* `is_terminal: true` is the room-end screen after combat. Its `proceed` leaves the room:
  it goes to the map, resumes the parent event after an event fight, or changes act after a boss.
  Other reward screens (chest extras, rest-site or event rewards) close by themselves once
  everything is claimed. Their `proceed` skips what is left and returns to the room.

### Act transitions

After a boss, `proceed` on the rewards screen:

* in a double-boss act (Ascension 10, final act) after the first boss: back to the map, whose only choice is the second boss;
* in the final act: `game_over` with `victory: true`. The engine's TheArchitect epilogue is dialogue only and is skipped;
* otherwise: enters the next act, which starts with its Ancient event.

## Python wrapper

`python/sts2_env.py` wraps the protocol in a Gym-style API (no gymnasium dependency):

```python
from sts2_env import Sts2Env

env = Sts2Env(flow="manual")
obs, info = env.reset(character="Defect", seed="42", ascension=0)
done = False
while not done:
    action = obs["legal_actions"][0]          # or an index: env.step(0)
    obs, reward, terminated, truncated, info = env.step(action)
    done = terminated or truncated
env.close()
```

The default reward is +1 for victory, -1 for defeat and 0 otherwise. Pass `reward_fn=` to shape it.
Running the module is a random-agent smoke test that uses only `legal_actions`:

```bash
python3 python/sts2_env.py 5 Ironclad --flow manual          # normal HP
python3 python/sts2_env.py 3 Silent --flow manual --god      # 9999 HP, reaches acts 2-3
```

A run that ends with `rejected=0` means every legal action the adapter offered was accepted.
