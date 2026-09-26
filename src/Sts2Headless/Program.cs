using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Headless;

class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// Locate the directory containing sts2.dll: STS2_LIB env, walk up from BaseDirectory, then BaseDirectory/lib.
    /// </summary>
    private static string ResolveLibDirectory()
    {
        var envLib = Environment.GetEnvironmentVariable("STS2_LIB");
        if (!string.IsNullOrWhiteSpace(envLib))
        {
            var p = Path.GetFullPath(envLib.Trim());
            if (Directory.Exists(p) && File.Exists(Path.Combine(p, "sts2.dll")))
                return p;
        }

        var dir = AppContext.BaseDirectory;
        for (var depth = 0; depth < 16 && !string.IsNullOrEmpty(dir); depth++)
        {
            var candidate = Path.Combine(dir, "lib");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "sts2.dll")))
                return Path.GetFullPath(candidate);
            dir = Directory.GetParent(dir)?.FullName ?? "";
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "lib"));
    }

    // The protocol channel: the process's real stdout. Console.Out is pointed at stderr so that
    // anything else printing to stdout (e.g. Sentry's "GDExtension not loaded" notice) cannot
    // corrupt the JSON-lines stream.
    static readonly TextWriter Protocol = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = false };

    /// <summary>Protocol schema version, reported in the ready message.</summary>
    const int SchemaVersion = 2;

    /// <summary>
    /// Debug commands (set_player, enter_room, set_draw_order) edit the game state. They share the
    /// channel an agent acts on, so they are only accepted when the engine is started with --debug
    /// (or STS2_DEBUG_COMMANDS=1): an RL agent cannot reach them by accident.
    /// </summary>
    static bool DebugCommands;

    static void Main(string[] args)
    {
        DebugCommands = args.Contains("--debug") || Environment.GetEnvironmentVariable("STS2_DEBUG_COMMANDS") == "1";
        Console.SetOut(Console.Error);
        // Prevent unhandled exceptions from crashing the process
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine($"[FATAL] Unhandled: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            PatchReport.EngineWarning($"Unobserved task exception: {e.Exception?.GetBaseException().Message}");
            e.SetObserved();
        };

        var libDir = ResolveLibDirectory();

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var path = Path.Combine(libDir, name.Name + ".dll");
            if (File.Exists(path))
                return ctx.LoadFromAssemblyPath(Path.GetFullPath(path));

            // Also check game directory (via STS2_GAME_DIR env var)
            var gameDir = Environment.GetEnvironmentVariable("STS2_GAME_DIR") ?? "";
            if (!string.IsNullOrEmpty(gameDir))
            {
                path = Path.Combine(gameDir, name.Name + ".dll");
                if (File.Exists(path))
                    return ctx.LoadFromAssemblyPath(path);
            }

            return null;
        };

        var sim = new RunSimulator();
        WriteLine(new Dictionary<string, object?>
        {
            ["type"] = "ready",
            ["version"] = "0.2.0",
            ["schema_version"] = SchemaVersion,
            ["debug_commands"] = DebugCommands,
        });

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            Dictionary<string, object?>? result;
            object? requestId = null;
            try
            {
                var cmd = JsonSerializer.Deserialize<JsonElement>(line);
                // Echoed back so a client can drop a late reply to a request it gave up on.
                if (cmd.ValueKind == JsonValueKind.Object && cmd.TryGetProperty("request_id", out var rid))
                    requestId = rid.ValueKind == JsonValueKind.Number && rid.TryGetInt64(out var n) ? n : rid.ToString();
                // Anything but an action or a read may change state: the next action revalidates
                // against a fresh legal set (actions refresh it themselves).
                var cmdName = cmd.TryGetProperty("cmd", out var cn) && cn.ValueKind == JsonValueKind.String ? cn.GetString() : null;
                if (cmdName is not ("action" or "get_map" or "flysts_state" or "flysts_action")) sim.InvalidateLegal();
                if (cmdName is "start_run" or "load_save") PatchReport.DrainEngineWarnings(); // belong to the old run
                result = HandleCommand(sim, cmd);
                sim.AttachLegalActions(result);
                sim.TrimSaveCallLog();
                if (cmdName is "start_run" or "load_save") PatchReport.RemoveMonoModTempFiles();
                var warnings = PatchReport.DrainEngineWarnings();
                if (warnings.Count > 0 && result != null) result["warnings"] = warnings;
                if (cmdName is "start_run" or "load_save" && result != null && PatchReport.Warnings.Count > 0)
                    result["patch_warnings"] = PatchReport.Warnings;
            }
            catch (JsonException ex)
            {
                result = new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Invalid JSON: {ex.Message}" };
            }
            catch (Exception ex)
            {
                result = new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"{ex.GetType().Name}: {ex.Message}", ["stack_trace"] = ex.ToString() };
            }

            if (result != null)
            {
                if (requestId != null) result["request_id"] = requestId;
                WriteLine(result);
                if (result.TryGetValue("type", out var resultTypeObj) &&
                    string.Equals(resultTypeObj as string, "quit_result", StringComparison.Ordinal))
                {
                    break;
                }
            }
        }
    }

    static Dictionary<string, object?>? HandleCommand(RunSimulator sim, JsonElement cmd)
    {
        var cmdType = cmd.GetProperty("cmd").GetString() ?? "";
        if (cmdType is "set_player" or "enter_room" or "set_draw_order" && !DebugCommands)
            return new Dictionary<string, object?>
            {
                ["type"] = "error",
                ["message"] = $"'{cmdType}' is a debug command; start the engine with --debug (or STS2_DEBUG_COMMANDS=1)",
            };
        switch (cmdType)
        {
            case "start_run" when cmd.TryGetProperty("payload", out var payload) && payload.GetString() == "flysts":
                // The FlystsBridge mod's payload and actions (flysts_state / flysts_action).
                return sim.StartFlystsRun(
                    cmd.TryGetProperty("character", out var fch) ? fch.GetString() ?? "Ironclad" : "Ironclad",
                    cmd.TryGetProperty("ascension", out var fasc) ? fasc.GetInt32() : 0,
                    cmd.TryGetProperty("seed", out var fs) ? fs.GetString() : null,
                    cmd.TryGetProperty("game_mode", out var fgm) ? fgm.GetString() : null,
                    cmd.TryGetProperty("profile", out var fpr) ? fpr.GetString() : null);

            case "flysts_state":
                return sim.FlystsGetState();

            case "content_catalog":
                return sim.ContentCatalog(cmd.TryGetProperty("profile", out var cpr) ? cpr.GetString() : null);

            case "flysts_action":
            {
                var faction = cmd.TryGetProperty("action", out var fa) ? fa.GetString() ?? "" : "";
                var fargs = new Dictionary<string, object?>();
                if (cmd.TryGetProperty("args", out var fargsElem) && fargsElem.ValueKind == JsonValueKind.Object)
                    foreach (var prop in fargsElem.EnumerateObject())
                        fargs[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Number => prop.Value.GetInt32(),
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => prop.Value.ToString(),
                        };
                return sim.FlystsAction(faction, fargs);
            }

            case "start_run":
                return sim.StartRun(
                    cmd.TryGetProperty("character", out var ch) ? ch.GetString() ?? "Ironclad" : "Ironclad",
                    cmd.TryGetProperty("ascension", out var asc) ? asc.GetInt32() : 0,
                    cmd.TryGetProperty("seed", out var s) ? s.GetString() : null,
                    cmd.TryGetProperty("flow", out var flow) ? flow.GetString() : null,
                    cmd.TryGetProperty("act1", out var act1) ? act1.GetString() : null
                );

            case "action":
            {
                var action = cmd.GetProperty("action").GetString() ?? "";
                Dictionary<string, object?>? actionArgs = null;
                if (cmd.TryGetProperty("args", out var argsElem))
                {
                    actionArgs = new Dictionary<string, object?>();
                    foreach (var prop in argsElem.EnumerateObject())
                    {
                        actionArgs[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Number => prop.Value.GetInt32(),
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => prop.Value.ToString(),
                        };
                    }
                }
                return sim.ExecuteAction(action, actionArgs);
            }

            case "load_save":
            {
                var savePath = cmd.TryGetProperty("path", out var sp) ? sp.GetString() : null;
                var saveJson = cmd.TryGetProperty("json", out var sj) ? sj.GetString() : null;
                if (saveJson == null && savePath != null)
                {
                    if (!File.Exists(savePath))
                        return new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Save file not found: {savePath}" };
                    saveJson = File.ReadAllText(savePath);
                }
                if (saveJson == null)
                    return new Dictionary<string, object?> { ["type"] = "error", ["message"] = "Provide 'path' or 'json' for load_save" };
                var loadFlow = cmd.TryGetProperty("flow", out var lf) ? lf.GetString() : null;
                return sim.LoadSave(saveJson, loadFlow);
            }
            case "get_map":
                return sim.GetFullMap();

            case "get_state":
                return sim.GetState();

            case "set_player":
            {
                var args = new Dictionary<string, JsonElement>();
                foreach (var prop in cmd.EnumerateObject())
                    if (prop.Name != "cmd") args[prop.Name] = prop.Value;
                var setPlayer = sim.SetPlayer(args);
                return IsError(setPlayer) ? setPlayer : sim.FlystsRefreshAfterDebug() ?? setPlayer;
            }

            case "enter_room":
            {
                var roomType = cmd.TryGetProperty("type", out var rt) ? rt.GetString() ?? "" : "";
                var encounter = cmd.TryGetProperty("encounter", out var enc) ? enc.GetString() : null;
                var eventId = cmd.TryGetProperty("event", out var ev) ? ev.GetString() : null;
                var entered = sim.EnterRoom(roomType, encounter, eventId);
                return IsError(entered) ? entered : sim.FlystsRefreshAfterDebug() ?? entered;
            }

            case "set_draw_order":
            {
                var cards = new List<string>();
                if (cmd.TryGetProperty("cards", out var cardsArr))
                    foreach (var c in cardsArr.EnumerateArray())
                        cards.Add(c.GetString() ?? "");
                var ordered = sim.SetDrawOrder(cards);
                return IsError(ordered) ? ordered : sim.FlystsRefreshAfterDebug() ?? ordered;
            }

            case "write_continue_save":
            {
                var outputPath = cmd.TryGetProperty("path", out var op) ? op.GetString() : null;
                return sim.SaveCheckpoint(outputPath);
            }

            case "quit":
            {
                var outputPath = cmd.TryGetProperty("path", out var op) ? op.GetString() : null;
                if (!string.IsNullOrEmpty(outputPath))
                {
                    var saveResult = sim.SaveCheckpoint(outputPath);
                    bool saveOk = saveResult.TryGetValue("success", out var sObj) && sObj is bool b && b;
                    if (!saveOk)
                    {
                        // Save failed — do NOT clean up so the caller can retry with a different path.
                        return new Dictionary<string, object?>
                        {
                            ["type"] = "save_error",
                            ["save"] = saveResult,
                        };
                    }
                    sim.CleanUp();
                    return new Dictionary<string, object?>
                    {
                        ["type"] = "quit_result",
                        ["success"] = true,
                        ["save"] = saveResult,
                    };
                }
                sim.CleanUp();
                return new Dictionary<string, object?>
                {
                    ["type"] = "quit_result",
                    ["success"] = true,
                    ["save"] = null,
                };
            }

            default:
                return new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Unknown command: {cmdType}" };
        }
    }

    /// <summary>A command's own error, returned as is: the flysts refresh after a debug command
    /// would otherwise replace it with the unchanged decision and a plain ok.</summary>
    static bool IsError(Dictionary<string, object?> result) =>
        result.TryGetValue("type", out var t) && t as string == "error";

    static void WriteLine(Dictionary<string, object?> data)
    {
        Protocol.WriteLine(JsonSerializer.Serialize(data, JsonOpts));
        Protocol.Flush();
    }
}
