// GodotStubAudit — list every GodotSharp type/member that sts2.dll references and
// report which ones the headless stub (src/GodotStubs → GodotSharp.dll) lacks.
//
// A missing member makes the JIT throw MissingMethodException / MissingFieldException
// the first time the calling method runs. In game logic that usually happens inside the
// combat turn loop ("Combat #N turn loop died"), so every reference is attributed to the
// namespaces of the sts2 code that uses it (IL operands, signatures, base types, custom
// attributes) and gaps are reported by tier:
//   gameplay  referenced from anything that is neither UI nor tooling (Models, Commands,
//             Combat, Entities, Events, Rewards, Runs, Saves, Helpers, ...) — fix these
//   tooling   referenced only from dev/debug/test code (DevConsole, AutoSlay, ...)
//   ui        referenced only from UI code (Nodes.*, addons, rich-text effects, ...)
//
// Usage:
//   dotnet run --project tools/GodotStubAudit -- [options]
//     --sts2 <path>        game assembly         (default: lib/sts2.dll)
//     --stub <path>        stub GodotSharp.dll   (default: src/Sts2Headless/bin/Debug/net9.0/GodotSharp.dll)
//     --ui-prefix <ns>     extra namespace prefix counted as UI (repeatable)
//     --tool-prefix <ns>   extra namespace prefix counted as tooling (repeatable)
//     --all                also list tooling/UI gaps (default: counts only)
//     --present            also list references that resolve
//     --json <path>        write the full report as JSON
//     --reference <path>   the real GodotSharp.dll (e.g. from the game directory): print
//                          the real declaration of each missing member, and check that
//                          every enum sts2 uses has the real underlying type and constant
//                          values (sts2 bakes enum constants into its IL, so a stub enum
//                          with different ordinals silently changes game logic, and an
//                          int-backed stub for a long-backed enum is invalid IL)
//     --fail               exit 1 when a gameplay gap or enum mismatch exists
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
        string? jsonPath = null, referencePath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--sts2": sts2Path = args[++i]; break;
                case "--stub": stubPath = args[++i]; break;
                case "--ui-prefix": uiPrefixes.Add(args[++i]); break;
                case "--tool-prefix": toolPrefixes.Add(args[++i]); break;
                case "--all": listAll = true; break;
                case "--present": listPresent = true; break;
                case "--json": jsonPath = args[++i]; break;
                case "--reference": referencePath = args[++i]; break;
                case "--fail": fail = true; break;
                case "-h" or "--help":
                    Console.WriteLine("usage: GodotStubAudit [--sts2 PATH] [--stub PATH] [--ui-prefix NS]... [--tool-prefix NS]... [--all] [--present] [--json PATH] [--reference PATH] [--fail]");
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
        Tier TierOfAll(Dictionary<string, int> origins) => origins.Count == 0 ? Tier.Gameplay : origins.Keys.Min(TierOf);

        var missingTypes = refs.Types.Values.Where(t => !t.Present).OrderBy(t => t.Name).ToList();
        var missingMembers = refs.Members.Values.Where(m => !m.Present).OrderBy(m => m.Display).ToList();
        int Count<T>(IEnumerable<T> xs, Func<T, Dictionary<string, int>> o, Tier tier) => xs.Count(x => TierOfAll(o(x)) == tier);

        Console.WriteLine($"sts2:  {sts2Path}");
        Console.WriteLine($"stub:  {stubPath}");
        Console.WriteLine();
        Console.WriteLine($"{"",-22}{"referenced",11}{"missing",9}{"gameplay",10}{"tooling",9}{"ui",6}");
        foreach (var (label, total, missing, orig) in new[]
                 {
                     ("GodotSharp types", refs.Types.Count, missingTypes.Cast<object>().ToList(), (Func<object, Dictionary<string, int>>)(x => ((TypeRefInfo)x).Origins)),
                     ("GodotSharp members", refs.Members.Count, missingMembers.Cast<object>().ToList(), x => ((MemberRefInfo)x).Origins),
                 })
        {
            Console.WriteLine($"{label,-22}{total,11}{missing.Count,9}{Count(missing, orig, Tier.Gameplay),10}{Count(missing, orig, Tier.Tooling),9}{Count(missing, orig, Tier.Ui),6}");
        }

        string Origins(Dictionary<string, int> o) => o.Count == 0 ? "(no attributed use)" : string.Join(", ",
            o.OrderBy(kv => TierOf(kv.Key)).ThenByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
             .Take(5).Select(kv => $"{kv.Key}×{kv.Value}")) + (o.Count > 5 ? $", +{o.Count - 5} more" : "");

        void PrintTypes(Tier tier)
        {
            var list = missingTypes.Where(t => TierOfAll(t.Origins) == tier).ToList();
            Console.WriteLine();
            Console.WriteLine($"== Missing types, {tier.ToString().ToLowerInvariant()} ({list.Count}) ==");
            foreach (var t in list) Console.WriteLine($"  {t.Name}\n      from: {Origins(t.Origins)}");
        }
        void PrintMembers(string title, IEnumerable<MemberRefInfo> members)
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
            }
        }

        foreach (var tier in listAll ? new[] { Tier.Gameplay, Tier.Tooling, Tier.Ui } : new[] { Tier.Gameplay })
        {
            PrintTypes(tier);
            PrintMembers($"Missing members, {tier.ToString().ToLowerInvariant()}", missingMembers.Where(m => TierOfAll(m.Origins) == tier));
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
                    enumIssues.Add($"  {t.Name} [{TierOfAll(t.Origins).ToString().ToLowerInvariant()}]: {string.Join(", ", diffs.Take(8))}{(diffs.Count > 8 ? $", +{diffs.Count - 8} more" : "")}");
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
                types = refs.Types.Values.OrderBy(t => t.Name).Select(t => new
                {
                    name = t.Name, present = t.Present, tier = TierOfAll(t.Origins).ToString().ToLowerInvariant(), origins = t.Origins,
                }),
                members = refs.Members.Values.OrderBy(m => m.Display).Select(m => new
                {
                    type = m.TypeName, kind = m.Kind, name = m.Name, signature = m.Display,
                    present = m.Present, note = m.Note, tier = TierOfAll(m.Origins).ToString().ToLowerInvariant(), origins = m.Origins,
                }),
            };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine();
            Console.WriteLine($"JSON report: {jsonPath}");
        }

        bool gameplayGap = missingTypes.Any(t => TierOfAll(t.Origins) == Tier.Gameplay)
                           || missingMembers.Any(m => TierOfAll(m.Origins) == Tier.Gameplay);
        return fail && (gameplayGap || enumIssues.Count > 0) ? 1 : 0;
    }

    private enum Tier { Gameplay, Tooling, Ui }

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
    public bool Present;
    public string? Note;

    public string Display => Kind == "field"
        ? $"{TypeName}::{Name} : {ReturnType}"
        : $"{(IsInstance ? "" : "static ")}{TypeName}::{Name}{(GenericArity > 0 ? $"<{GenericArity}>" : "")}({string.Join(", ", Parameters)}) : {ReturnType}";
    public string Key => $"{Kind}|{Display}";
}

/// <summary>Collects Godot type/member references from the game assembly and attributes
/// each one to the namespaces of the code that uses it.</summary>
internal sealed class ReferenceScanner
{
    public readonly Dictionary<string, TypeRefInfo> Types = new();
    public readonly Dictionary<string, MemberRefInfo> Members = new();

    private readonly MetadataReader _md;
    private readonly HashSet<AssemblyReferenceHandle> _godotAsm = new();
    private readonly Dictionary<TypeReferenceHandle, string> _godotTypeRefs = new();
    private readonly Dictionary<MemberReferenceHandle, MemberRefInfo> _godotMemberRefs = new();
    private readonly SignatureNames _names;
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
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var s = new ReferenceScanner(pe.GetMetadataReader());
        s.Collect(assemblyName);
        s.Attribute(pe);
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
            if (!td.BaseType.IsNil) NoteEntity(td.BaseType, ns, collector);
            foreach (var ih in td.GetInterfaceImplementations()) NoteEntity(_md.GetInterfaceImplementation(ih).Interface, ns, collector);
            foreach (var fh in td.GetFields()) _md.GetFieldDefinition(fh).DecodeSignature(collector, null);
            foreach (var ph in td.GetProperties()) _md.GetPropertyDefinition(ph).DecodeSignature(collector, null);
            foreach (var mh in td.GetMethods())
            {
                var m = _md.GetMethodDefinition(mh);
                m.DecodeSignature(collector, null);
                if (m.RelativeVirtualAddress != 0) ScanBody(pe.GetMethodBody(m.RelativeVirtualAddress), ns, collector);
            }
        }
        foreach (var ch in _md.CustomAttributes)
        {
            var ca = _md.GetCustomAttribute(ch);
            string? ns = OwnerNamespace(ca.Parent);
            if (ns == null) continue;
            collector.Namespace = ns;
            NoteEntity(ca.Constructor, ns, collector);
        }
    }

    private void ScanBody(MethodBodyBlock body, string ns, TypeRefCollector collector)
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
                case OperandType.InlineField:
                case OperandType.InlineMethod:
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
                if (_godotMemberRefs.TryGetValue(mrh, out var info)) Bump(info.Origins, ns);
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

    internal void NoteType(TypeReferenceHandle h, string ns)
    {
        if (_godotTypeRefs.TryGetValue(h, out var name)) Bump(Types[name].Origins, ns);
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
