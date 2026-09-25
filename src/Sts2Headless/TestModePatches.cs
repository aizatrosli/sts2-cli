using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Models.Relics;

namespace Sts2Headless;

/// <summary>
/// The headless engine runs with <c>TestMode.IsOn</c> (it makes UI waits and VFX no-ops). A few
/// gameplay methods also branch on it and would give different results than the real game:
///
/// * <c>CallingBell.GenerateRewards</c>: fixed Anchor / Gremlin Horn / Mummified Hand instead of a
///   random Common, Uncommon and Rare relic.
/// * <c>Cauldron.GenerateRewards</c>: a fixed list of potions instead of random potion rewards.
/// * <c>MerchantPotionEntry.CalcCost</c>: skips the ±5% price roll, which also skips a draw from
///   the run-long shop RNG and so changes every later shop on the seed.
///
/// In these methods the TestMode reads are replaced with the real-game values (IsOn = false,
/// IsOff = true). tools/TestModeAudit lists every TestMode read in gameplay code so new ones
/// that arrive with a game update get reviewed.
/// </summary>
internal static class TestModePatches
{
    internal static readonly (Type Type, string Method)[] Targets =
    {
        (typeof(CallingBell), "GenerateRewards"),
        (typeof(Cauldron), "GenerateRewards"),
        (typeof(MerchantPotionEntry), "CalcCost"),
    };

    private static readonly MethodInfo? IsOn = AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.TestSupport.TestMode), "IsOn");
    private static readonly MethodInfo? IsOff = AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.TestSupport.TestMode), "IsOff");

    public static void Apply()
    {
        var harmony = new Harmony("sts2headless.testmode");
        var transpiler = new HarmonyMethod(AccessTools.Method(typeof(TestModePatches), nameof(RealGameTranspiler)));
        int patched = 0;
        foreach (var (type, name) in Targets)
        {
            var method = AccessTools.DeclaredMethod(type, name);
            if (method == null || !PatchProcessor.GetOriginalInstructions(method).Any(IsTestModeRead))
            {
                Console.Error.WriteLine($"[WARN] TestMode patch: no TestMode read in {type.Name}.{name}");
                continue;
            }
            harmony.Patch(method, transpiler: transpiler);
            patched++;
        }
        Console.Error.WriteLine($"[INFO] TestMode patches: {patched}/{Targets.Length} gameplay methods use real-game branches");
    }

    private static bool IsTestModeRead(CodeInstruction i) =>
        i.operand is MethodInfo m && (m == IsOn || m == IsOff);

    public static IEnumerable<CodeInstruction> RealGameTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var ins in instructions)
        {
            // Both getters are static and take no arguments, so a constant is a drop-in replacement.
            if (ins.operand is MethodInfo m && m == IsOn)
            {
                ins.opcode = OpCodes.Ldc_I4_0;
                ins.operand = null;
            }
            else if (ins.operand is MethodInfo m2 && m2 == IsOff)
            {
                ins.opcode = OpCodes.Ldc_I4_1;
                ins.operand = null;
            }
            yield return ins;
        }
    }
}
