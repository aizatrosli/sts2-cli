using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2Headless;

public partial class RunSimulator
{
    /// <summary>`content_catalog`: every card, relic, potion, event, encounter, monster and power
    /// the game's ModelDb holds, with its pool / act (read-only). Coverage reports use it as the
    /// denominator: what exists in this game build, not in a scraped list. With `profile` (a
    /// progress.save), cards, relics and potions carry `unlocked`: whether a singleplayer run
    /// from that profile can generate them (the pools' own GetUnlocked* filters).</summary>
    public Dictionary<string, object?> ContentCatalog(string? profilePath = null)
    {
        try
        {
            EnsureModelDbInitialized();
            HashSet<string>? cardsOn = null, relicsOn = null, potionsOn = null;
            if (!string.IsNullOrWhiteSpace(profilePath))
            {
                if (_runState != null) return Error("content_catalog with a profile needs no run in progress");
                if (!File.Exists(profilePath)) return Error($"profile not found: '{profilePath}'");
                if (!LoadFlystsProfile(profilePath, out var profileError)) return Error(profileError);
                var unlocks = UnlockState.FromSerializable(SaveManager.Instance.GenerateUnlockStateFromProgress().ToSerializable());
                cardsOn = ModelDb.AllCardPools.SelectMany(p => p.GetUnlockedCards(unlocks, CardMultiplayerConstraint.SingleplayerOnly))
                    .Select(c => c.Id.Entry).ToHashSet();
                relicsOn = unlocks.Relics.Select(r => r.Id.Entry).ToHashSet();
                potionsOn = unlocks.Potions.Select(p => p.Id.Entry).ToHashSet();
            }
            var cards = ModelDb.AllCardPools.SelectMany(pool => pool.AllCards.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id.Entry, ["pool"] = pool.Id.Entry,
                ["rarity"] = c.Rarity.ToString().ToLowerInvariant(), ["type"] = c.Type.ToString().ToLowerInvariant(),
                ["unlocked"] = cardsOn?.Contains(c.Id.Entry),
            })).ToList();
            var relics = ModelDb.AllRelicPools.SelectMany(pool => pool.AllRelics.Select(r => new Dictionary<string, object?>
            {
                ["id"] = r.Id.Entry, ["pool"] = pool.Id.Entry, ["rarity"] = r.Rarity.ToString().ToLowerInvariant(),
                ["unlocked"] = relicsOn?.Contains(r.Id.Entry),
            })).ToList();
            var potions = ModelDb.AllPotionPools.SelectMany(pool => pool.AllPotions.Select(p => new Dictionary<string, object?>
            {
                ["id"] = p.Id.Entry, ["pool"] = pool.Id.Entry, ["rarity"] = p.Rarity.ToString().ToLowerInvariant(),
                ["unlocked"] = potionsOn?.Contains(p.Id.Entry),
            })).ToList();
            var events = ModelDb.Acts.SelectMany(a => a.AllEvents.Select(e => new Dictionary<string, object?> { ["id"] = e.Id.Entry, ["act"] = a.Id.Entry }))
                .Concat(ModelDb.AllSharedEvents.Select(e => new Dictionary<string, object?> { ["id"] = e.Id.Entry, ["act"] = "shared" }))
                .Concat(ModelDb.AllAncients.Select(e => new Dictionary<string, object?> { ["id"] = e.Id.Entry, ["act"] = "ancient" }))
                .ToList();
            var encounters = ModelDb.Acts.SelectMany(a => a.AllEncounters.Select(e => new Dictionary<string, object?>
            {
                ["id"] = e.Id.Entry, ["act"] = a.Id.Entry, ["room_type"] = e.RoomType.ToString().ToLowerInvariant(),
            })).ToList();
            return new Dictionary<string, object?>
            {
                ["type"] = "content_catalog",
                ["characters"] = ModelDb.AllCharacters.Select(c => c.Id.Entry).ToList(),
                ["cards"] = cards,
                ["relics"] = relics,
                ["potions"] = potions,
                ["events"] = events,
                ["encounters"] = encounters,
                ["monsters"] = ModelDb.Monsters.Select(m => m.Id.Entry).Distinct().ToList(),
                ["powers"] = ModelDb.AllPowers.Select(p => p.Id.Entry).Distinct().ToList(),
            };
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("content_catalog failed", ex);
        }
    }
}
