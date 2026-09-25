namespace Sts2Headless;

/// <summary>
/// The legal_actions contract: an action is executed only if it is one of the entries the adapter
/// last exported (or a valid fill of the select_cards template). Anything else is rejected before
/// any game state is touched.
/// </summary>
public partial class RunSimulator
{
    private List<Dictionary<string, object?>>? _lastLegal;
    private string? _lastDecision;

    /// <summary>Remembers what the client was just shown. Called from AttachLegalActions.</summary>
    private void RememberLegal(string decision, List<Dictionary<string, object?>> legal)
    {
        _lastDecision = decision;
        _lastLegal = legal;
    }

    /// <summary>Forget the cached legal set; the next action recomputes it from live state.</summary>
    internal void InvalidateLegal()
    {
        _lastLegal = null;
        _lastDecision = null;
    }

    /// <summary>Returns null when the action is legal, otherwise an error response.</summary>
    private Dictionary<string, object?>? ValidateAction(string action, Dictionary<string, object?>? args)
    {
        // Recompute when nothing was exported since the last state change (e.g. after a debug
        // command), or when the export offered nothing: the engine may have caught up since
        // (a combat decision exported just before the play phase started).
        if (_lastLegal == null || _lastLegal.Count == 0)
        {
            var current = DetectDecisionPoint();
            if (!string.Equals(current.GetValueOrDefault("type") as string, "decision", StringComparison.Ordinal))
                return Error($"No decision is pending ({current.GetValueOrDefault("type")})");
            RememberLegal(current.GetValueOrDefault("decision") as string ?? "", ComputeLegalActions(current));
        }

        var legal = _lastLegal!;
        var name = NormalizeActionName(action, legal);
        var candidates = legal.Where(a => string.Equals(a["action"] as string, name, StringComparison.Ordinal)).ToList();
        if (candidates.Count == 0)
            return Illegal(action, $"'{action}' is not available at {_lastDecision}");

        string? lastReason = null;
        foreach (var entry in candidates)
        {
            var reason = entry.GetValueOrDefault("template") is true
                ? CheckTemplate(entry, args)
                : CheckArgs(entry.GetValueOrDefault("args") as Dictionary<string, object?>, args);
            if (reason == null) return null;
            lastReason = reason;
        }
        return Illegal(action, candidates.Count == 1 ? lastReason! : $"arguments match none of the listed '{name}' actions");
    }

    private Dictionary<string, object?> Illegal(string action, string reason) => new()
    {
        ["type"] = "error",
        ["message"] = $"Illegal action '{action}': {reason}",
        ["decision"] = _lastDecision,
    };

    /// <summary>leave_room is the documented alias of proceed in manual flow; auto flow lists leave_room in shops.</summary>
    private static string NormalizeActionName(string action, List<Dictionary<string, object?>> legal)
    {
        bool Listed(string n) => legal.Any(a => string.Equals(a["action"] as string, n, StringComparison.Ordinal));
        if (action == "leave_room" && !Listed("leave_room") && Listed("proceed")) return "proceed";
        if (action == "proceed" && !Listed("proceed") && Listed("leave_room")) return "leave_room";
        return action;
    }

    /// <summary>
    /// Args must equal the listed entry's args. One tolerance: an entry without target_index
    /// (single alive enemy, or an untargeted card/potion) also accepts target_index 0, which is
    /// what clients send for "the only target".
    /// </summary>
    private static string? CheckArgs(Dictionary<string, object?>? listed, Dictionary<string, object?>? given)
    {
        listed ??= new();
        given ??= new();
        foreach (var (k, v) in given)
        {
            if (!listed.TryGetValue(k, out var expected))
            {
                if (k == "target_index" && TryInt(v, out var t) && t == 0) continue;
                return $"unexpected argument '{k}'";
            }
            if (!ArgEquals(expected, v))
                return $"'{k}' = {v} is not a listed value";
        }
        foreach (var k in listed.Keys)
            if (!given.ContainsKey(k))
                return $"missing argument '{k}'";
        return null;
    }

    private static bool ArgEquals(object? expected, object? given)
    {
        if (expected is int or long)
            return TryInt(given, out var g) && g == Convert.ToInt64(expected);
        if (expected is string es)
            return given is string gs && string.Equals(es, gs, StringComparison.OrdinalIgnoreCase);
        return Equals(expected, given);
    }

    private static bool TryInt(object? value, out long result)
    {
        switch (value)
        {
            case int i: result = i; return true;
            case long l: result = l; return true;
            case string s when long.TryParse(s.Trim(), out var p): result = p; return true;
            default: result = 0; return false;
        }
    }

    /// <summary>select_cards: distinct in-range indices, min_select ≤ count ≤ max_select.</summary>
    private static string? CheckTemplate(Dictionary<string, object?> entry, Dictionary<string, object?>? given)
    {
        if (given == null || !given.TryGetValue("indices", out var raw) || raw is not string text)
            return "select_cards requires 'indices' (comma-separated)";
        var extra = given.Keys.FirstOrDefault(k => k != "indices");
        if (extra != null) return $"unexpected argument '{extra}'";
        if (!TryParseIndices(text, out var indices))
            return $"indices '{text}' must be comma-separated integers";
        int n = Convert.ToInt32(entry.GetValueOrDefault("num_cards") ?? 0);
        int min = Convert.ToInt32(entry.GetValueOrDefault("min_select") ?? 0);
        int max = Convert.ToInt32(entry.GetValueOrDefault("max_select") ?? 0);
        if (indices.Distinct().Count() != indices.Count) return "duplicate indices";
        var bad = indices.FirstOrDefault(i => i < 0 || i >= n, -1);
        if (indices.Any(i => i < 0 || i >= n)) return $"index {bad} out of range 0..{n - 1}";
        if (indices.Count < min || indices.Count > max)
            return $"select {min}–{max} cards, got {indices.Count}";
        return null;
    }

    internal static bool TryParseIndices(string text, out List<int> indices)
    {
        indices = new List<int>();
        if (string.IsNullOrWhiteSpace(text)) return true;
        foreach (var tok in text.Split(','))
        {
            if (!int.TryParse(tok.Trim(), out var i)) return false;
            indices.Add(i);
        }
        return true;
    }
}
