using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Vfx.Utilities;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Headless;

/// <summary>
/// Game logic that touches UI singletons (NGame, NEventRoom, NAudioManager...) which are null
/// headless. Each of these threw partway through, found by driving every event and full runs
/// with a random agent. An event option that throws never advances, so the same option comes
/// back forever; a monster hook that throws kills the combat turn loop (false game_over):
///
/// * Amalgamator, PunchOff, DenseVegetation call <c>NGame.Instance.ScreenShakeTrauma/ScreenRumble</c>
///   unguarded. Amalgamator had already removed the two Strikes/Defends, so every retry ate more
///   of the deck. The calls are rewritten to null-safe static helpers.
/// * Trial.Accept guards its portrait/VFX work with <c>LocalContext.IsMe(Owner)</c> and then uses
///   <c>NEventRoom.Instance</c>. Those guards only protect UI, so they are forced false.
/// * DenseVegetation.Rest plays audio through <c>NDebugAudioManager.Instance</c> behind the same
///   kind of IsMe guard; forced false as well.
/// * Crusher/Rocket (Kaiser Crab arms) play their death sound through an unguarded
///   <c>NAudioManager.Instance.PlayOneShot</c>; made null-safe like the screen shakes.
/// * Trial's "Double Down" opens the abandon-run confirmation popup; confirming it abandons the run.
///   Headless it calls <c>RunManager.Abandon()</c> directly, which ends the run in defeat.
/// * Foul Potion outside combat is thrown at a merchant. Its usability check asks the UI for the
///   merchant button (null headless), so it was never usable; at the Fake Merchant its use also
///   needs the event's <c>NFakeMerchant</c> node to start the fight. Headless it is usable in a
///   shop and at a Fake Merchant that has not been attacked yet, and the throw calls the event's
///   own <c>FoulPotionThrown</c> (fight with the merchant, his relics as rewards).
/// </summary>
internal static class HeadlessUiPatches
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                     | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly MethodInfo? ShakeTrauma = AccessTools.Method(typeof(NGame), nameof(NGame.ScreenShakeTrauma));
    private static readonly MethodInfo? Rumble = AccessTools.Method(typeof(NGame), nameof(NGame.ScreenRumble));
    private static readonly MethodInfo? DebugPlay =
        AccessTools.Method(typeof(NDebugAudioManager), nameof(NDebugAudioManager.Play), new[] { typeof(string), typeof(float), typeof(PitchVariance) });
    private static readonly MethodInfo? PlayOneShot =
        AccessTools.Method(typeof(NAudioManager), nameof(NAudioManager.PlayOneShot), new[] { typeof(string), typeof(float) });

    public static void SafeScreenShakeTrauma(NGame? game, ShakeStrength strength) => game?.ScreenShakeTrauma(strength);

    public static void SafeScreenRumble(NGame? game, ShakeStrength strength, ShakeDuration duration, RumbleStyle style) =>
        game?.ScreenRumble(strength, duration, style);

    public static void SafePlayOneShot(NAudioManager? audio, string path, float volume) => audio?.PlayOneShot(path, volume);
    public static int SafeDebugPlay(NDebugAudioManager? audio, string stream, float volume, PitchVariance variance) =>
        audio?.Play(stream, volume, variance) ?? 0;

    /// <summary>
    /// The shop's card-removal slot marks its entry used (<c>NMerchantCardRemoval.OnCardRemovalUsed</c>
    /// → <c>SetUsed</c>); headless nothing did, so removal could be bought again in the same visit.
    /// </summary>
    public static void PurchaseCompletedPostfix(MerchantEntry entry)
    {
        if (entry is MerchantCardRemovalEntry removal) removal.SetUsed();
    }

    /// <summary>The local player's Fake Merchant while its shop is open (not yet attacked).</summary>
    internal static FakeMerchant? ActiveFakeMerchant(Player? player)
    {
        if (player?.RunState?.CurrentRoom is not EventRoom room || room.CanonicalEvent is not FakeMerchant)
            return null;
        return RunManager.Instance.EventSynchronizer?.GetEventForPlayer(player) is FakeMerchant fm
               && fm.Inventory != null && !fm.StartedFight ? fm : null;
    }

    public static void FoulPotionUsablePostfix(FoulPotion __instance, ref bool __result)
    {
        if (__result || CombatManager.Instance.IsInProgress) return;
        __result = __instance.Owner?.RunState?.CurrentRoom is MerchantRoom || ActiveFakeMerchant(__instance.Owner) != null;
    }

    public static bool FoulPotionOnUsePrefix(FoulPotion __instance, ref Task __result)
    {
        if (CombatManager.Instance.IsInProgress || ActiveFakeMerchant(__instance.Owner) is not { } fm || fm.Node != null)
            return true;
        var players = __instance.Owner.RunState.Players;
        __result = Task.WhenAll(players.Select(p =>
            ((FakeMerchant)RunManager.Instance.EventSynchronizer.GetEventForPlayer(p)).FoulPotionThrown(__instance)));
        return false;
    }

    public static void Apply()
    {
        var harmony = new Harmony("sts2headless.ui");
        int nullSafe = PatchMatching(harmony,
            new[] { typeof(Amalgamator), typeof(PunchOff), typeof(DenseVegetation), typeof(Crusher), typeof(Rocket),
                    typeof(JungleMazeAdventure), typeof(DigRestSiteOption) },
            IsNullSafeTarget, nameof(NullSafeCallTranspiler));
        int trial = PatchMatching(harmony, new[] { typeof(Trial) },
            i => IsLocalContextIsMe(i.operand), nameof(UiGuardOffTranspiler),
            m => m.Name is "Accept" or "AddVfxAnchoredToPortrait");
        int rest = PatchMatching(harmony, new[] { typeof(DenseVegetation) },
            i => IsLocalContextIsMe(i.operand), nameof(UiGuardOffTranspiler),
            m => m.Name == "Rest");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(Trial), "DoubleDown"),
                new HarmonyMethod(AccessTools.Method(typeof(HeadlessUiPatches), nameof(TrialDoubleDownPrefix))));
            trial++;
        }
        catch (Exception ex) { PatchReport.Warn($"Trial.DoubleDown patch failed: {ex.Message}"); }
        harmony.Patch(AccessTools.Method(typeof(MerchantEntry), nameof(MerchantEntry.InvokePurchaseCompleted)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HeadlessUiPatches), nameof(PurchaseCompletedPostfix))));
        int foul = 0;
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(FoulPotion), nameof(FoulPotion.PassesCustomUsabilityCheck)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(HeadlessUiPatches), nameof(FoulPotionUsablePostfix))));
            foul++;
            harmony.Patch(AccessTools.Method(typeof(FoulPotion), "OnUse"),
                new HarmonyMethod(AccessTools.Method(typeof(HeadlessUiPatches), nameof(FoulPotionOnUsePrefix))));
            foul++;
        }
        catch (Exception ex) { PatchReport.Warn($"FoulPotion patch failed: {ex.Message}"); }
        Console.Error.WriteLine($"[INFO] Headless UI patches: {nullSafe} null-safe call sites, {trial} Trial methods, {rest} DenseVegetation methods");
        PatchReport.Expect("Headless UI null-safe call sites", nullSafe, 11);
        PatchReport.Expect("Headless UI Trial methods", trial, 3);
        PatchReport.Expect("Headless UI DenseVegetation methods", rest, 1);
        PatchReport.Expect("Headless UI Foul Potion methods", foul, 2);
    }

    // ─── transpilers ───

    public static IEnumerable<CodeInstruction> NullSafeCallTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var replacements = new Dictionary<MethodInfo, MethodInfo>();
        if (ShakeTrauma != null) replacements[ShakeTrauma] = AccessTools.Method(typeof(HeadlessUiPatches), nameof(SafeScreenShakeTrauma));
        if (Rumble != null) replacements[Rumble] = AccessTools.Method(typeof(HeadlessUiPatches), nameof(SafeScreenRumble));
        if (PlayOneShot != null) replacements[PlayOneShot] = AccessTools.Method(typeof(HeadlessUiPatches), nameof(SafePlayOneShot));
        if (DebugPlay != null) replacements[DebugPlay] = AccessTools.Method(typeof(HeadlessUiPatches), nameof(SafeDebugPlay));
        foreach (var ins in instructions)
        {
            // The instance is already on the stack as the first argument, so a static call with
            // the same argument list is a drop-in replacement.
            if (ins.operand is MethodInfo m && replacements.TryGetValue(m, out var safe))
            {
                ins.opcode = OpCodes.Call;
                ins.operand = safe;
            }
            yield return ins;
        }
    }

    /// <summary>Replace each LocalContext.IsMe(player) with false: pop the argument, push 0.</summary>
    public static IEnumerable<CodeInstruction> UiGuardOffTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var ins in instructions)
        {
            if (ins.opcode == OpCodes.Call && IsLocalContextIsMe(ins.operand))
            {
                ins.opcode = OpCodes.Pop; // keeps labels/blocks attached to this instruction
                ins.operand = null;
                yield return ins;
                yield return new CodeInstruction(OpCodes.Ldc_I4_0);
                continue;
            }
            yield return ins;
        }
    }

    public static bool TrialDoubleDownPrefix(ref Task __result)
    {
        Console.Error.WriteLine("[SIM] Trial: Double Down abandons the run");
        RunManager.Instance.Abandon();
        __result = Task.CompletedTask;
        return false;
    }

    // ─── helpers ───

    private static bool IsNullSafeTarget(CodeInstruction i) =>
        i.operand is MethodInfo m && (m == ShakeTrauma || m == Rumble || m == PlayOneShot || m == DebugPlay);

    private static bool IsLocalContextIsMe(object? operand) =>
        operand is MethodInfo m && m.DeclaringType == typeof(LocalContext) && m.Name == "IsMe"
        && m.ReturnType == typeof(bool) && m.GetParameters().Length == 1;

    /// <summary>
    /// Patch every method (and async state machine MoveNext) of <paramref name="types"/> whose
    /// original IL contains an instruction matching <paramref name="match"/>.
    /// </summary>
    private static int PatchMatching(Harmony harmony, Type[] types, Func<CodeInstruction, bool> match,
        string transpilerName, Func<MethodBase, bool>? ownerFilter = null)
    {
        var transpiler = new HarmonyMethod(AccessTools.Method(typeof(HeadlessUiPatches), transpilerName));
        int patched = 0;
        foreach (var type in types)
        {
            var candidates = new List<MethodBase>();
            foreach (var m in type.GetMethods(All))
                if (ownerFilter == null || ownerFilter(m)) candidates.Add(m);
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                // Async/iterator state machines are named <Owner>d__N.
                if (nested.IsGenericTypeDefinition) continue;
                if (ownerFilter != null && !candidates.Any(c => nested.Name.StartsWith("<" + c.Name + ">"))) continue;
                var moveNext = nested.GetMethod("MoveNext", All);
                if (moveNext != null) candidates.Add(moveNext);
            }

            foreach (var method in candidates)
            {
                if (method.IsAbstract || method.GetMethodBody() == null) continue;
                try
                {
                    if (!PatchProcessor.GetOriginalInstructions(method).Any(match)) continue;
                    harmony.Patch(method, transpiler: transpiler);
                    patched++;
                }
                catch (Exception ex)
                {
                    PatchReport.Warn($"Event patch failed for {method.DeclaringType?.Name}.{method.Name}: {ex.Message}");
                }
            }
        }
        return patched;
    }
}
