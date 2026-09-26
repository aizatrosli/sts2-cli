// TestModeAudit — list every read of TestMode.IsOn / TestMode.IsOff in sts2.dll outside UI code.
//
// The headless engine runs with TestMode on. Most TestMode branches only skip visuals, but some
// change gameplay (CallingBell/Cauldron loot, shop price rolls — see src/Sts2Headless/TestModePatches.cs).
// Game updates add new branches silently, so every gameplay site is checked against a reviewed
// allowlist (tools/TestModeAudit/allowlist.txt, one "Namespace.Type::Method" per line; text after
// '#' is a note). A site that is not on the list must be reviewed: patch it in TestModePatches if it
// changes gameplay, otherwise add it to the allowlist.
//
// Usage:
//   dotnet run --project tools/TestModeAudit -- [--sts2 lib/sts2.dll] [--allowlist <path>] [--fail] [--write-allowlist]
//     --fail             exit 1 when a site is not on the allowlist
//     --write-allowlist  rewrite the allowlist with the current sites (keeps existing notes)

using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace TestModeAudit;

internal static class Program
{
    private static readonly string[] UiPrefixes =
    {
        "MegaCrit.Sts2.Core.Nodes", "MegaCrit.Sts2.addons", "MegaCrit.Sts2.Core.RichTextTags",
        "MegaCrit.Sts2.Core.ControllerInput", "MegaCrit.Sts2.Core.TestSupport", "MegaCrit.Sts2.Core.DevConsole",
        "MegaCrit.Sts2.Core.AutoSlay", "MegaCrit.Sts2.Core.Debug",
    };

    private static readonly Dictionary<short, OperandType> OperandTypes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value, o => o.OperandType);

    private static int Main(string[] args)
    {
        var root = FindRepoRoot();
        string sts2 = Arg(args, "--sts2") ?? Path.Combine(root, "lib", "sts2.dll");
        string allowPath = Arg(args, "--allowlist") ?? Path.Combine(root, "tools", "TestModeAudit", "allowlist.txt");
        bool fail = args.Contains("--fail");
        bool write = args.Contains("--write-allowlist");

        var sites = Scan(sts2);
        var allow = ReadAllowlist(allowPath);
        var unknown = sites.Where(s => !allow.ContainsKey(s)).ToList();
        var gone = allow.Keys.Where(k => !sites.Contains(k)).ToList();

        Console.WriteLine($"TestMode reads in gameplay code: {sites.Count} methods ({allow.Count} allowlisted)");
        foreach (var s in unknown) Console.WriteLine($"  NEW   {s}");
        foreach (var s in gone) Console.WriteLine($"  GONE  {s}");

        if (write)
        {
            var lines = new List<string>
            {
                "# Reviewed TestMode.IsOn/IsOff reads in gameplay code (see tools/TestModeAudit/Program.cs).",
                "# Gameplay-changing sites are patched in src/Sts2Headless/TestModePatches.cs and marked 'patched'.",
            };
            lines.AddRange(sites.OrderBy(s => s, StringComparer.Ordinal)
                .Select(s => allow.TryGetValue(s, out var note) && note.Length > 0 ? $"{s}  # {note}" : s));
            File.WriteAllLines(allowPath, lines);
            Console.WriteLine($"Wrote {sites.Count} entries to {allowPath}");
            return 0;
        }
        return fail && unknown.Count > 0 ? 1 : 0;
    }

    private static HashSet<string> Scan(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        var targets = new HashSet<int>();
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            if (md.GetString(t.Name) != "TestMode" || md.GetString(t.Namespace) != "MegaCrit.Sts2.Core.TestSupport") continue;
            foreach (var mh in t.GetMethods())
            {
                var name = md.GetString(md.GetMethodDefinition(mh).Name);
                if (name is "get_IsOn" or "get_IsOff") targets.Add(MetadataTokens.GetToken(mh));
            }
        }
        if (targets.Count == 0) throw new InvalidOperationException("TestMode.get_IsOn/get_IsOff not found in " + path);

        var sites = new HashSet<string>(StringComparer.Ordinal);
        foreach (var th in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(th);
            var (owner, ns) = OwnerType(md, type);
            if (UiPrefixes.Any(p => ns.StartsWith(p, StringComparison.Ordinal))) continue;
            foreach (var mh in type.GetMethods())
            {
                var method = md.GetMethodDefinition(mh);
                if (method.RelativeVirtualAddress == 0) continue;
                if (!ReadsTestMode(pe.GetMethodBody(method.RelativeVirtualAddress), targets)) continue;
                sites.Add($"{owner}::{MethodName(md, type, method)}");
            }
        }
        return sites;
    }

    private static bool ReadsTestMode(MethodBodyBlock body, HashSet<int> targets)
    {
        var il = body.GetILReader();
        while (il.RemainingBytes > 0)
        {
            short value = il.ReadByte();
            if (value == 0xFE) value = unchecked((short)(0xFE00 | il.ReadByte()));
            switch (OperandTypes[value])
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: il.Offset += 1; break;
                case OperandType.InlineVar: il.Offset += 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: il.Offset += 8; break;
                case OperandType.InlineSwitch:
                    int n = il.ReadInt32();
                    il.Offset += 4 * n;
                    break;
                case OperandType.InlineMethod:
                    if (targets.Contains(il.ReadInt32())) return true;
                    break;
                default: il.Offset += 4; break;
            }
        }
        return false;
    }

    /// <summary>Compiler-generated nested types (state machines, lambdas) are reported under their outer type.</summary>
    private static (string Owner, string Namespace) OwnerType(MetadataReader md, TypeDefinition type)
    {
        var names = new List<string>();
        var t = type;
        while (true)
        {
            var name = md.GetString(t.Name);
            if (!name.StartsWith('<')) names.Insert(0, name);
            var parent = t.GetDeclaringType();
            if (parent.IsNil) break;
            t = md.GetTypeDefinition(parent);
        }
        var ns = md.GetString(t.Namespace);
        return ($"{ns}.{string.Join("+", names)}", ns);
    }

    /// <summary>State-machine MoveNext is reported as the method that owns the state machine ("&lt;Foo&gt;d__3" → Foo).</summary>
    private static string MethodName(MetadataReader md, TypeDefinition type, MethodDefinition method)
    {
        var name = md.GetString(method.Name);
        var typeName = md.GetString(type.Name);
        if (typeName.StartsWith('<') && typeName.IndexOf('>') > 1)
            return typeName[1..typeName.IndexOf('>')];
        if (name.StartsWith('<') && name.IndexOf('>') > 1)
            return name[1..name.IndexOf('>')];
        return name;
    }

    private static Dictionary<string, string> ReadAllowlist(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        foreach (var raw in File.ReadAllLines(path))
        {
            var hash = raw.IndexOf('#');
            var entry = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (entry.Length == 0) continue;
            result[entry] = hash >= 0 ? raw[(hash + 1)..].Trim() : "";
        }
        return result;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
