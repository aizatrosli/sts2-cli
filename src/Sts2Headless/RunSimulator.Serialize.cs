using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;

namespace Sts2Headless;

/// <summary>
/// Shared serializers for cards, relics, potions and powers. Text comes from the engine's own
/// renderers (the same strings the UI shows: variables filled in, conditional text resolved),
/// with BBCode removed and icons turned into [E] (energy) and [star].
/// </summary>
public partial class RunSimulator
{
    private static readonly Regex ImgTag = new(@"\[img\]([^\[]*)\[/img\]", RegexOptions.Compiled);
    // Any BBCode tag, including ones with attributes ([rainbow freq=0.3 sat=0.8]).
    private static readonly Regex BbTag = new(@"\[/?[a-zA-Z_][^\[\]]*\]", RegexOptions.Compiled);

    internal static string CleanText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // Icons first, through placeholders the BBCode pass cannot touch.
        text = ImgTag.Replace(text, m =>
            m.Groups[1].Value.Contains("star", StringComparison.OrdinalIgnoreCase) ? "\u0001star\u0002"
            : m.Groups[1].Value.Contains("energy", StringComparison.OrdinalIgnoreCase) ? "\u0001E\u0002" : "");
        text = BbTag.Replace(text, "");
        return text.Replace('\u0001', '[').Replace('\u0002', ']').Trim();
    }

    /// <summary>Engine-rendered text, or the raw table text if rendering fails headless.</summary>
    private string Rendered(Func<string?> render, string table, string key)
    {
        try
        {
            var text = render();
            if (!string.IsNullOrWhiteSpace(text) && text != key) return CleanText(text);
        }
        catch { }
        return _loc.Text(table, key);
    }

    // Rendering through SmartFormat is the most expensive part of an observation, and most text
    // is unchanged from one step to the next. Rendered text is cached under a key built from every
    // input the renderer reads (model id, variable values, upgrade, enchantment, keywords, pile and
    // combat flags, power amount...). Cleared on every new run.
    private readonly Dictionary<string, string> _textCache = new();

    private string Cached(string key, Func<string> render)
    {
        if (_textCache.TryGetValue(key, out var text)) return text;
        if (_textCache.Count > 50_000) _textCache.Clear();
        text = render();
        _textCache[key] = text;
        return text;
    }

    private static string VarsKey(MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet? vars)
    {
        if (vars == null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var v in vars.Values) sb.Append(v.Name).Append('=').Append(v.BaseValue).Append('/').Append(v.PreviewValue).Append(';');
        return sb.ToString();
    }

    private string CardKey(CardModel c, PileType pile)
    {
        bool inCombat = MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress;
        string flags = "";
        try { flags = $"{c.IsSlyThisTurn}{c.ShouldRetainThisTurn}{c.Owner?.IsOstyAlive}{c.Pile?.Type}"; } catch { }
        var kws = c.Keywords == null ? "" : string.Join(",", c.Keywords.Select(k => (int)k).OrderBy(k => k));
        return $"{c.Id}|{c.CurrentUpgradeLevel}|{pile}|{inCombat}|{flags}|{kws}|{c.Enchantment?.Id}:{c.Enchantment?.Amount}|{c.Affliction?.Id}:{c.Affliction?.Amount}|{VarsKey(c.DynamicVars)}";
    }

    private string CardName(CardModel c) => Cached($"cn|{c.Id}|{c.CurrentUpgradeLevel}", () => Rendered(() => c.Title, "cards", c.Id.Entry + ".title"));
    private string CardText(CardModel c, PileType pile) =>
        Cached("ct|" + CardKey(c, pile), () => Rendered(() => c.GetDescriptionForPile(pile), "cards", c.Id.Entry + ".description"));
    private string RelicName(RelicModel r) => Cached($"rn|{r.Id}", () => Rendered(() => r.Title.GetFormattedText(), "relics", r.Id.Entry + ".title"));
    private string RelicText(RelicModel r) =>
        Cached($"rt|{r.Id}|{VarsKey(r.DynamicVars)}", () => Rendered(() => r.DynamicDescription.GetFormattedText(), "relics", r.Id.Entry + ".description"));
    private string PotionName(PotionModel p) => Cached($"pn|{p.Id}", () => Rendered(() => p.Title.GetFormattedText(), "potions", p.Id.Entry + ".title"));
    private string PotionText(PotionModel p) =>
        Cached($"pt|{p.Id}|{VarsKey(p.DynamicVars)}", () => Rendered(() => p.DynamicDescription.GetFormattedText(), "potions", p.Id.Entry + ".description"));
    // Power titles can depend on their source (temporary strength names the card or relic).
    private string PowerName(PowerModel p) => Rendered(() => p.Title.GetFormattedText(), "powers", p.Id.Entry + ".title");
    private string PowerText(PowerModel p)
    {
        string owner = "";
        try { owner = $"{p.Owner?.IsPlayer}|{p.Owner?.Monster?.Id}|{p.Applier?.Monster?.Id}|{p.Target?.Monster?.Id}"; } catch { }
        return Cached($"pw|{p.Id}|{p.Amount}|{owner}|{PowerName(p)}|{VarsKey(p.DynamicVars)}",
            () => Rendered(() => p.HoverTips.OfType<HoverTip>().FirstOrDefault().Description, "powers", p.Id.Entry + ".description"));
    }

    private string MonsterName(MonsterModel? m) =>
        m == null ? "?" : Rendered(() => m.Title.GetFormattedText(), "monsters", m.Id.Entry + ".name");

    /// <summary>Boss/encounter: id (e.g. KAISER_CRAB_BOSS) and its display title from encounters.json.</summary>
    private Dictionary<string, object?> EncounterInfo(EncounterModel e) => new()
    {
        ["id"] = e.Id.Entry,
        ["name"] = Rendered(() => e.Title.GetFormattedText(), "encounters", e.Id.Entry + ".title"),
    };

    /// <summary>
    /// An event option as NEventOptionButton shows it: the event's variables are added to the
    /// option's title and description, which are then formatted.
    /// </summary>
    private (string? Title, string? Description) EventOptionText(MegaCrit.Sts2.Core.Models.EventModel ev, MegaCrit.Sts2.Core.Events.EventOption opt)
    {
        string? title = null, description = null;
        try
        {
            if (opt.Title != null)
            {
                ev.DynamicVars.AddTo(opt.Title);
                title = Rendered(() => opt.Title.GetFormattedText(), opt.Title.LocTable, opt.Title.LocEntryKey);
            }
            if (opt.Description != null && !string.IsNullOrEmpty(opt.Description.LocEntryKey))
            {
                ev.DynamicVars.AddTo(opt.Description);
                description = Rendered(() => opt.Description.GetFormattedText(), opt.Description.LocTable, opt.Description.LocEntryKey);
            }
        }
        catch { }
        return (string.IsNullOrWhiteSpace(title) ? null : title, string.IsNullOrWhiteSpace(description) ? null : description);
    }

    /// <summary>The event page text with the event's variables filled in.</summary>
    private string EventBodyText(MegaCrit.Sts2.Core.Models.EventModel ev)
    {
        var desc = ev.Description;
        try { ev.DynamicVars.AddTo(desc); } catch { }
        return Rendered(() => desc.GetFormattedText(), desc.LocTable, desc.LocEntryKey);
    }

    /// <summary>What an event option offers (its hover tips): relics, cards, potions, curses, enchantments.</summary>
    private List<Dictionary<string, object?>>? EventOptionOffers(MegaCrit.Sts2.Core.Events.EventOption opt)
    {
        var offers = new List<Dictionary<string, object?>>();
        try
        {
            if (opt.Relic != null) offers.Add(Offer("relic", opt.Relic.Id.ToString(), RelicName(opt.Relic), RelicText(opt.Relic)));
            foreach (var tip in opt.HoverTips ?? Enumerable.Empty<IHoverTip>())
            {
                if (tip is CardHoverTip ct)
                {
                    var cid = ct.Card.Id.ToString();
                    if (!offers.Any(o => (string?)o["id"] == cid))
                        offers.Add(Offer("card", cid, CardName(ct.Card), CardText(ct.Card, PileType.None)));
                    continue;
                }
                if (tip is not HoverTip h || h.CanonicalModel == null) continue;
                var kind = h.CanonicalModel switch
                {
                    RelicModel => "relic",
                    CardModel => "card",
                    PotionModel => "potion",
                    EnchantmentModel => "enchantment",
                    PowerModel => "power",
                    _ => h.CanonicalModel.GetType().Name,
                };
                var id = h.CanonicalModel.Id.ToString();
                if (offers.Any(o => (string?)o["id"] == id)) continue;
                offers.Add(Offer(kind, id, CleanText(h.Title), CleanText(h.Description)));
            }
        }
        catch { }
        return offers.Count > 0 ? offers : null;
    }

    private static Dictionary<string, object?> Offer(string kind, string id, string? name, string? description) => new()
    {
        ["kind"] = kind,
        ["id"] = id,
        ["name"] = name,
        ["description"] = description,
    };

    /// <summary>A rest-site option as the campfire shows it (title, description or the disabled reason).</summary>
    private Dictionary<string, object?> RestOptionInfo(MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption opt, int index) => new()
    {
        ["index"] = index,
        ["option_id"] = opt.OptionId,
        ["name"] = Rendered(() => opt.Title.GetFormattedText(), "rest_site_ui", "OPTION_" + opt.OptionId + ".name"),
        ["description"] = Rendered(() => opt.Description.GetFormattedText(), "rest_site_ui", "OPTION_" + opt.OptionId + ".description"),
        ["is_enabled"] = opt.IsEnabled,
    };

    /// <summary>Compact pile contents (id, name, upgrade, cost); full card details are in hand/deck.</summary>
    private List<Dictionary<string, object?>> PileCards(IEnumerable<CardModel>? cards, bool sort)
    {
        var list = (cards ?? Enumerable.Empty<CardModel>()).Where(c => c != null).Select(c => new Dictionary<string, object?>
        {
            ["id"] = c.Id.ToString(),
            ["name"] = CardName(c),
            ["cost"] = c.EnergyCost == null || c.EnergyCost.CostsX ? 0 : c.EnergyCost.GetResolved(),
            ["upgraded"] = c.IsUpgraded,
        }).ToList();
        if (sort)
            list = list.OrderBy(d => (string?)d["id"], StringComparer.Ordinal).ThenBy(d => (string?)d["name"], StringComparer.Ordinal).ToList();
        return list;
    }

    /// <summary>
    /// The board around a selection that opens mid-combat (Headbutt, Survivor, an enemy's curse
    /// pick): the same hand, enemies, energy, powers and piles a combat_play decision carries.
    /// </summary>
    private Dictionary<string, object?>? CombatSnapshot(MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        if (!MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress) return null;
        try
        {
            var state = CombatPlayState(player);
            var keys = new[] { "round", "energy", "max_energy", "hand", "enemies", "player_powers", "draw_pile_count",
                               "discard_pile_count", "exhaust_pile_count", "draw_pile", "discard_pile", "exhaust_pile",
                               "orbs", "orb_slots", "stars", "osty" };
            return keys.Where(state.ContainsKey).ToDictionary(k => k, k => state[k]);
        }
        catch { return null; }
    }

    private static Dictionary<string, object?>? CardStats(CardModel c)
    {
        var stats = new Dictionary<string, object?>();
        try { foreach (var dv in c.DynamicVars.Values) stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }
        return stats.Count > 0 ? stats : null;
    }

    private static List<string>? CardKeywords(CardModel c)
    {
        var kws = c.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
        return kws?.Count > 0 ? kws : null;
    }

    /// <summary>
    /// Fields every card export shares. <c>cost</c> is the current energy cost; X-cost cards have
    /// <c>costs_x</c> (their cost is all remaining energy) and unplayable cards <c>unplayable</c>.
    /// </summary>
    private Dictionary<string, object?> CardInfo(CardModel c, PileType pile, bool upgradePreview = true)
    {
        var cost = c.EnergyCost;
        var info = new Dictionary<string, object?>
        {
            ["id"] = c.Id.ToString(),
            ["name"] = CardName(c),
            ["cost"] = cost == null || cost.CostsX ? 0 : cost.GetResolved(),
            ["base_cost"] = cost?.Canonical ?? 0,
            ["costs_x"] = cost?.CostsX ?? false,
            ["unplayable"] = c.Keywords?.Contains(CardKeyword.Unplayable) ?? false,
            ["type"] = c.Type.ToString(),
            ["rarity"] = c.Rarity.ToString(),
            ["upgraded"] = c.IsUpgraded,
            ["upgrade_level"] = c.CurrentUpgradeLevel,
            ["description"] = CardText(c, pile),
            ["stats"] = CardStats(c),
            ["keywords"] = CardKeywords(c),
        };
        if (c.Enchantment != null)
        {
            info["enchantment"] = _loc.Text("enchantments", c.Enchantment.Id.Entry + ".title");
            try { if (c.Enchantment.Amount != 0) info["enchantment_amount"] = c.Enchantment.Amount; } catch { }
        }
        if (c.Affliction != null)
        {
            info["affliction"] = _loc.Text("afflictions", c.Affliction.Id.Entry + ".title");
            try { if (c.Affliction.Amount != 0) info["affliction_amount"] = c.Affliction.Amount; } catch { }
        }
        if (upgradePreview) info["after_upgrade"] = GetUpgradedInfo(c);
        return info;
    }

    private Dictionary<string, object?> RelicInfo(RelicModel r)
    {
        var info = new Dictionary<string, object?>
        {
            ["id"] = r.Id.ToString(),
            ["name"] = RelicName(r),
            ["description"] = RelicText(r),
        };
        try { if (r.ShowCounter) info["counter"] = r.DisplayAmount; } catch { }
        try { if (r.IsUsedUp) info["used_up"] = true; } catch { }
        try
        {
            var vars = new Dictionary<string, object?>();
            foreach (var dv in r.DynamicVars.Values) vars[dv.Name] = (int)dv.BaseValue;
            if (vars.Count > 0) info["vars"] = vars;
        }
        catch { }
        return info;
    }

    private Dictionary<string, object?> PotionInfo(PotionModel p) => new()
    {
        ["id"] = p.Id.ToString(),
        ["name"] = PotionName(p),
        ["description"] = PotionText(p),
        ["usage"] = p.Usage.ToString(),
        ["target_type"] = p.TargetType.ToString(),
    };

    private Dictionary<string, object?> PowerInfo(PowerModel pw) => new()
    {
        ["id"] = pw.Id.ToString(),
        ["name"] = PowerName(pw),
        ["description"] = PowerText(pw),
        ["amount"] = pw.Amount,
        ["type"] = pw.Type.ToString(),
    };
}
