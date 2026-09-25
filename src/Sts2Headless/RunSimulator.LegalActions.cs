using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Headless;

/// <summary>
/// Enumerates the actions that are valid at a decision point, in the exact shape of an
/// "action" command ({"action": ..., "args": {...}}), so RL agents can mask/choose directly.
/// select_cards (choose k of n) is combinatorial and is given as a single template entry.
/// </summary>
public partial class RunSimulator
{
    /// <summary>Adds "legal_actions" to decision payloads. Called by Program after every command.</summary>
    public void AttachLegalActions(Dictionary<string, object?>? result)
    {
        if (result == null || _runState == null) return;
        if (!string.Equals(result.GetValueOrDefault("type") as string, "decision", StringComparison.Ordinal)) return;
        try
        {
            var legal = ComputeLegalActions(result);
            result["legal_actions"] = legal;
            RememberLegal(result.GetValueOrDefault("decision") as string ?? "", legal);
        }
        catch (Exception ex)
        {
            InvalidateLegal();
            Log($"legal_actions: {ex.Message}");
        }
    }

    private static Dictionary<string, object?> Act(string action, Dictionary<string, object?>? args = null)
    {
        var a = new Dictionary<string, object?> { ["action"] = action };
        if (args != null) a["args"] = args;
        return a;
    }

    private static IEnumerable<Dictionary<string, object?>> Rows(Dictionary<string, object?> result, string key) =>
        result.GetValueOrDefault(key) as IEnumerable<Dictionary<string, object?>> ?? Enumerable.Empty<Dictionary<string, object?>>();

    private static bool Flag(Dictionary<string, object?> row, string key, bool dflt = false) =>
        row.GetValueOrDefault(key) is bool b ? b : dflt;

    private List<Dictionary<string, object?>> ComputeLegalActions(Dictionary<string, object?> result)
    {
        var player = _runState!.Players[0];
        var legal = new List<Dictionary<string, object?>>();
        var decision = result.GetValueOrDefault("decision") as string ?? "";

        switch (decision)
        {
            case "map_select":
                foreach (var c in Rows(result, "choices"))
                    legal.Add(Act("select_map_node", new() { ["col"] = c["col"], ["row"] = c["row"] }));
                break;

            case "combat_play":
                AddCombatActions(player, result, legal);
                break;

            case "card_select":
            {
                var n = Rows(result, "cards").Count();
                legal.Add(new Dictionary<string, object?>
                {
                    ["action"] = "select_cards",
                    ["template"] = true,
                    ["args"] = new Dictionary<string, object?> { ["indices"] = "comma-separated card indices" },
                    ["min_select"] = result.GetValueOrDefault("min_select"),
                    ["max_select"] = result.GetValueOrDefault("max_select"),
                    ["num_cards"] = n,
                });
                // Skip when nothing is required; on cancelable screens (Smith, shop removal) it cancels.
                if ((result.GetValueOrDefault("min_select") is int min && min == 0) || Flag(result, "cancelable"))
                    legal.Add(Act("skip_select"));
                break;
            }

            case "card_reward":
                foreach (var c in Rows(result, "cards"))
                    legal.Add(Act("select_card_reward", new() { ["card_index"] = c["index"] }));
                if (Flag(result, "can_skip", true))
                    legal.Add(Act("skip_card_reward"));
                foreach (var alt in Rows(result, "alternatives"))
                {
                    if (string.Equals(alt.GetValueOrDefault("id") as string, "Skip", StringComparison.OrdinalIgnoreCase))
                        continue; // same as skip_card_reward
                    legal.Add(Act("select_card_reward_alternative", new() { ["alternative_index"] = alt["index"] }));
                }
                break;

            case "bundle_select":
                foreach (var b in Rows(result, "bundles"))
                    legal.Add(Act("select_bundle", new() { ["bundle_index"] = b["index"] }));
                break;

            case "rewards":
            {
                var blockedPotion = false;
                foreach (var r in Rows(result, "rewards"))
                {
                    if (Flag(r, "enabled", true))
                        legal.Add(Act("claim_reward", new() { ["reward_index"] = r["index"] }));
                    else if (r.GetValueOrDefault("type") as string == "potion")
                        blockedPotion = true;
                }
                // Make room for a potion reward (the top-bar potion popup's Discard).
                if (blockedPotion && player.CanUseOrRemovePotions)
                    AddPotionIndexActions(player, "discard_potion", legal);
                legal.Add(Act("proceed"));
                break;
            }

            case "event_choice":
                foreach (var o in Rows(result, "options"))
                    if (!Flag(o, "is_locked"))
                        legal.Add(Act("choose_option", new() { ["option_index"] = o["index"] }));
                // Auto flow: an event with nothing left to choose is left like the UI's Proceed.
                if (!_manualFlow && legal.Count == 0)
                    legal.Add(Act("leave_room"));
                break;

            case "rest_site":
                foreach (var o in Rows(result, "options"))
                    if (Flag(o, "is_enabled", true))
                        legal.Add(Act("choose_option", new() { ["option_index"] = o["index"] }));
                if (_manualFlow && Flag(result, "can_proceed"))
                    legal.Add(Act("proceed"));
                // Auto flow: no usable option (e.g. nothing to upgrade and resting disabled) means leave.
                if (!_manualFlow && legal.Count == 0)
                    legal.Add(Act("leave_room"));
                break;

            case "shop":
                AddShopActions(player, result, legal);
                break;

            case "treasure":
                if (!Flag(result, "chest_opened"))
                {
                    legal.Add(Act("open_chest"));
                    break;
                }
                foreach (var r in Rows(result, "relics"))
                    legal.Add(Act("pick_relic", new() { ["relic_index"] = r["index"] }));
                if (Rows(result, "relics").Any())
                    legal.Add(Act("skip_relic"));
                if (Flag(result, "can_proceed", true))
                    legal.Add(Act("proceed"));
                break;

            case "crystal_sphere":
                AddCrystalSphereActions(legal);
                break;

            case "game_over":
                break;

            default:
                legal.Add(Act("proceed"));
                break;
        }
        return legal;
    }

    private void AddCombatActions(Player player, Dictionary<string, object?> result, List<Dictionary<string, object?>> legal)
    {
        if (!IsPlayPhase()) return;
        var aliveEnemies = CombatManager.Instance.DebugOnlyGetState()?.Enemies?
            .Count(e => e != null && e.IsAlive) ?? 0;

        foreach (var c in Rows(result, "hand"))
        {
            if (!Flag(c, "can_play")) continue;
            var singleEnemy = c.GetValueOrDefault("target_type") as string == nameof(TargetType.AnyEnemy);
            if (singleEnemy && aliveEnemies == 0) continue; // nothing to target (e.g. mid phase change)
            if (singleEnemy && aliveEnemies > 1)
            {
                for (int t = 0; t < aliveEnemies; t++)
                    legal.Add(Act("play_card", new() { ["card_index"] = c["index"], ["target_index"] = t }));
            }
            else
            {
                legal.Add(Act("play_card", new() { ["card_index"] = c["index"] }));
            }
        }

        if (player.CanUseOrRemovePotions)
        {
            var potions = player.Potions?.ToList() ?? new();
            for (int i = 0; i < potions.Count; i++)
            {
                var p = potions[i];
                if (p == null || p.IsQueued) continue;
                var usable = (p.Usage == PotionUsage.CombatOnly || p.Usage == PotionUsage.AnyTime)
                             && p.PassesCustomUsabilityCheck;
                if (!usable) continue;
                if (p.TargetType == TargetType.AnyEnemy && aliveEnemies == 0) continue;
                if (p.TargetType == TargetType.AnyEnemy && aliveEnemies > 1)
                {
                    for (int t = 0; t < aliveEnemies; t++)
                        legal.Add(Act("use_potion", new() { ["potion_index"] = i, ["target_index"] = t }));
                }
                else
                {
                    legal.Add(Act("use_potion", new() { ["potion_index"] = i }));
                }
            }
            AddPotionIndexActions(player, "discard_potion", legal);
        }

        legal.Add(Act("end_turn"));
    }

    private static void AddPotionIndexActions(Player player, string action, List<Dictionary<string, object?>> legal)
    {
        var potions = player.Potions?.ToList() ?? new();
        for (int i = 0; i < potions.Count; i++)
            if (potions[i] != null)
                legal.Add(Act(action, new() { ["potion_index"] = i }));
    }

    private void AddShopActions(Player player, Dictionary<string, object?> result, List<Dictionary<string, object?>> legal)
    {
        int gold = player.Gold;
        bool Affordable(Dictionary<string, object?> row) =>
            Flag(row, "is_stocked") && row.GetValueOrDefault("cost") is int cost && cost <= gold;

        foreach (var c in Rows(result, "cards"))
            if (Affordable(c)) legal.Add(Act("buy_card", new() { ["card_index"] = c["index"] }));
        foreach (var r in Rows(result, "relics"))
            if (Affordable(r)) legal.Add(Act("buy_relic", new() { ["relic_index"] = r["index"] }));
        if (player.HasOpenPotionSlots)
            foreach (var p in Rows(result, "potions"))
                if (Affordable(p)) legal.Add(Act("buy_potion", new() { ["potion_index"] = p["index"] }));

        var removal = (_runState?.CurrentRoom as MerchantRoom)?.GetLocalInventory()?.CardRemovalEntry;
        if (removal != null && removal.IsStocked && removal.Cost <= gold)
            legal.Add(Act("remove_card"));

        legal.Add(Act(_manualFlow ? "proceed" : "leave_room"));
    }
}
