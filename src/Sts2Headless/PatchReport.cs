namespace Sts2Headless;

/// <summary>
/// Collects Harmony patch problems found at startup: a patch that failed, or matched a different
/// number of sites than expected. Game updates rename or reshape methods, and a patch that quietly
/// matches nothing brings back the crash or divergence it was fixing. Problems are logged and
/// reported as <c>patch_warnings</c> on start_run / load_save responses.
/// </summary>
internal static class PatchReport
{
    /// <summary>
    /// Engine failures that happened off the main path (a background event option or reward task
    /// that threw). They used to only reach the log; the next response carries them as "warnings".
    /// </summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> EngineWarnings = new();

    public static void EngineWarning(string message)
    {
        EngineWarnings.Enqueue(message);
        Console.Error.WriteLine($"[WARN] {message}");
    }

    public static List<string> DrainEngineWarnings()
    {
        var list = new List<string>();
        while (EngineWarnings.TryDequeue(out var w)) list.Add(w);
        return list;
    }

    private static readonly List<string> _warnings = new();

    public static IReadOnlyList<string> Warnings
    {
        get { lock (_warnings) return _warnings.ToList(); }
    }

    public static void Warn(string message)
    {
        lock (_warnings) _warnings.Add(message);
        Console.Error.WriteLine($"[WARN] {message}");
    }

    private static bool _tempFilesRemoved;

    /// <summary>
    /// MonoMod (Harmony's backend) writes a native helper to /tmp/mm-exhelper.so.XXXXXX for each
    /// process and never deletes it; thousands pile up over many runs. Once it is mapped the file
    /// can be unlinked (Linux keeps the mapping). Called after patching; no-op elsewhere.
    /// </summary>
    public static void RemoveMonoModTempFiles()
    {
        if (_tempFilesRemoved || !OperatingSystem.IsLinux()) return;
        try
        {
            foreach (var line in File.ReadLines("/proc/self/maps"))
            {
                var idx = line.IndexOf("/mm-exhelper.so", StringComparison.Ordinal);
                if (idx < 0) continue;
                var path = line[line.IndexOf('/')..].Replace(" (deleted)", "").Trim();
                if (File.Exists(path)) File.Delete(path);
                _tempFilesRemoved = true;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] Could not remove MonoMod temp file: {ex.Message}"); }
    }

    /// <summary>Record a mismatch when a patch touched a different number of sites than expected.</summary>
    public static void Expect(string patch, int actual, int expected)
    {
        if (actual != expected)
            Warn($"{patch}: patched {actual} site(s), expected {expected} (game update?)");
    }
}
