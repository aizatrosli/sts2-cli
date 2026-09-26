// GodotStubAudit — list every GodotSharp type/member that sts2.dll references and
// report which ones the headless stub (src/GodotStubs → GodotSharp.dll) lacks.
//
// A missing member makes the JIT throw MissingMethodException / MissingFieldException
// the first time the calling method runs. In game logic that usually happens inside the
// combat turn loop ("Combat #N turn loop died"), so every reference is attributed to the
// namespaces of the sts2 code that uses it (IL operands, signatures, base types, custom
// attributes) and gaps are reported by tier:
//   gameplay      referenced from anything that is neither UI nor tooling (Models, Commands,
//                 Combat, Entities, Events, Rewards, Runs, Saves, Helpers, ...) — fix these
//   reachable-ui  referenced only from UI/tooling code, but from a UI method that gameplay code
//                 calls within --depth call edges (default 2: gameplay → UI → UI). It crashes
//                 the turn loop just like a gameplay gap — fix these too. Each one is printed
//                 with one shortest gameplay → UI call path.
//   tooling       referenced only from dev/debug/test code (DevConsole, AutoSlay, ...)
//   ui            referenced only from UI code that gameplay does not reach
//
// The call graph covers the game's own methods: call/callvirt/newobj/ldftn/ldvirtftn edges;
// a virtual or interface call also reaches every override/implementation in the game; a call
// into a type also reaches its static constructor; an async/iterator method reaches its state
// machine's MoveNext at no extra depth. A type-level use (base type, interface, field or
// property type, attribute) counts as a use by every method of that type.
//
// Usage:
//   dotnet run --project tools/GodotStubAudit -- [options]
//     --sts2 <path>        game assembly         (default: lib/sts2.dll)
//     --stub <path>        stub GodotSharp.dll   (default: src/Sts2Headless/bin/Debug/net9.0/GodotSharp.dll)
//     --ui-prefix <ns>     extra namespace prefix counted as UI (repeatable)
//     --tool-prefix <ns>   extra namespace prefix counted as tooling (repeatable)
//     --depth <n>          call edges from gameplay that make a UI reference reachable (default 2)
//     --all                also list tooling/UI gaps (default: gameplay and reachable-ui only)
//     --present            also list references that resolve
//     --json <path>        write the full report as JSON
//     --reference <path>   the real GodotSharp.dll (e.g. from the game directory): print
//                          the real declaration of each missing member, and check that
//                          every enum sts2 uses has the real underlying type and constant
//                          values (sts2 bakes enum constants into its IL, so a stub enum
//                          with different ordinals silently changes game logic, and an
//                          int-backed stub for a long-backed enum is invalid IL)
//     --fail               exit 1 when a gameplay or reachable-ui gap, or an enum mismatch, exists
//
// Matching mirrors the runtime's MemberRef resolution: the referenced type and then its
// base types are searched for a member with the same name, generic arity, parameter
// types and return type (custom modifiers are ignored). Constructors are only looked up
// on the referenced type itself.

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace GodotStubAudit;

internal static class Program
{
    private static readonly string[] DefaultUiPrefixes =
    {
        "MegaCrit.Sts2.Core.Nodes", "MegaCrit.Sts2.addons", "MegaCrit.Sts2.Core.RichTextTags",
        "MegaCrit.Sts2.Core.ControllerInput",
    };

    private static readonly string[] DefaultToolPrefixes =
    {
        "MegaCrit.Sts2.Core.Debug", "MegaCrit.Sts2.Core.DevConsole", "MegaCrit.Sts2.Core.AutoSlay",
        "MegaCrit.Sts2.Core.Audio.Debug", "MegaCrit.Sts2.Core.TestSupport", "RiderTestRunner", "GodotPlugins",
    };

    private static int Main(string[] args)
    {
        string repo = FindRepoRoot();
        string sts2Path = Path.Combine(repo, "lib", "sts2.dll");
        string stubPath = Path.Combine(repo, "src", "Sts2Headless", "bin", "Debug", "net9.0", "GodotSharp.dll");
        var uiPrefixes = new List<string>(DefaultUiPrefixes);
        var toolPrefixes = new List<string>(DefaultToolPrefixes);
        bool listAll = false, listPresent = false, fail = false;
        int depth = 2;
        string? jsonPath = null, referencePath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--sts2": sts2Path = args[++i]; break;
                case "--stub": stubPath = args[++i]; break;
                case "--ui-prefix": uiPrefixes.Add(args[++i]); break;
                case "--tool-prefix": toolPrefixes.Add(args[++i]); break;
                case "--depth": depth = int.Parse(args[++i]); break;
                case "--all": listAll = true; break;
                case "--present": listPresent = true; break;
                case "--json": jsonPath = args[++i]; break;
                case "--reference": referencePath = args[++i]; break;
                case "--fail": fail = true; break;
                case "-h" or "--help":
                    Console.WriteLine("usage: GodotStubAudit [--sts2 PATH] [--stub PATH] [--ui-prefix NS]... [--tool-prefix NS]... [--depth N] [--all] [--present] [--json PATH] [--reference PATH] [--fail]");
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown argument: {args[i]}");
                    return 2;
            }
        }
        foreach (var p in new[] { sts2Path, stubPath, referencePath }.OfType<string>())
        {
            if (!File.Exists(p)) { Console.Error.WriteLine($"not found: {p}"); return 2; }
        }

        var refs = ReferenceScanner.Scan(sts2Path, "GodotSharp");
        using var stub = new StubIndex(stubPath);
        using var real = referencePath != null ? new StubIndex(referencePath) : null;

        foreach (var t in refs.Types.Values) t.Present = stub.FindType(t.Name) != null;
        foreach (var m in refs.Members.Values) (m.Present, m.Note) = stub.Resolve(m);

        // The game spells its namespace root both "Sts2" and "sts2".
        static bool Under(string ns, string p) =>
            ns.Equals(p, StringComparison.OrdinalIgnoreCase) || ns.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase);
        Tier TierOf(string ns) =>
            uiPrefixes.Any(p => Under(ns, p)) ? Tier.Ui : toolPrefixes.Any(p => Under(ns, p)) ? Tier.Tooling : Tier.Gameplay;
        // A reference with no attributed use is counted as gameplay: we cannot rule it out.
        Tier TierOfNamespaces(Dictionary<string, int> origins) => origins.Count == 0 ? Tier.Gameplay : origins.Keys.Min(TierOf);

        var reach = refs.Reach(m => TierOf(refs.MethodNamespace(m)) == Tier.Gameplay, depth);
        // The UI method using a reference that gameplay reaches soonest, if any within depth.
        MethodDefinitionHandle? ReachedUser(HashSet<MethodDefinitionHandle> users) => users
            .Where(u => reach.ContainsKey(u) && TierOf(refs.MethodNamespace(u)) == Tier.Ui)
            .OrderBy(u => reach[u].Depth).ThenBy(refs.MethodDisplay).Select(u => (MethodDefinitionHandle?)u).FirstOrDefault();
        Tier TierOfRef(Dictionary<string, int> origins, HashSet<MethodDefinitionHandle> users)
        {
            var t = TierOfNamespaces(origins);
            return t != Tier.Gameplay && ReachedUser(users) != null ? Tier.ReachableUi : t;
        }
        List<string> PathTo(HashSet<MethodDefinitionHandle> users)
        {
            var path = new List<string>();
            for (var m = ReachedUser(users); m is { } h; m = reach[h].Pred.IsNil ? null : reach[h].Pred)
                path.Add(refs.MethodDisplay(h));
            path.Reverse();
            return path;
        }
        Tier TypeTier(TypeRefInfo t) => TierOfRef(t.Origins, t.Users);
        Tier MemberTier(MemberRefInfo m) => TierOfRef(m.Origins, m.Users);

        var missingTypes = refs.Types.Values.Where(t => !t.Present).OrderBy(t => t.Name).ToList();
        var missingMembers = refs.Members.Values.Where(m => !m.Present).OrderBy(m => m.Display).ToList();
        var tierOrder = new[] { Tier.Gameplay, Tier.ReachableUi, Tier.Tooling, Tier.Ui };

        Console.WriteLine($"sts2:  {sts2Path}");
        Console.WriteLine($"stub:  {stubPath}");
        Console.WriteLine();
        Console.WriteLine($"call depth: {depth} (gameplay → UI edges counted for reachable-ui)");
        Console.WriteLine();
        Console.WriteLine($"{"",-22}{"referenced",11}{"missing",9}{"gameplay",10}{"reachable-ui",14}{"tooling",9}{"ui",6}");
        foreach (var (label, total, tiers) in new[]
                 {
                     ("GodotSharp types", refs.Types.Count, missingTypes.Select(TypeTier).ToList()),
                     ("GodotSharp members", refs.Members.Count, missingMembers.Select(MemberTier).ToList()),
                 })
        {
            int N(Tier t) => tiers.Count(x => x == t);
            Console.WriteLine($"{label,-22}{total,11}{tiers.Count,9}{N(Tier.Gameplay),10}{N(Tier.ReachableUi),14}{N(Tier.Tooling),9}{N(Tier.Ui),6}");
        }

        string Origins(Dictionary<string, int> o) => o.Count == 0 ? "(no attributed use)" : string.Join(", ",
            o.OrderBy(kv => TierOf(kv.Key)).ThenByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
             .Take(5).Select(kv => $"{kv.Key}×{kv.Value}")) + (o.Count > 5 ? $", +{o.Count - 5} more" : "");

        void PrintPath(Tier tier, HashSet<MethodDefinitionHandle> users, string target)
        {
            if (tier == Tier.ReachableUi) Console.WriteLine($"      path: {string.Join(" → ", PathTo(users).Append(target))}");
        }
        void PrintTypes(Tier tier)
        {
            var list = missingTypes.Where(t => TypeTier(t) == tier).ToList();
            Console.WriteLine();
            Console.WriteLine($"== Missing types, {Label(tier)} ({list.Count}) ==");
            foreach (var t in list)
            {
                Console.WriteLine($"  {t.Name}\n      from: {Origins(t.Origins)}");
                PrintPath(tier, t.Users, t.Name);
            }
        }
        void PrintMembers(string title, IEnumerable<MemberRefInfo> members, Tier? tier = null)
        {
            var list = members.ToList();
            Console.WriteLine();
            Console.WriteLine($"== {title} ({list.Count}) ==");
            foreach (var m in list)
            {
                Console.WriteLine($"  {m.Display}");
                if (!string.IsNullOrEmpty(m.Note)) Console.WriteLine($"      stub: {m.Note}");
                if (real?.Find(m).match is { } rm) Console.WriteLine($"      real: {StubIndex.Describe(rm)}");
                Console.WriteLine($"      from: {Origins(m.Origins)}");
                if (tier is { } t) PrintPath(t, m.Users, $"{m.TypeName}::{m.Name}");
            }
        }

        foreach (var tier in listAll ? tierOrder : new[] { Tier.Gameplay, Tier.ReachableUi })
        {
            PrintTypes(tier);
            PrintMembers($"Missing members, {Label(tier)}", missingMembers.Where(m => MemberTier(m) == tier), tier);
        }
        var enumIssues = new List<string>();
        if (real != null)
        {
            foreach (var t in refs.Types.Values.Where(t => t.Present).OrderBy(t => t.Name))
            {
                var want = real.EnumValues(t.Name);
                if (want == null) continue;
                var have = stub.EnumValues(t.Name);
                if (have == null) { enumIssues.Add($"  {t.Name}: not an enum in the stub"); continue; }
                var diffs = new List<string>();
                if (real.EnumUnderlyingType(t.Name) is { } wantU && stub.EnumUnderlyingType(t.Name) is { } haveU && wantU != haveU)
                    diffs.Add($"underlying {haveU} (real {wantU})");
                diffs.AddRange(want.Where(kv => !have.TryGetValue(kv.Key, out var v) || v != kv.Value)
                    .Select(kv => have.TryGetValue(kv.Key, out var v) ? $"{kv.Key}={v} (real {kv.Value})" : $"{kv.Key} missing (real {kv.Value})")
                    .Concat(have.Keys.Except(want.Keys).Select(k => $"{k}={have[k]} (not in real)")));
                if (diffs.Count > 0)
                    enumIssues.Add($"  {t.Name} [{Label(TypeTier(t))}]: {string.Join(", ", diffs.Take(8))}{(diffs.Count > 8 ? $", +{diffs.Count - 8} more" : "")}");
            }
            Console.WriteLine();
            Console.WriteLine($"== Enums whose stub values differ from the real assembly ({enumIssues.Count}) ==");
            foreach (var line in enumIssues) Console.WriteLine(line);
        }

        if (listPresent) PrintMembers("Resolved members", refs.Members.Values.Where(m => m.Present).OrderBy(m => m.Display));

        if (jsonPath != null)
        {
            var report = new
            {
                sts2 = sts2Path,
                stub = stubPath,
                uiPrefixes,
                toolPrefixes,
                depth,
                types = refs.Types.Values.OrderBy(t => t.Name).Select(t => new
                {
                    name = t.Name, present = t.Present, tier = Label(TypeTier(t)), origins = t.Origins,
                    path = TypeTier(t) == Tier.ReachableUi ? PathTo(t.Users) : null,
                }),
                members = refs.Members.Values.OrderBy(m => m.Display).Select(m => new
                {
                    type = m.TypeName, kind = m.Kind, name = m.Name, signature = m.Display,
                    present = m.Present, note = m.Note, tier = Label(MemberTier(m)), origins = m.Origins,
                    path = MemberTier(m) == Tier.ReachableUi ? PathTo(m.Users) : null,
                }),
            };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine();
            Console.WriteLine($"JSON report: {jsonPath}");
        }

        // A reachable-ui gap throws MissingMethodException in the turn loop just like a gameplay one.
        bool gap = missingTypes.Any(t => TypeTier(t) is Tier.Gameplay or Tier.ReachableUi)
                   || missingMembers.Any(m => MemberTier(m) is Tier.Gameplay or Tier.ReachableUi);
        return fail && (gap || enumIssues.Count > 0) ? 1 : 0;
    }

    // Declared in severity order: a reference's tier is the most severe of its uses.
    private enum Tier { Gameplay, ReachableUi, Tooling, Ui }

    private static string Label(Tier t) => t == Tier.ReachableUi ? "reachable-ui" : t.ToString().ToLowerInvariant();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "GodotStubs"))) return dir.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}

internal sealed class TypeRefInfo
{
    public required string Name;
    public readonly Dictionary<string, int> Origins = new();
    public readonly HashSet<MethodDefinitionHandle> Users = new();
    public bool Present;
}

internal sealed class MemberRefInfo
{
    public required string TypeName;          // "Godot.Collections.Array`1"
    public required string Name;
    public required string Kind;              // "method" | "field"
    public int GenericArity;
    public bool IsInstance;
    public ImmutableArray<string> Parameters = ImmutableArray<string>.Empty;
    public required string ReturnType;        // field type for fields
    public readonly Dictionary<string, int> Origins = new();
    public readonly HashSet<MethodDefinitionHandle> Users = new();
    public bool Present;
    public string? Note;

    public string Display => Kind == "field"
        ? $"{TypeName}::{Name} : {ReturnType}"
        : $"{(IsInstance ? "" : "static ")}{TypeName}::{Name}{(GenericArity > 0 ? $"<{GenericArity}>" : "")}({string.Join(", ", Parameters)}) : {ReturnType}";
    public string Key => $"{Kind}|{Display}";
}

/// <summary>Collects Godot type/member references from the game assembly and attributes
/// each one to the namespaces and methods of the code that uses it. Also builds the game's
/// call graph (see <see cref="Reach"/>).</summary>
internal sealed class ReferenceScanner
{
    public readonly Dictionary<string, TypeRefInfo> Types = new();
    public readonly Dictionary<string, MemberRefInfo> Members = new();

    private readonly MetadataReader _md;
    private readonly HashSet<AssemblyReferenceHandle> _godotAsm = new();
    private readonly Dictionary<TypeReferenceHandle, string> _godotTypeRefs = new();
    private readonly Dictionary<MemberReferenceHandle, MemberRefInfo> _godotMemberRefs = new();
    private readonly SignatureNames _names;
    // Methods a reference found right now is attributed to: the method whose signature/body is
    // being read, or every method of the type for type-level uses (base type, interfaces, field
    // and property types, type attributes), since calling any of them loads the type.
    private IReadOnlyList<MethodDefinitionHandle> _users = Array.Empty<MethodDefinitionHandle>();
    // Call graph over the game's own methods. Weight 1 = a call edge; weight 0 = an async or
    // iterator method to its state machine's MoveNext (the same logical method).
    private readonly Dictionary<MethodDefinitionHandle, List<(MethodDefinitionHandle To, int Weight)>> _edges = new();
    private readonly List<(MethodDefinitionHandle Caller, MethodDefinitionHandle Callee, bool Virtual)> _calls = new();
    private static readonly Dictionary<short, OperandType> OperandTypes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value, o => o.OperandType);

    private ReferenceScanner(MetadataReader md)
    {
        _md = md;
        _names = new SignatureNames(md);
    }

    public static ReferenceScanner Scan(string path, string assemblyName)
    {
        // The image stays in managed memory: the call graph and names are read after Scan returns.
        var pe = new PEReader(ImmutableArray.Create(File.ReadAllBytes(path)));
        var s = new ReferenceScanner(pe.GetMetadataReader());
        s.Collect(assemblyName);
        s.Attribute(pe);
        s.BuildCallGraph();
        return s;
    }

    private void Collect(string assemblyName)
    {
        foreach (var h in _md.AssemblyReferences)
        {
            if (_md.GetString(_md.GetAssemblyReference(h).Name) == assemblyName) _godotAsm.Add(h);
        }
        foreach (var h in _md.TypeReferences)
        {
            if (!IsGodot(h)) continue;
            string name = _names.TypeRefName(h);
            _godotTypeRefs[h] = name;
            if (!Types.ContainsKey(name)) Types[name] = new TypeRefInfo { Name = name };
        }
        foreach (var h in _md.MemberReferences)
        {
            var mr = _md.GetMemberReference(h);
            string? typeName = ParentGodotType(mr.Parent);
            if (typeName == null) continue;
            string name = _md.GetString(mr.Name);
            MemberRefInfo info;
            if (mr.GetKind() == MemberReferenceKind.Method)
            {
                var sig = mr.DecodeMethodSignature(_names, null);
                info = new MemberRefInfo
                {
                    TypeName = typeName, Name = name, Kind = "method",
                    GenericArity = sig.GenericParameterCount, IsInstance = sig.Header.IsInstance,
                    Parameters = sig.ParameterTypes, ReturnType = sig.ReturnType,
                };
            }
            else
            {
                info = new MemberRefInfo
                {
                    TypeName = typeName, Name = name, Kind = "field", ReturnType = mr.DecodeFieldSignature(_names, null),
                };
            }
            if (Members.TryGetValue(info.Key, out var existing)) info = existing;
            else Members[info.Key] = info;
            _godotMemberRefs[h] = info;
        }
    }

    private bool IsGodot(TypeReferenceHandle h)
    {
        var scope = _md.GetTypeReference(h).ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference) scope = _md.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        return scope.Kind == HandleKind.AssemblyReference && _godotAsm.Contains((AssemblyReferenceHandle)scope);
    }

    private string? ParentGodotType(EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
                return _godotTypeRefs.GetValueOrDefault((TypeReferenceHandle)parent);
            case HandleKind.TypeSpecification:
                var root = GenericRoot((TypeSpecificationHandle)parent);
                return root is { } r ? _godotTypeRefs.GetValueOrDefault(r) : null;
            default:
                return null;
        }
    }

    /// <summary>The TypeRef at the head of a GENERICINST TypeSpec blob, if any.</summary>
    private TypeReferenceHandle? GenericRoot(TypeSpecificationHandle h)
    {
        var blob = _md.GetBlobReader(_md.GetTypeSpecification(h).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
        blob.ReadSignatureTypeCode(); // CLASS / VALUETYPE
        var t = blob.ReadTypeHandle();
        return t.Kind == HandleKind.TypeReference ? (TypeReferenceHandle)t : null;
    }

    // ── attribution ──────────────────────────────────────────────────────────

    private void Attribute(PEReader pe)
    {
        var collector = new TypeRefCollector(this);
        foreach (var th in _md.TypeDefinitions)
        {
            var td = _md.GetTypeDefinition(th);
            string ns = OuterNamespace(th);
            collector.Namespace = ns;
            var typeMethods = td.GetMethods().ToList();
            _users = typeMethods;
            if (!td.BaseType.IsNil) NoteEntity(td.BaseType, ns, collector);
            foreach (var ih in td.GetInterfaceImplementations()) NoteEntity(_md.GetInterfaceImplementation(ih).Interface, ns, collector);
            foreach (var fh in td.GetFields()) _md.GetFieldDefinition(fh).DecodeSignature(collector, null);
            foreach (var ph in td.GetProperties()) _md.GetPropertyDefinition(ph).DecodeSignature(collector, null);
            foreach (var mh in typeMethods)
            {
                _users = new[] { mh };
                var m = _md.GetMethodDefinition(mh);
                m.DecodeSignature(collector, null);
                if (m.RelativeVirtualAddress != 0) ScanBody(pe.GetMethodBody(m.RelativeVirtualAddress), mh, ns, collector);
            }
        }
        foreach (var ch in _md.CustomAttributes)
        {
            var ca = _md.GetCustomAttribute(ch);
            string? ns = OwnerNamespace(ca.Parent);
            if (ns == null) continue;
            collector.Namespace = ns;
            _users = OwnerMethods(ca.Parent);
            NoteEntity(ca.Constructor, ns, collector);
        }
        _users = Array.Empty<MethodDefinitionHandle>();
    }

    private IReadOnlyList<MethodDefinitionHandle> OwnerMethods(EntityHandle h) => h.Kind switch
    {
        HandleKind.MethodDefinition => new[] { (MethodDefinitionHandle)h },
        HandleKind.TypeDefinition => _md.GetTypeDefinition((TypeDefinitionHandle)h).GetMethods().ToList(),
        HandleKind.FieldDefinition => _md.GetTypeDefinition(_md.GetFieldDefinition((FieldDefinitionHandle)h).GetDeclaringType()).GetMethods().ToList(),
        HandleKind.PropertyDefinition or HandleKind.EventDefinition when AccessorType(h) is { } t => _md.GetTypeDefinition(t).GetMethods().ToList(),
        _ => Array.Empty<MethodDefinitionHandle>(),
    };

    private TypeDefinitionHandle? AccessorType(EntityHandle h)
    {
        MethodDefinitionHandle m = default;
        if (h.Kind == HandleKind.PropertyDefinition)
        {
            var a = _md.GetPropertyDefinition((PropertyDefinitionHandle)h).GetAccessors();
            m = a.Getter.IsNil ? a.Setter : a.Getter;
        }
        else if (h.Kind == HandleKind.EventDefinition)
        {
            m = _md.GetEventDefinition((EventDefinitionHandle)h).GetAccessors().Adder;
        }
        return m.IsNil ? null : _md.GetMethodDefinition(m).GetDeclaringType();
    }

    private void ScanBody(MethodBodyBlock body, MethodDefinitionHandle method, string ns, TypeRefCollector collector)
    {
        if (!body.LocalSignature.IsNil)
        {
            var sig = _md.GetStandaloneSignature(body.LocalSignature);
            if (sig.GetKind() == StandaloneSignatureKind.LocalVariables) sig.DecodeLocalSignature(collector, null);
        }
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
                    int targets = il.ReadInt32(); // read before touching Offset: `Offset += 4 * ReadInt32()` loses the count bytes
                    il.Offset += 4 * targets;
                    break;
                case OperandType.InlineMethod:
                    var target = MetadataTokens.EntityHandle(il.ReadInt32());
                    NoteEntity(target, ns, collector);
                    if (ResolveMethod(target) is { } callee)
                        _calls.Add((method, callee, value is 0x6F /* callvirt */ or unchecked((short)0xFE07) /* ldvirtftn */));
                    break;
                case OperandType.InlineField:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                    NoteEntity(MetadataTokens.EntityHandle(il.ReadInt32()), ns, collector);
                    break;
                default: il.Offset += 4; break;
            }
        }
    }

    private void NoteEntity(EntityHandle h, string ns, TypeRefCollector collector)
    {
        switch (h.Kind)
        {
            case HandleKind.MemberReference:
                var mrh = (MemberReferenceHandle)h;
                if (_godotMemberRefs.TryGetValue(mrh, out var info))
                {
                    Bump(info.Origins, ns);
                    info.Users.UnionWith(_users);
                }
                var mr = _md.GetMemberReference(mrh);
                NoteEntity(mr.Parent, ns, collector);
                if (mr.GetKind() == MemberReferenceKind.Method) mr.DecodeMethodSignature(collector, null);
                else mr.DecodeFieldSignature(collector, null);
                break;
            case HandleKind.MethodSpecification:
                var ms = _md.GetMethodSpecification((MethodSpecificationHandle)h);
                NoteEntity(ms.Method, ns, collector);
                ms.DecodeSignature(collector, null);
                break;
            case HandleKind.TypeReference:
                NoteType((TypeReferenceHandle)h, ns);
                break;
            case HandleKind.TypeSpecification:
                _md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(collector, null);
                break;
        }
    }

    // ── call graph ───────────────────────────────────────────────────────────

    /// <summary>The game method a call operand binds to: a MethodDef, a MethodSpec of one, or a
    /// MemberRef on an instantiation of one of the game's generic types.</summary>
    private MethodDefinitionHandle? ResolveMethod(EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.MethodDefinition:
                return (MethodDefinitionHandle)h;
            case HandleKind.MethodSpecification:
                return ResolveMethod(_md.GetMethodSpecification((MethodSpecificationHandle)h).Method);
            case HandleKind.MemberReference:
                var mr = _md.GetMemberReference((MemberReferenceHandle)h);
                if (mr.GetKind() != MemberReferenceKind.Method) return null;
                if (mr.Parent.Kind == HandleKind.MethodDefinition) return (MethodDefinitionHandle)mr.Parent; // vararg call site
                if (TypeDefOf(mr.Parent) is not { } owner) return null;
                string name = _md.GetString(mr.Name);
                var sig = mr.DecodeMethodSignature(_names, null);
                foreach (var mh in _md.GetTypeDefinition(owner).GetMethods())
                {
                    var m = _md.GetMethodDefinition(mh);
                    if (_md.GetString(m.Name) != name) continue;
                    var ms = m.DecodeSignature(_names, null);
                    if (ms.GenericParameterCount == sig.GenericParameterCount && ms.ReturnType == sig.ReturnType
                        && ms.ParameterTypes.SequenceEqual(sig.ParameterTypes)) return mh;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>The game TypeDef behind a TypeDef handle or a generic instantiation of one.</summary>
    private TypeDefinitionHandle? TypeDefOf(EntityHandle h)
    {
        if (h.Kind == HandleKind.TypeDefinition) return (TypeDefinitionHandle)h;
        if (h.Kind != HandleKind.TypeSpecification) return null;
        var blob = _md.GetBlobReader(_md.GetTypeSpecification((TypeSpecificationHandle)h).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
        blob.ReadSignatureTypeCode(); // CLASS / VALUETYPE
        var t = blob.ReadTypeHandle();
        return t.Kind == HandleKind.TypeDefinition ? (TypeDefinitionHandle)t : null;
    }

    private void AddEdge(MethodDefinitionHandle from, MethodDefinitionHandle to, int weight)
    {
        if (!_edges.TryGetValue(from, out var list)) _edges[from] = list = new();
        list.Add((to, weight));
    }

    /// <summary>Turns the recorded call sites into edges. Over-approximates where dispatch is
    /// dynamic, which is the safe direction for a reachability check:
    /// a virtual call may land in any override or implementation in the game; calling into a
    /// type may run its static constructor; an async/iterator method runs its body in the
    /// state machine's MoveNext.</summary>
    private void BuildCallGraph()
    {
        var implementors = new Dictionary<MethodDefinitionHandle, List<MethodDefinitionHandle>>();
        void Impl(MethodDefinitionHandle decl, MethodDefinitionHandle impl)
        {
            if (decl == impl) return;
            if (!implementors.TryGetValue(decl, out var l)) implementors[decl] = l = new();
            l.Add(impl);
        }
        int ParamCount(MethodDefinition m) => m.GetParameters().Count(p => _md.GetParameter(p).SequenceNumber > 0);

        foreach (var th in _md.TypeDefinitions)
        {
            var td = _md.GetTypeDefinition(th);
            foreach (var mh in td.GetMethods())
            {
                var m = _md.GetMethodDefinition(mh);
                const MethodAttributes virtualReuse = MethodAttributes.Virtual;
                if ((m.Attributes & (MethodAttributes.Virtual | MethodAttributes.NewSlot)) != virtualReuse) continue;
                // An override: the nearest game base type declaring a same-name, same-arity method.
                // Parameter types are not compared (a generic base declares them as !0).
                string name = _md.GetString(m.Name);
                int count = ParamCount(m);
                for (var b = TypeDefOf(td.BaseType); b is { } bh; b = TypeDefOf(_md.GetTypeDefinition(bh).BaseType))
                {
                    var match = _md.GetTypeDefinition(bh).GetMethods().Where(x =>
                    {
                        var bm = _md.GetMethodDefinition(x);
                        return (bm.Attributes & MethodAttributes.Virtual) != 0 && _md.GetString(bm.Name) == name && ParamCount(bm) == count;
                    }).ToList();
                    if (match.Count == 0) continue;
                    foreach (var x in match) Impl(x, mh);
                    break;
                }
            }
            // Interfaces declared in the game: implicit implementations by name and arity...
            foreach (var ih in td.GetInterfaceImplementations())
            {
                if (TypeDefOf(_md.GetInterfaceImplementation(ih).Interface) is not { } iface) continue;
                foreach (var imh in _md.GetTypeDefinition(iface).GetMethods())
                {
                    var im = _md.GetMethodDefinition(imh);
                    string name = _md.GetString(im.Name);
                    int count = ParamCount(im);
                    foreach (var mh in td.GetMethods())
                    {
                        var m = _md.GetMethodDefinition(mh);
                        if (_md.GetString(m.Name) == name && ParamCount(m) == count) Impl(imh, mh);
                    }
                }
            }
            // ...and explicit ones (MethodImpl rows, which also cover explicit overrides).
            foreach (var mih in td.GetMethodImplementations())
            {
                var mi = _md.GetMethodImplementation(mih);
                if (ResolveMethod(mi.MethodDeclaration) is { } decl && ResolveMethod(mi.MethodBody) is { } body) Impl(decl, body);
            }
            // Async and iterator state machines: <Name>d__N nested in the method's declaring type.
            string tname = _md.GetString(td.Name);
            if (td.IsNested && tname.StartsWith('<') && tname.LastIndexOf(">d", StringComparison.Ordinal) is var end and > 1
                && (end + 2 == tname.Length || tname.AsSpan(end + 2).StartsWith("__")))
            {
                string owner = tname.Substring(1, end - 1);
                var moveNext = td.GetMethods().FirstOrDefault(x => _md.GetString(_md.GetMethodDefinition(x).Name) == "MoveNext");
                if (!moveNext.IsNil)
                {
                    foreach (var mh in _md.GetTypeDefinition(td.GetDeclaringType()).GetMethods())
                        if (_md.GetString(_md.GetMethodDefinition(mh).Name) == owner) AddEdge(mh, moveNext, 0);
                }
            }
        }

        // Transitive closure: a call through a base method can land in an override of an override.
        var closure = new Dictionary<MethodDefinitionHandle, HashSet<MethodDefinitionHandle>>();
        HashSet<MethodDefinitionHandle> Overrides(MethodDefinitionHandle m)
        {
            if (closure.TryGetValue(m, out var set)) return set;
            closure[m] = set = new();
            if (implementors.TryGetValue(m, out var direct))
                foreach (var d in direct)
                    if (set.Add(d)) set.UnionWith(Overrides(d));
            return set;
        }

        var cctors = new Dictionary<TypeDefinitionHandle, MethodDefinitionHandle>();
        foreach (var th in _md.TypeDefinitions)
        {
            var c = _md.GetTypeDefinition(th).GetMethods().FirstOrDefault(x => _md.GetString(_md.GetMethodDefinition(x).Name) == ".cctor");
            if (!c.IsNil) cctors[th] = c;
        }

        foreach (var (caller, callee, isVirtual) in _calls)
        {
            AddEdge(caller, callee, 1);
            if (isVirtual) foreach (var o in Overrides(callee)) AddEdge(caller, o, 1);
            var owner = _md.GetMethodDefinition(callee).GetDeclaringType();
            if (cctors.TryGetValue(owner, out var cctor) && cctor != caller) AddEdge(caller, cctor, 1);
        }
    }

    /// <summary>Shortest call distance (edge weights 0/1) of every method within
    /// <paramref name="maxDepth"/> of any root, with the predecessor on one shortest path.</summary>
    public Dictionary<MethodDefinitionHandle, (int Depth, MethodDefinitionHandle Pred)> Reach(Func<MethodDefinitionHandle, bool> isRoot, int maxDepth)
    {
        var dist = new Dictionary<MethodDefinitionHandle, (int Depth, MethodDefinitionHandle Pred)>();
        var queue = new LinkedList<MethodDefinitionHandle>();
        foreach (var th in _md.TypeDefinitions)
        {
            foreach (var mh in _md.GetTypeDefinition(th).GetMethods())
            {
                if (!isRoot(mh)) continue;
                dist[mh] = (0, default);
                queue.AddLast(mh);
            }
        }
        while (queue.First is { } node) // 0-1 BFS: weight-0 edges go to the front
        {
            queue.RemoveFirst();
            var m = node.Value;
            int d = dist[m].Depth;
            if (!_edges.TryGetValue(m, out var outs)) continue;
            foreach (var (to, w) in outs)
            {
                int nd = d + w;
                if (nd > maxDepth || (dist.TryGetValue(to, out var cur) && cur.Depth <= nd)) continue;
                dist[to] = (nd, m);
                if (w == 0) queue.AddFirst(to); else queue.AddLast(to);
            }
        }
        return dist;
    }

    public string MethodNamespace(MethodDefinitionHandle h) => OuterNamespace(_md.GetMethodDefinition(h).GetDeclaringType());

    public string MethodDisplay(MethodDefinitionHandle h)
    {
        var m = _md.GetMethodDefinition(h);
        string type = _names.GetTypeFromDefinition(_md, m.GetDeclaringType(), 0);
        foreach (var prefix in new[] { "MegaCrit.Sts2.Core.", "MegaCrit.sts2.Core.", "MegaCrit.Sts2." })
            if (type.StartsWith(prefix, StringComparison.Ordinal)) { type = type[prefix.Length..]; break; }
        return $"{type}::{_md.GetString(m.Name)}";
    }

    internal void NoteType(TypeReferenceHandle h, string ns)
    {
        if (!_godotTypeRefs.TryGetValue(h, out var name)) return;
        Bump(Types[name].Origins, ns);
        Types[name].Users.UnionWith(_users);
    }

    private static void Bump(Dictionary<string, int> d, string key) => d[key] = d.GetValueOrDefault(key) + 1;

    private string OuterNamespace(TypeDefinitionHandle h)
    {
        var td = _md.GetTypeDefinition(h);
        while (td.IsNested) td = _md.GetTypeDefinition(td.GetDeclaringType());
        string ns = _md.GetString(td.Namespace);
        return ns.Length == 0 ? "<global>" : ns;
    }

    private string? OwnerNamespace(EntityHandle h) => h.Kind switch
    {
        HandleKind.TypeDefinition => OuterNamespace((TypeDefinitionHandle)h),
        HandleKind.MethodDefinition => OuterNamespace(_md.GetMethodDefinition((MethodDefinitionHandle)h).GetDeclaringType()),
        HandleKind.FieldDefinition => OuterNamespace(_md.GetFieldDefinition((FieldDefinitionHandle)h).GetDeclaringType()),
        HandleKind.PropertyDefinition => PropertyNamespace((PropertyDefinitionHandle)h),
        HandleKind.EventDefinition => EventNamespace((EventDefinitionHandle)h),
        HandleKind.Parameter => null,
        HandleKind.AssemblyDefinition or HandleKind.ModuleDefinition => "<assembly>",
        _ => null,
    };

    private string? PropertyNamespace(PropertyDefinitionHandle h)
    {
        var a = _md.GetPropertyDefinition(h).GetAccessors();
        var m = a.Getter.IsNil ? a.Setter : a.Getter;
        return m.IsNil ? null : OuterNamespace(_md.GetMethodDefinition(m).GetDeclaringType());
    }

    private string? EventNamespace(EventDefinitionHandle h)
    {
        var a = _md.GetEventDefinition(h).GetAccessors();
        return a.Adder.IsNil ? null : OuterNamespace(_md.GetMethodDefinition(a.Adder).GetDeclaringType());
    }

    /// <summary>Signature visitor that records every Godot TypeRef it meets, attributed to
    /// <see cref="Namespace"/> (the provider callbacks for TypeRefs get no generic context).</summary>
    private sealed class TypeRefCollector : ISignatureTypeProvider<int, object?>
    {
        private readonly ReferenceScanner _s;
        public string Namespace = "";
        public TypeRefCollector(ReferenceScanner s) => _s = s;
        public int GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) { _s.NoteType(h, Namespace); return 0; }
        public int GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => 0;
        public int GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) =>
            r.GetTypeSpecification(h).DecodeSignature(this, c);
        public int GetSZArrayType(int e) => 0;
        public int GetArrayType(int e, ArrayShape s) => 0;
        public int GetByReferenceType(int e) => 0;
        public int GetPointerType(int e) => 0;
        public int GetPinnedType(int e) => 0;
        public int GetGenericInstantiation(int g, ImmutableArray<int> a) => 0;
        public int GetGenericMethodParameter(object? c, int i) => 0;
        public int GetGenericTypeParameter(object? c, int i) => 0;
        public int GetFunctionPointerType(MethodSignature<int> s) => 0;
        public int GetModifiedType(int m, int u, bool req) => 0;
        public int GetPrimitiveType(PrimitiveTypeCode c) => 0;
    }
}
