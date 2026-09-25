using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Headless;

/// <summary>
/// Crystal Sphere event minigame. Its option awaits CrystalSphereMinigame.PlayMinigame, which
/// opens NCrystalSphereScreen and waits for the screen to click cells until the divinations run
/// out. The screen is replaced (LocPatches.CrystalSphereScreenPrefix) by a "crystal_sphere"
/// decision; "crystal_sphere_divine" does what a cell click does (SetTool + CellClicked).
/// </summary>
public partial class RunSimulator
{
    private CrystalSphereMinigame? _pendingCrystalSphere;

    internal void OnCrystalSphereShown(CrystalSphereMinigame minigame)
    {
        _pendingCrystalSphere = minigame;
        Console.Error.WriteLine($"[SIM] Crystal Sphere minigame pending: {minigame.DivinationCount} divinations");
    }

    private Dictionary<string, object?> CrystalSphereState(Player player)
    {
        var mg = _pendingCrystalSphere!;
        int w = mg.GridSize.X, h = mg.GridSize.Y;
        // grid[y][x]: "?" = still hidden, "" = cleared and empty, otherwise the item type under it.
        var grid = new List<List<string>>();
        for (int y = 0; y < h; y++)
        {
            var row = new List<string>();
            for (int x = 0; x < w; x++)
            {
                var cell = mg.cells[x, y];
                row.Add(cell.IsHidden ? "?" : cell.Item == null ? "" : ItemTypeName(cell.Item));
            }
            grid.Add(row);
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "crystal_sphere",
            ["context"] = RunContext(),
            ["divinations_left"] = mg.DivinationCount,
            ["width"] = w,
            ["height"] = h,
            ["grid"] = grid,
            ["tools"] = new[] { "big", "small" },
            ["player"] = PlayerSummary(player),
        };
    }

    private static string ItemTypeName(CrystalSphereItem item) => item switch
    {
        CrystalSphereRelic => "relic",
        CrystalSpherePotion => "potion",
        CrystalSphereCardReward => "card_reward",
        CrystalSphereCurse => "curse",
        CrystalSphereGold => "gold",
        _ => item.GetType().Name,
    };

    private Dictionary<string, object?> DoCrystalSphereDivine(Player player, Dictionary<string, object?>? args)
    {
        var mg = _pendingCrystalSphere;
        if (mg == null)
            return Error("No Crystal Sphere minigame in progress");
        if (args == null || !args.ContainsKey("x") || !args.ContainsKey("y"))
            return Error("crystal_sphere_divine requires 'x' and 'y' (and optional 'tool': big|small)");
        int x = Convert.ToInt32(args["x"]), y = Convert.ToInt32(args["y"]);
        if (x < 0 || y < 0 || x >= mg.GridSize.X || y >= mg.GridSize.Y)
            return Error($"Cell ({x},{y}) is outside the {mg.GridSize.X}x{mg.GridSize.Y} grid");
        var cell = mg.cells[x, y];
        if (!cell.IsHidden)
            return Error($"Cell ({x},{y}) is already revealed");
        var toolName = (args.GetValueOrDefault("tool") as string ?? "big").ToLowerInvariant();
        var tool = toolName switch
        {
            "big" => CrystalSphereMinigame.CrystalSphereToolType.Big,
            "small" => CrystalSphereMinigame.CrystalSphereToolType.Small,
            _ => (CrystalSphereMinigame.CrystalSphereToolType?)null,
        };
        if (tool == null)
            return Error($"Unknown tool '{toolName}' (expected big or small)");

        Log($"Crystal Sphere: divining ({x},{y}) with {toolName} tool");
        mg.SetTool(tool.Value);
        var click = Task.Run(() => mg.CellClicked(cell));
        for (int i = 0; i < 2500 && !click.IsCompleted; i++)
        {
            _syncCtx.Pump();
            Thread.Sleep(2);
        }
        if (click.IsFaulted)
            Log($"Crystal Sphere click failed: {click.Exception?.GetBaseException().Message}");

        if (!mg.IsFinished)
            return DetectDecisionPoint();

        // Out of divinations: the event resumes (rewards for revealed items, then the event finishes).
        _pendingCrystalSphere = null;
        if (_manualFlow) return ResumeBackgroundWork();
        for (int i = 0; i < 2500; i++)
        {
            _syncCtx.Pump();
            if (HasPendingSelection) break;
            var ev = RunManager.Instance.EventSynchronizer?.GetLocalEvent();
            if (ev == null || ev.IsFinished || _runState?.CurrentRoom is not EventRoom) break;
            Thread.Sleep(2);
        }
        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private void AddCrystalSphereActions(List<Dictionary<string, object?>> legal)
    {
        var mg = _pendingCrystalSphere;
        if (mg == null) return;
        for (int y = 0; y < mg.GridSize.Y; y++)
            for (int x = 0; x < mg.GridSize.X; x++)
            {
                if (!mg.cells[x, y].IsHidden) continue;
                foreach (var tool in new[] { "big", "small" })
                    legal.Add(Act("crystal_sphere_divine", new() { ["x"] = x, ["y"] = y, ["tool"] = tool }));
            }
    }
}
