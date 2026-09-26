using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;

namespace Sts2Headless;

/// <summary>
/// The engine's test selector hook (<c>ICardSelector.GetSelectedCards(options, min, max)</c>) drops
/// what the UI knows about a selection: whether it can be cancelled (<c>CardSelectorPrefs.Cancelable</c>:
/// Smith, Cook, shop removal), its prompt, and for choose-a-card screens whether skipping is allowed
/// (<c>FromChooseACardScreen</c> always passes (0, 1), ignoring <c>canSkip</c>). A prefix on every
/// <c>CardSelectCmd.From*</c> entry point records these; the headless selector consumes them.
/// </summary>
internal static class SelectionPrefsPatches
{
    internal sealed record Info(string Source, bool Cancelable, bool? CanSkip, string? Prompt)
    {
        // What the UI screen is given beyond the (options, min, max) the selector hook sees; the
        // flysts payload reports it the way the mod read it off the screen (prefs, enchantment).
        public string? PromptKey { get; init; }
        public string? PromptFormatted { get; init; }
        public int? MinSelect { get; init; }
        public int? MaxSelect { get; init; }
        public bool RequireManualConfirmation { get; init; }
        public MegaCrit.Sts2.Core.Models.EnchantmentModel? Enchantment { get; init; }
        public int? EnchantmentAmount { get; init; }
        public MegaCrit.Sts2.Core.Entities.Cards.PileType? PileType { get; init; }
    }

    private static Info? _next;

    /// <summary>The prefs of the selection that is about to reach the selector (consumed once).</summary>
    internal static Info? Take()
    {
        var info = _next;
        _next = null;
        return info;
    }

    public static void Apply()
    {
        var harmony = new Harmony("sts2headless.selectionprefs");
        var prefix = new HarmonyMethod(typeof(SelectionPrefsPatches).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        int patched = 0;
        foreach (var method in typeof(CardSelectCmd).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            // Every entry point, including those without prefs, so a selection that never reached
            // the selector cannot leave stale prefs for the next one.
            if (!method.Name.StartsWith("From", StringComparison.Ordinal) || !typeof(Task).IsAssignableFrom(method.ReturnType))
                continue;
            harmony.Patch(method, prefix: prefix);
            patched++;
        }
        Console.Error.WriteLine($"[INFO] Patched {patched} CardSelectCmd entry points (selection prefs)");
        PatchReport.Expect("CardSelectCmd selection prefs", patched, 16);
    }

    private static void Prefix(MethodBase __originalMethod, object[] __args)
    {
        bool cancelable = false;
        bool? canSkip = null;
        string? prompt = null, promptKey = null, promptFormatted = null;
        int? min = null, max = null, enchantmentAmount = null;
        bool requireManual = false;
        MegaCrit.Sts2.Core.Models.EnchantmentModel? enchantment = null;
        MegaCrit.Sts2.Core.Entities.Cards.PileType? pileType = null;
        var parameters = __originalMethod.GetParameters();
        for (int i = 0; i < parameters.Length && i < __args.Length; i++)
        {
            if (__args[i] is CardSelectorPrefs prefs)
            {
                cancelable = prefs.Cancelable;
                min = prefs.MinSelect;
                max = prefs.MaxSelect;
                requireManual = prefs.RequireManualConfirmation;
                promptKey = prefs.Prompt?.LocEntryKey;
                // Formatted now: the prompt carries its own variables (Amount, MinCount, MaxCount).
                try { promptFormatted = prefs.Prompt?.GetFormattedText(); } catch { }
                try { prompt = RunSimulator.CleanText(promptFormatted); }
                catch { prompt = promptKey; }
            }
            else if (parameters[i].Name == "canSkip" && __args[i] is bool b)
            {
                canSkip = b;
            }
            else if (__args[i] is MegaCrit.Sts2.Core.Models.EnchantmentModel e)
            {
                enchantment = e;
            }
            else if (parameters[i].Name == "amount" && __args[i] is int amount)
            {
                enchantmentAmount = amount;
            }
            else if (__args[i] is MegaCrit.Sts2.Core.Entities.Cards.CardPile pile)
            {
                pileType = pile.Type;
            }
        }
        var source = __originalMethod.Name.StartsWith("From", StringComparison.Ordinal) ? __originalMethod.Name[4..] : __originalMethod.Name;
        _next = new Info(source, cancelable, canSkip, string.IsNullOrWhiteSpace(prompt) ? null : prompt)
        {
            PromptKey = promptKey,
            PromptFormatted = promptFormatted,
            MinSelect = min,
            MaxSelect = max,
            RequireManualConfirmation = requireManual,
            Enchantment = enchantment,
            EnchantmentAmount = enchantmentAmount,
            PileType = pileType,
        };
    }
}
