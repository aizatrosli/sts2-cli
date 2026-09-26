using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2Headless;

/// <summary>
/// The flysts payload: the JSON state the FlystsBridge game mod (flysts_mod/GameState.cs) builds
/// for its trainer, produced from the same model objects headless. Every model read is ported as
/// the mod does it (ids = Id.Entry, lower-case enums, TotalFloor, intent totals, damage_vs through
/// Hook.ModifyDamage, ...). Values the mod read off UI nodes come from FlystsSelection / the flow
/// state instead, with the value a settled live read shows.
/// </summary>
public partial class RunSimulator
{
    // ---- run layer -------------------------------------------------------

    private static Dictionary<string, object?> FlystsRunInfo(RunState runState) => new()
    {
        ["act"] = runState.CurrentActIndex + 1,
        ["floor"] = runState.TotalFloor,
        ["ascension"] = runState.AscensionLevel,
        ["seed"] = runState.Rng.StringSeed,
        ["game_mode"] = runState.GameMode.ToString().ToLowerInvariant(),
        ["modifiers"] = runState.Modifiers.Select(m => m.Id.Entry).ToList(),
    };

    private static readonly string[] FlystsRoomKeys =
    {
        "boss_id", "second_boss_id", "normal_encounters_visited", "elite_encounters_visited",
        "events_visited", "boss_encounters_visited",
        "normal_encounter_ids", "elite_encounter_ids", "event_ids",
    };

    /// <summary>The mod's lite run_state (GameState.PruneRunState): only the keys the agent reads.</summary>
    private static System.Text.Json.Nodes.JsonObject FlystsPruneRunState(string json)
    {
        var src = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var dst = new System.Text.Json.Nodes.JsonObject();
        void Copy(System.Text.Json.Nodes.JsonObject from, System.Text.Json.Nodes.JsonObject to, string key)
        {
            if (from.TryGetPropertyValue(key, out var v)) to[key] = v?.DeepClone();
        }
        foreach (string k in new[] { "odds", "current_act_index", "run_time", "visited_map_coords" }) Copy(src, dst, k);
        if (src["players"] is System.Text.Json.Nodes.JsonArray players && players.Count > 0
            && players[0] is System.Text.Json.Nodes.JsonObject p0)
        {
            var p = new System.Text.Json.Nodes.JsonObject();
            foreach (string k in new[] { "odds", "current_hp", "max_hp", "gold" }) Copy(p0, p, k);
            if (p0["deck"] is System.Text.Json.Nodes.JsonArray deck)
            {
                var d = new System.Text.Json.Nodes.JsonArray();
                foreach (var c in deck)
                {
                    if (c is not System.Text.Json.Nodes.JsonObject co) continue;
                    var cc = new System.Text.Json.Nodes.JsonObject();
                    Copy(co, cc, "id");
                    Copy(co, cc, "current_upgrade_level");
                    d.Add(cc);
                }
                p["deck"] = d;
            }
            dst["players"] = new System.Text.Json.Nodes.JsonArray(p);
        }
        if (src["acts"] is System.Text.Json.Nodes.JsonArray acts)
        {
            var a = new System.Text.Json.Nodes.JsonArray();
            foreach (var act in acts)
            {
                if (act is not System.Text.Json.Nodes.JsonObject ao) { a.Add(null); continue; }
                var o = new System.Text.Json.Nodes.JsonObject();
                Copy(ao, o, "id");
                if (ao["rooms"] is System.Text.Json.Nodes.JsonObject rooms)
                {
                    var r = new System.Text.Json.Nodes.JsonObject();
                    foreach (string k in FlystsRoomKeys) Copy(rooms, r, k);
                    o["rooms"] = r;
                }
                a.Add(o);
            }
            dst["acts"] = a;
        }
        return dst;
    }

    /// <summary>GameState.BuildLiteState's run_state: the game's own save, pruned; run_time only on game_over.</summary>
    private void AttachFlystsRunState(Dictionary<string, object?> state)
    {
        if (_runState == null) return;
        try
        {
            string json = JsonSerializationUtility.ToJson(RunManager.Instance.ToSave(null));
            var pruned = FlystsPruneRunState(json);
            if (!Equals(state.GetValueOrDefault("state_type"), "game_over")) pruned.Remove("run_time");
            state["run_state"] = pruned;
        }
        catch (Exception ex) { Log($"flysts run_state failed: {ex.Message}"); }
    }

    // ---- player ------------------------------------------------------------

    private static string? FlystsTitle(Func<object?> getter)
    {
        try
        {
            return getter() switch
            {
                null => null,
                LocString loc => loc.GetFormattedText(),
                object other => other.ToString(),
            };
        }
        catch { return null; }
    }

    private static int? SafeInt(Func<int> getter) { try { return getter(); } catch { return null; } }
    private static bool SafeBool(Func<bool> getter) { try { return getter(); } catch { return false; } }
    private static string? SafeString(Func<string> getter) { try { return getter(); } catch { return null; } }
    private static List<string>? SafeList(Func<List<string>> getter) { try { return getter(); } catch { return null; } }

    private Dictionary<string, object?> FlystsPlayerSummary(Player player)
    {
        Creature creature = player.Creature;
        var relics = player.Relics.Select(r => new Dictionary<string, object?>
        {
            ["id"] = r.Id.Entry,
            ["name"] = FlystsTitle(() => r.Title),
            ["counter"] = SafeBool(() => r.ShowCounter) ? SafeInt(() => r.DisplayAmount) : null,
            ["used_up"] = SafeBool(() => r.IsUsedUp),
            ["stack"] = SafeInt(() => r.StackCount),
            ["rarity"] = SafeString(() => r.Rarity.ToString().ToLowerInvariant()),
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["character"] = FlystsTitle(() => player.Character.Title),
            ["hp"] = creature.CurrentHp,
            ["max_hp"] = creature.MaxHp,
            ["block"] = creature.Block,
            ["gold"] = player.Gold,
            ["relics"] = relics,
            ["potions"] = player.Potions.Select(p => p.Id.Entry).ToList(),
            ["potion_slots"] = player.MaxPotionCount,
            ["potion_slots_detail"] = player.PotionSlots.Select((p, i) => p == null
                ? new Dictionary<string, object?> { ["slot"] = i, ["id"] = null }
                : new Dictionary<string, object?>
                {
                    ["slot"] = i,
                    ["id"] = p.Id.Entry,
                    ["usage"] = SafeString(() => p.Usage.ToString()),
                    ["target_type"] = SafeString(() => p.TargetType.ToString()),
                    ["rarity"] = SafeString(() => p.Rarity.ToString()),
                    ["usable"] = FlystsPotionUsable(p),
                }).ToList(),
            ["base_max_energy"] = player.MaxEnergy,
            ["deck_count"] = player.Deck.Cards.Count,
            ["deck"] = player.Deck.Cards.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id.Entry,
                ["upgrade_level"] = c.CurrentUpgradeLevel,
                ["type"] = c.Type.ToString().ToLowerInvariant(),
                ["rarity"] = SafeString(() => c.Rarity.ToString().ToLowerInvariant()),
                ["enchantment"] = SafeString(() => c.Enchantment?.Id.Entry ?? ""),
            }).ToList(),
        };
    }

    /// <summary>GameState.PotionUsable (the game's NPotionPopup rule). NPlayerHand.IsInCardSelection
    /// is a hand selection pending in the headless selector.</summary>
    internal bool FlystsPotionUsable(PotionModel potion)
    {
        try
        {
            Creature creature = potion.Owner.Creature;
            if (potion.IsQueued || creature.IsDead || !potion.Owner.CanRemovePotions || !potion.PassesCustomUsabilityCheck)
                return false;
            if (potion.Usage == PotionUsage.AnyTime) return true;
            if (potion.Usage != PotionUsage.CombatOnly) return false;
            return CombatManager.Instance.IsInProgress
                && creature.CombatState?.CurrentSide == creature.Side
                && potion.Owner.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && !CombatManager.Instance.PlayerActionsDisabled
                && !FlystsHandSelectionPending;
        }
        catch { return false; }
    }

    // ---- combat --------------------------------------------------------------

    private Dictionary<string, object?> FlystsCombatState(RunState runState, Player player, CombatRoom combatRoom)
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        string roomType = combatRoom.RoomType.ToString().ToLowerInvariant();
        string stateType = roomType is "monster" or "elite" or "boss" ? roomType : "monster";
        if (combatState == null)
        {
            return new()
            {
                ["state_type"] = stateType,
                ["run"] = FlystsRunInfo(runState),
                ["player"] = FlystsPlayerSummary(player),
                ["message"] = "Combat state unavailable",
            };
        }

        bool isPlayPhase = combatState.CurrentSide == CombatSide.Player
            && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play;

        var sel = FlystsCurrentSelection();
        bool inHandSelection = sel != null && sel.IsHand;
        var monsters = new List<Dictionary<string, object?>>();
        foreach (Creature enemy in combatState.Enemies)
        {
            if (enemy.IsAlive) monsters.Add(FlystsMonsterState(enemy, player.Creature));
        }

        return new()
        {
            ["state_type"] = stateType,
            ["room_type"] = roomType,
            ["encounter_id"] = SafeString(() => combatRoom.Encounter.Id.Entry),
            ["encounter_is_weak"] = SafeBool(() => combatRoom.Encounter.IsWeak),
            ["parent_event_id"] = SafeString(() => combatRoom.ParentEventId?.Entry ?? ""),
            ["run"] = FlystsRunInfo(runState),
            ["is_play_phase"] = isPlayPhase,
            ["round"] = combatState.RoundNumber,
            ["player"] = FlystsCombatPlayerState(player, inHandSelection ? sel : null),
            ["monsters"] = monsters,
            ["turn"] = FlystsTurnHistory(combatState, player),
            ["hand_selection"] = inHandSelection ? (sel!.Kind == FlystsSelectionKind.HandUpgrade ? "upgrade_select" : "simple_select") : null,
            ["can_confirm"] = inHandSelection && sel!.CanConfirm,
            ["hand_prompt"] = inHandSelection ? sel!.PromptKey ?? "" : null,
            ["hand_min_select"] = inHandSelection ? sel!.Min : null,
            ["hand_max_select"] = inHandSelection ? sel!.Max : null,
            ["resolving"] = SafeBool(() => RunManager.Instance.ActionExecutor.IsRunning
                                           || !RunManager.Instance.ActionQueueSet.IsEmpty),
            ["current_action"] = SafeString(() => RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.GetType().Name ?? ""),
        };
    }

    /// <summary>GameState.AttachCombatContext: the fight behind a mid-combat card screen.</summary>
    private void FlystsAttachCombatContext(Dictionary<string, object?> state, Player player)
    {
        if (!SafeBool(() => CombatManager.Instance.IsInProgress)) return;
        CombatState? cs = CombatManager.Instance.DebugOnlyGetState();
        if (cs == null) return;
        var monsters = new List<Dictionary<string, object?>>();
        foreach (Creature enemy in cs.Enemies)
        {
            if (enemy.IsAlive) monsters.Add(FlystsMonsterState(enemy, player.Creature));
        }
        state["player"] = FlystsCombatPlayerState(player, null);
        state["monsters"] = monsters;
        state["round"] = cs.RoundNumber;
        state["in_combat"] = true;
    }

    private Dictionary<string, object?> FlystsCombatPlayerState(Player player, FlystsSelection? handSel)
    {
        Dictionary<string, object?> state = FlystsPlayerSummary(player);
        PlayerCombatState? pcs = player.PlayerCombatState;
        state["energy"] = pcs?.Energy ?? 0;
        state["max_energy"] = pcs?.MaxEnergy ?? 0;
        if (pcs != null)
        {
            state["powers"] = FlystsPowers(player.Creature);
            state["stars"] = pcs.Stars;
            state["piles"] = new Dictionary<string, object?>
            {
                ["draw"] = pcs.DrawPile.Cards.Count,
                ["discard"] = pcs.DiscardPile.Cards.Count,
                ["exhaust"] = pcs.ExhaustPile.Cards.Count,
                ["play"] = pcs.PlayPile.Cards.Count,
            };
            state["pile_cards"] = new Dictionary<string, object?>
            {
                ["draw"] = pcs.DrawPile.Cards.Select(c => c.Id.Entry).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                ["discard"] = pcs.DiscardPile.Cards.Select(c => c.Id.Entry).ToList(),
                ["exhaust"] = pcs.ExhaustPile.Cards.Select(c => c.Id.Entry).ToList(),
                ["play"] = pcs.PlayPile.Cards.Select(c => c.Id.Entry).ToList(),
            };
        }
        var hand = new List<Dictionary<string, object?>>();
        if (pcs != null)
        {
            foreach (CardModel card in pcs.Hand.Cards)
            {
                var cardEntry = new Dictionary<string, object?>
                {
                    ["id"] = card.Id.Entry,
                    ["cost"] = card.EnergyCost.CostsX ? 0 : card.EnergyCost.GetAmountToSpend(),
                    ["type"] = card.Type.ToString().ToLowerInvariant(),
                    ["upgraded"] = card.IsUpgraded,
                    ["needs_target"] = card.TargetType == TargetType.AnyEnemy,
                    ["playable"] = card.CanPlay(),
                    ["upgradeable"] = card.CurrentUpgradeLevel < card.MaxUpgradeLevel,
                };
                if (handSel != null)
                {
                    // NPlayerHand: a picked card's holder leaves the hand (the model stays in
                    // Hand.Cards); cards the selection filter rejects are hidden.
                    bool selected = handSel.IsSelected(card);
                    cardEntry["selected"] = selected;
                    cardEntry["selectable"] = !selected && handSel.Offers(card);
                }
                FlystsCardFaceDetail(cardEntry, card);
                FlystsCardCombatPreview(cardEntry, card, player);
                hand.Add(cardEntry);
            }
        }
        state["hand"] = hand;
        return state;
    }

    private static List<Dictionary<string, object?>> FlystsPowers(Creature creature) => creature.Powers.Select(p => new Dictionary<string, object?>
    {
        ["id"] = p.Id.Entry,
        ["amount"] = p.Amount,
        ["type"] = p.Type.ToString().ToLowerInvariant(),
        ["visible"] = SafeBool(() => p.IsVisible),
        ["display_amount"] = SafeInt(() => p.DisplayAmount),
    }).ToList();

    private static Dictionary<string, object?> FlystsMonsterState(Creature creature, Creature playerCreature)
    {
        var state = new Dictionary<string, object?>
        {
            ["hp"] = creature.CurrentHp,
            ["max_hp"] = creature.MaxHp,
            ["block"] = creature.Block,
            ["id"] = creature.Monster?.Id.Entry,
            ["combat_id"] = creature.CombatId.HasValue ? (long)creature.CombatId.Value : null,
            ["powers"] = FlystsPowers(creature),
            ["move_id"] = SafeString(() => creature.Monster!.NextMove.StateId),
            ["stunned"] = SafeBool(() => creature.IsStunned),
            ["hittable"] = SafeBool(() => creature.IsHittable),
            ["is_secondary"] = SafeBool(() => creature.IsSecondaryEnemy),
        };
        MoveState? move = creature.Monster?.NextMove;
        AbstractIntent? primary = move?.Intents?.FirstOrDefault();
        state["intent"] = primary == null
            ? new Dictionary<string, object?> { ["type"] = "unknown", ["value"] = 0 }
            : new Dictionary<string, object?> { ["type"] = FlystsIntentType(primary.IntentType), ["value"] = FlystsIntentMagnitude(primary, creature, playerCreature) };
        var intents = new List<Dictionary<string, object?>>();
        foreach (AbstractIntent intent in move?.Intents ?? Enumerable.Empty<AbstractIntent>())
        {
            var entry = new Dictionary<string, object?>
            {
                ["type"] = FlystsIntentType(intent.IntentType),
                ["raw_type"] = intent.IntentType.ToString(),
                ["value"] = FlystsIntentMagnitude(intent, creature, playerCreature),
            };
            if (intent is AttackIntent atk)
            {
                entry["damage_per_hit"] = SafeInt(() => atk.GetSingleDamage(new[] { playerCreature }, creature));
                entry["hits"] = Math.Max(1, atk.Repeats);
            }
            else if (intent is StatusIntent status)
            {
                entry["card_count"] = status.CardCount;
                entry["value"] = status.CardCount;
            }
            intents.Add(entry);
        }
        state["intents"] = intents;
        return state;
    }

    private static int FlystsIntentMagnitude(AbstractIntent intent, Creature monster, Creature playerCreature)
    {
        if (intent is AttackIntent atk)
            return atk.GetTotalDamage(new[] { playerCreature }, monster);
        foreach (string name in new[] { "Damage", "Amount", "Value", "BaseDamage", "Hits" })
        {
            PropertyInfo? prop = intent.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (prop?.GetValue(intent) is int intValue) return intValue;
        }
        return 0;
    }

    private static string FlystsIntentType(IntentType intentType) => intentType switch
    {
        IntentType.Attack => "attack",
        IntentType.Defend => "defend",
        IntentType.Buff => "buff",
        IntentType.Debuff => "debuff",
        IntentType.DebuffStrong => "debuff",
        IntentType.CardDebuff => "debuff",
        IntentType.DeathBlow => "attack",
        _ => "unknown",
    };

    /// <summary>GameState.AddCardFaceDetail. `vars` is DynamicVar.PreviewValue, which live is what
    /// the card's NCard.UpdateVisuals last computed (ClearPreview, then UpdateDynamicVarPreview with
    /// Normal mode and the card's current target); headless the refresh is done here, and the
    /// preview is cleared again afterwards (see CLAUDE.md, DynamicVar preview).</summary>
    private static void FlystsCardFaceDetail(Dictionary<string, object?> entry, CardModel card)
    {
        try
        {
            card.DynamicVars.ClearPreview();
            try { card.UpdateDynamicVarPreview(CardPreviewMode.Normal, card.CurrentTarget, card.DynamicVars); } catch { }
            var vars = new Dictionary<string, object?>();
            foreach (var kv in card.DynamicVars)
            {
                if (kv.Value is BoolVar || kv.Value is StringVar || kv.Value is IfUpgradedVar) continue;
                DynamicVar v = kv.Value;
                vars[kv.Key.ToLowerInvariant()] = SafeInt(() => (int)v.PreviewValue);
            }
            entry["vars"] = vars;
        }
        catch { }
        finally
        {
            try { card.DynamicVars.ClearPreview(); } catch { }
        }
        entry["keywords"] = SafeList(() => card.Keywords.Select(k => k.ToString().ToLowerInvariant()).OrderBy(k => k, StringComparer.Ordinal).ToList());
        entry["rarity"] = SafeString(() => card.Rarity.ToString().ToLowerInvariant());
        entry["star_cost"] = SafeInt(() => card.GetStarCostWithModifiers());
        entry["x_cost"] = SafeBool(() => card.EnergyCost.CostsX || card.HasStarCostX);
        entry["retain_this_turn"] = SafeBool(() => card.ShouldRetainThisTurn);
        entry["sly_this_turn"] = SafeBool(() => card.IsSlyThisTurn);
        entry["enchantment"] = SafeString(() => card.Enchantment?.Id.Entry ?? "");
        entry["enchantment_amount"] = SafeInt(() => card.Enchantment?.Amount ?? 0);
        entry["affliction"] = SafeString(() => card.Affliction?.Id.Entry ?? "");
    }

    /// <summary>GameState.AddCardCombatPreview: damage_vs per alive enemy and block_now through the
    /// pure Hook.ModifyDamage / Hook.ModifyBlock.</summary>
    private static void FlystsCardCombatPreview(Dictionary<string, object?> entry, CardModel card, Player player)
    {
        try
        {
            ICombatState? cs = card.CombatState ?? player.Creature.CombatState;
            if (cs != null)
            {
                DynamicVar? dmgVar = card.DynamicVars.Values.FirstOrDefault(v => v is CalculatedDamageVar)
                                     ?? card.DynamicVars.Values.FirstOrDefault(v => v is DamageVar);
                if (dmgVar != null)
                {
                    var perEnemy = new List<int>();
                    foreach (Creature enemy in cs.Enemies.Where(e => e.IsAlive))
                    {
                        decimal baseDmg;
                        ValueProp props;
                        if (dmgVar is CalculatedDamageVar cv) { baseDmg = cv.Calculate(enemy); props = cv.Props; }
                        else { var dv = (DamageVar)dmgVar; baseDmg = dv.BaseValue; props = dv.Props; }
                        decimal d = Hook.ModifyDamage(player.RunState, cs, enemy, player.Creature, baseDmg, props, card,
                            ModifyDamageHookType.All, CardPreviewMode.Normal, out IEnumerable<AbstractModel> _);
                        perEnemy.Add((int)Math.Max(d, 0m));
                    }
                    entry["damage_vs"] = perEnemy;
                }
                BlockVar? blk = card.DynamicVars.Values.OfType<BlockVar>().FirstOrDefault();
                if (blk != null)
                {
                    entry["block_now"] = (int)Math.Max(
                        Hook.ModifyBlock(cs, player.Creature, blk.BaseValue, blk.Props, card, null, out IEnumerable<AbstractModel> _), 0m);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FLYSTS] card preview failed for {card.Id.Entry}: {ex.Message}");
        }
        entry["tags"] = SafeList(() => card.Tags.Select(t => t.ToString().ToLowerInvariant()).OrderBy(t => t, StringComparer.Ordinal).ToList());
        entry["target_type"] = SafeString(() => card.TargetType.ToString());
        entry["canonical_cost"] = SafeInt(() => card.EnergyCost.Canonical);
        try
        {
            card.CanPlay(out UnplayableReason reason, out AbstractModel? _);
            entry["unplayable_reason"] = reason.ToString();
        }
        catch { }
    }

    private static Dictionary<string, object?>? FlystsTurnHistory(ICombatState combatState, Player player)
    {
        try
        {
            CombatHistory history = CombatManager.Instance.History;
            var plays = history.CardPlaysFinished
                .Where(e => e.HappenedThisTurn(combatState) && e.CardPlay.Card.Owner == player)
                .Select(e => e.CardPlay.Card).ToList();
            return new Dictionary<string, object?>
            {
                ["cards_played"] = plays.Count,
                ["attacks_played"] = plays.Count(c => c.Type == CardType.Attack),
                ["skills_played"] = plays.Count(c => c.Type == CardType.Skill),
                ["powers_played"] = plays.Count(c => c.Type == CardType.Power),
                ["energy_spent"] = history.Entries.OfType<EnergySpentEntry>()
                    .Where(e => e.HappenedThisTurn(combatState)).Sum(e => e.Amount),
                ["played_ids"] = plays.Select(c => c.Id.Entry).ToList(),
            };
        }
        catch { return null; }
    }

    // ---- map -------------------------------------------------------------

    private Dictionary<string, object?> FlystsMapState(RunState runState, Player player) => new()
    {
        ["state_type"] = "map",
        ["run"] = FlystsRunInfo(runState),
        ["player"] = FlystsPlayerSummary(player),
        ["map"] = FlystsMapDetail(runState),
    };

    private List<MapPoint> FlystsTravelable(RunState runState) =>
        TravelablePoints(runState.Map).OrderBy(p => p.coord.col).ThenBy(p => p.coord.row).ToList();

    private Dictionary<string, object?> FlystsMapDetail(RunState runState)
    {
        ActMap map = runState.Map;
        var visitedList = runState.VisitedMapCoords.Select(coord => new Dictionary<string, object?>
        {
            ["col"] = coord.col,
            ["row"] = coord.row,
            ["type"] = map.GetPoint(coord)?.PointType.ToString(),
        }).ToList();

        var nextOptions = new List<Dictionary<string, object?>>();
        var travelable = FlystsTravelable(runState);
        for (int i = 0; i < travelable.Count; i++)
        {
            MapPoint point = travelable[i];
            var entry = new Dictionary<string, object?>
            {
                ["index"] = i,
                ["col"] = point.coord.col,
                ["row"] = point.coord.row,
                ["type"] = point.PointType.ToString(),
            };
            var leadsTo = point.Children.OrderBy(c => c.coord.col)
                .Select(c => new Dictionary<string, object?> { ["col"] = c.coord.col, ["row"] = c.coord.row, ["type"] = c.PointType.ToString() })
                .ToList();
            if (leadsTo.Count > 0) entry["leads_to"] = leadsTo;
            nextOptions.Add(entry);
        }

        var nodes = new List<Dictionary<string, object?>> { FlystsMapNode(map.StartingMapPoint) };
        nodes.AddRange(map.GetAllMapPoints().Select(FlystsMapNode));
        nodes.Add(FlystsMapNode(map.BossMapPoint));
        if (map.SecondBossMapPoint != null) nodes.Add(FlystsMapNode(map.SecondBossMapPoint));

        return new()
        {
            ["visited"] = visitedList,
            ["next_options"] = nextOptions,
            ["traveling"] = false,
            ["nodes"] = nodes,
            ["boss"] = new Dictionary<string, object?> { ["col"] = map.BossMapPoint.coord.col, ["row"] = map.BossMapPoint.coord.row },
        };
    }

    private static Dictionary<string, object?> FlystsMapNode(MapPoint point) => new()
    {
        ["col"] = point.coord.col,
        ["row"] = point.coord.row,
        ["type"] = point.PointType.ToString(),
        ["children"] = point.Children.OrderBy(c => c.coord.col).Select(c => new List<int> { c.coord.col, c.coord.row }).ToList(),
    };

    // ---- shop / event / rest / card reward / grids ---------------------------

    private Dictionary<string, object?> FlystsShopState(RunState runState, Player player, MerchantRoom merchantRoom)
    {
        var items = new List<Dictionary<string, object?>>();
        int i = 0;
        foreach (var entry in merchantRoom.GetLocalInventory().AllEntries)
        {
            items.Add(new Dictionary<string, object?>
            {
                ["index"] = i,
                ["cost"] = entry.Cost,
                ["in_stock"] = entry.IsStocked,
                ["affordable"] = entry.EnoughGold,
                ["leave"] = false,
                ["kind"] = FlystsShopKind(entry),
                ["id"] = FlystsShopId(entry),
                ["on_sale"] = entry is MerchantCardEntry ce && ce.IsOnSale,
            });
            i++;
        }
        items.Add(new Dictionary<string, object?> { ["index"] = i, ["leave"] = true });
        return new()
        {
            ["state_type"] = "shop",
            ["last_purchase"] = _flystsLastPurchase,
            ["run"] = FlystsRunInfo(runState),
            ["player"] = FlystsPlayerSummary(player),
            ["items"] = items,
        };
    }

    private static string FlystsShopKind(MerchantEntry entry) => entry switch
    {
        MerchantCardEntry => "card",
        MerchantPotionEntry => "potion",
        MerchantRelicEntry => "relic",
        MerchantCardRemovalEntry => "card_removal",
        _ => "other",
    };

    private static string? FlystsShopId(MerchantEntry entry) => entry switch
    {
        MerchantCardEntry ce => ce.CreationResult?.Card?.Id.Entry ?? "CARD_PENDING",
        MerchantPotionEntry pe => pe.Model?.Id.Entry ?? "POTION_PENDING",
        MerchantRelicEntry re => re.Model?.Id.Entry ?? "RELIC_PENDING",
        MerchantCardRemovalEntry => "CARD_REMOVAL",
        _ => entry.GetType().Name,
    };

    private static readonly System.Text.RegularExpressions.Regex FlystsBbcode = new(@"\[[^\]]*\]");
    private static string FlystsStripBbcode(string text) => FlystsBbcode.Replace(text ?? "", "").Trim();

    /// <summary>The options the event screen shows: the event's CurrentOptions, or the Proceed
    /// option NEventRoom.SetOptions synthesizes for a finished event.</summary>
    private static IReadOnlyList<EventOption> FlystsEventOptions(EventModel? ev)
    {
        if (ev == null) return Array.Empty<EventOption>();
        if (ev.IsFinished)
            return new[] { new EventOption(ev, MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom.Proceed, "PROCEED", false, true) };
        return ev.CurrentOptions;
    }

    private Dictionary<string, object?> FlystsEventState(RunState runState, Player player, EventRoom eventRoom)
    {
        var ev = SafeEvent();
        var options = new List<Dictionary<string, object?>>();
        int i = 0;
        foreach (var option in FlystsEventOptions(ev))
        {
            options.Add(new Dictionary<string, object?>
            {
                ["index"] = i,
                ["locked"] = option.IsLocked,
                ["text_key"] = SafeString(() => option.TextKey),
                ["title"] = SafeString(() => FlystsStripBbcode(option.Title.GetFormattedText())),
                ["description"] = SafeString(() => FlystsStripBbcode(option.Description.GetFormattedText())),
                ["relic"] = SafeString(() => option.Relic?.Id.Entry ?? ""),
                ["will_kill"] = SafeBool(() => option.WillKillPlayer?.Invoke(player) ?? false),
                ["is_proceed"] = SafeBool(() => option.IsProceed),
                // NEventOptionButton.IsEnabled: every logged live option read true at a settled
                // decision (locked ones too); the lock is reported separately.
                ["enabled"] = true,
            });
            i++;
        }
        string? canonicalId = SafeString(() => eventRoom.CanonicalEvent?.Id.Entry ?? "");
        string? liveId = ev == null ? null : SafeString(() => ev.Id.Entry);
        return new()
        {
            ["state_type"] = "event",
            ["run"] = FlystsRunInfo(runState),
            ["player"] = FlystsPlayerSummary(player),
            ["event_id"] = !string.IsNullOrEmpty(liveId) ? liveId : canonicalId,
            ["canonical_event_id"] = canonicalId,
            ["event_begun"] = ev != null,
            ["options"] = options,
        };
    }

    private static EventModel? SafeEvent()
    {
        try
        {
            var sync = RunManager.Instance.EventSynchronizer;
            if (sync == null || sync.Events.Count == 0) return null;
            return sync.GetLocalEvent();
        }
        catch { return null; }
    }

    private Dictionary<string, object?> FlystsRestSiteState(RunState runState, Player player, RestSiteRoom room)
    {
        var options = new List<Dictionary<string, object?>>();
        int i = 0;
        SettleRestSiteTask();
        bool resolving = _restSiteTask != null;
        foreach (var option in room.Options)
        {
            // NRestSiteRoom greys every button while a chosen option resolves (DisableOptions).
            options.Add(new Dictionary<string, object?> { ["index"] = i, ["option_id"] = option.OptionId, ["enabled"] = !resolving && option.IsEnabled, ["leave"] = false });
            i++;
        }
        options.Add(new Dictionary<string, object?> { ["index"] = i, ["leave"] = true, ["enabled"] = !resolving && (_restSiteChoiceMade || room.Options.Count == 0) });
        return new()
        {
            ["state_type"] = "rest_site",
            ["run"] = FlystsRunInfo(runState),
            ["player"] = FlystsPlayerSummary(player),
            ["heal_amount"] = SafeInt(() => (int)HealRestSiteOption.GetHealAmount(player)),
            ["options"] = options,
        };
    }

    private Dictionary<string, object?> FlystsCardRewardState(RunState runState, Player player)
    {
        var cards = new List<Dictionary<string, object?>>();
        int i = 0;
        foreach (var cr in _cardSelector.PendingRewardCards ?? new())
        {
            var card = cr.Card;
            cards.Add(new Dictionary<string, object?>
            {
                ["card_index"] = i,
                ["name"] = FlystsTitle(() => card?.Title),
                ["id"] = card?.Id.Entry,
                ["type"] = card?.Type.ToString().ToLowerInvariant(),
                ["upgraded"] = card?.IsUpgraded,
                ["rarity"] = SafeString(() => card?.Rarity.ToString().ToLowerInvariant() ?? ""),
                ["enchantment"] = SafeString(() => card?.Enchantment?.Id.Entry ?? ""),
            });
            i++;
        }
        return new()
        {
            ["state_type"] = "card_reward",
            ["run"] = FlystsRunInfo(runState),
            ["player"] = FlystsPlayerSummary(player),
            ["cards"] = cards,
            ["alternatives"] = (_cardSelector.PendingRewardAlternatives ?? new())
                .Select((a, j) => new Dictionary<string, object?>
                {
                    ["index"] = j,
                    ["option_id"] = a.OptionId,
                    ["title"] = SafeString(() => FlystsStripBbcode(a.Title.GetFormattedText())),
                }).ToList(),
        };
    }

    private Dictionary<string, object?> FlystsGridState(RunState runState, Player player, FlystsSelection sel)
    {
        var cards = new List<Dictionary<string, object?>>();
        for (int i = 0; i < sel.Display.Count; i++)
        {
            var card = sel.Display[i];
            cards.Add(new Dictionary<string, object?>
            {
                ["card_index"] = i,
                ["name"] = FlystsTitle(() => card.Title),
                ["id"] = card.Id.Entry,
                ["type"] = card.Type.ToString().ToLowerInvariant(),
                ["upgraded"] = card.IsUpgraded,
                ["enchantment"] = SafeString(() => card.Enchantment?.Id.Entry ?? ""),
                ["selected"] = sel.IsSelected(card),
            });
        }
        var state = new Dictionary<string, object?>
        {
            ["state_type"] = sel.StateType,
            ["run"] = FlystsRunInfo(runState),
            ["player"] = FlystsPlayerSummary(player),
            ["cards"] = cards,
            ["enchantment"] = sel.Enchantment == null ? null : SafeString(() => sel.Enchantment.Id.Entry),
            ["enchantment_amount"] = sel.Enchantment == null ? null : sel.EnchantmentAmount,
            ["selected_count"] = sel.SelectedCount,
            ["preview_open"] = sel.PreviewOpenReported,
            ["min_select"] = sel.Min,
            ["max_select"] = sel.Max,
            ["prompt"] = sel.PromptFormatted,
            ["can_confirm"] = sel.CanConfirm,
        };
        if (sel.StateType == "combat_pile_select") FlystsAttachCombatContext(state, player);
        return state;
    }

    private Dictionary<string, object?> FlystsBundleState(RunState runState, Player player, FlystsSelection sel)
    {
        var bundles = new List<Dictionary<string, object?>>();
        for (int i = 0; i < sel.Bundles!.Count; i++)
        {
            bundles.Add(new Dictionary<string, object?>
            {
                ["index"] = i,
                ["cards"] = sel.Bundles[i].Select(c => c.Id.Entry).ToList(),
                ["selected"] = sel.SelectedBundle == i,
            });
        }
        return new()
        {
            ["state_type"] = "choose_a_bundle",
            ["run"] = FlystsRunInfo(runState),
            ["player"] = FlystsPlayerSummary(player),
            ["bundles"] = bundles,
            ["can_confirm"] = sel.SelectedBundle >= 0,
        };
    }

    private Dictionary<string, object?> FlystsChooseACardState(RunState runState, Player player, FlystsSelection sel)
    {
        var cards = new List<Dictionary<string, object?>>();
        for (int i = 0; i < sel.Display.Count; i++)
        {
            var card = sel.Display[i];
            cards.Add(new Dictionary<string, object?>
            {
                ["card_index"] = i,
                ["name"] = FlystsTitle(() => card.Title),
                ["id"] = card.Id.Entry,
                ["type"] = card.Type.ToString().ToLowerInvariant(),
                ["upgraded"] = card.IsUpgraded,
                ["cost"] = SafeInt(() => card.EnergyCost.CostsX ? 0 : card.EnergyCost.GetAmountToSpend()),
            });
        }
        var state = new Dictionary<string, object?>
        {
            ["state_type"] = "choose_a_card",
            ["run"] = FlystsRunInfo(runState),
            ["player"] = FlystsPlayerSummary(player),
            ["cards"] = cards,
            ["can_skip"] = sel.CanSkip,
        };
        FlystsAttachCombatContext(state, player);
        return state;
    }

    private Dictionary<string, object?> FlystsGameOverState(RunState runState, bool victory)
    {
        Player? me = runState.Players.Count > 0 ? runState.Players[0] : null;
        var history = RunManager.Instance.History;
        string? killedBy = history == null ? null
            : history.KilledByEncounter != ModelId.none ? history.KilledByEncounter.Entry
            : history.KilledByEvent != ModelId.none ? history.KilledByEvent.Entry : null;
        if (killedBy == null && !victory && runState.CurrentRoom is CombatRoom cr)
            killedBy = SafeString(() => cr.Encounter.Id.Entry);
        if (killedBy == null && !victory && runState.CurrentRoom is EventRoom er)
            killedBy = SafeString(() => er.CanonicalEvent?.Id.Entry ?? "");
        int? score = null;
        try { score = ScoreUtility.CalculateScore(RunManager.Instance.ToSave(null), victory); } catch { }
        var state = new Dictionary<string, object?>
        {
            ["state_type"] = "game_over",
            ["victory"] = victory,
            ["done"] = true,
            ["screen_ready"] = true,
            ["message"] = victory ? "Run ended in victory." : "Run ended.",
            ["options"] = new List<string> { "main_menu" },
            ["run"] = FlystsRunInfo(runState),
            ["killed_by"] = killedBy,
            ["abandoned"] = false,
            ["score"] = score,
        };
        if (me != null)
        {
            try { state["player"] = FlystsPlayerSummary(me); }
            catch (Exception ex) { Log($"flysts game_over player summary failed: {ex.Message}"); }
        }
        return state;
    }
}
