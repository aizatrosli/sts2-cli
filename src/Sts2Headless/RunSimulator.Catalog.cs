using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Timeline.Epochs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2Headless;

public partial class RunSimulator
{
    /// <summary>`content_catalog`: every card, relic, potion, event, encounter, monster and power
    /// the game's ModelDb holds, with its pool / act (read-only). Coverage reports use it as the
    /// denominator: what exists in this game build, not in a scraped list. With `profile` (a
    /// progress.save), cards, relics and potions carry `unlocked`: whether a singleplayer run
    /// from that profile can generate them (the pools' own GetUnlocked* filters); events carry
    /// it too: a regular event if its act is unlocked (ActModel.IsUnlocked) and it isn't held back
    /// by an unrevealed Event1-3 epoch (ActModel.GenerateRooms), an Ancient if some unlocked act
    /// offers it (GetUnlockedAncients, UnlockState.SharedAncients). `conditional` marks events
    /// that override EventModel.IsAllowed (run-state conditions checked when the room is rolled).
    /// `enchantments` / `afflictions` list the non-mock ones.</summary>
    public Dictionary<string, object?> ContentCatalog(string? profilePath = null)
    {
        try
        {
            EnsureModelDbInitialized();
            HashSet<string>? cardsOn = null, relicsOn = null, potionsOn = null, eventsOn = null;
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
                eventsOn = UnlockedEvents(unlocks);
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
            Dictionary<string, object?> Event(EventModel e, string act) => new()
            {
                ["id"] = e.Id.Entry, ["act"] = act,
                ["unlocked"] = eventsOn?.Contains(e.Id.Entry),
                ["conditional"] = e.GetType().GetMethod(nameof(EventModel.IsAllowed))?.DeclaringType != typeof(EventModel),
            };
            var events = ModelDb.Acts.SelectMany(a => a.AllEvents.Select(e => Event(e, a.Id.Entry)))
                .Concat(ModelDb.AllSharedEvents.Select(e => Event(e, "shared")))
                .Concat(ModelDb.AllAncients.Select(e => Event(e, "ancient")))
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
                ["enchantments"] = ModelDb.DebugEnchantments.Where(e => !IsMock(e)).Select(e => e.Id.Entry).Distinct().ToList(),
                ["afflictions"] = ModelDb.DebugAfflictions.Where(a => !IsMock(a)).Select(a => a.Id.Entry).Distinct().ToList(),
            };
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("content_catalog failed", ex);
        }
    }

    private static bool IsMock(AbstractModel model) => model.GetType().Namespace?.EndsWith(".Mocks") == true;

    /// <summary>The events a run from this profile can roll: each unlocked act's events and the
    /// shared ones minus the unrevealed Event1-3 epochs' (ActModel.GenerateRooms), the act's
    /// unlocked Ancients, and the shared Ancients (Darv, DarvEpoch).</summary>
    private static HashSet<string> UnlockedEvents(UnlockState unlocks)
    {
        var held = new List<EventModel>();
        if (!unlocks.IsEpochRevealed<Event1Epoch>()) held.AddRange(Event1Epoch.Events);
        if (!unlocks.IsEpochRevealed<Event2Epoch>()) held.AddRange(Event2Epoch.Events);
        if (!unlocks.IsEpochRevealed<Event3Epoch>()) held.AddRange(Event3Epoch.Events);
        var on = new HashSet<string>();
        foreach (var act in ModelDb.Acts.Where(a => a.IsUnlocked(unlocks)))
        {
            foreach (var e in act.AllEvents.Concat(ModelDb.AllSharedEvents))
                if (!held.Any(h => h.Id == e.Id)) on.Add(e.Id.Entry);
            foreach (var a in act.GetUnlockedAncients(unlocks)) on.Add(a.Id.Entry);
        }
        foreach (var a in unlocks.SharedAncients) on.Add(a.Id.Entry);
        return on;
    }
}
