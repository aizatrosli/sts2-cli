// Godot stub audit: which GodotSharp types/members does sts2.dll use that src/GodotStubs lacks?
//
//   dotnet run --project scripts/godot_stub_audit -- \
//       [--sts2 lib/sts2.dll] [--stub src/Sts2Headless/bin/Debug/net9.0/GodotSharp.dll] \
//       [--reference <real GodotSharp.dll>] [--all]
//
// CoreCLR resolves call/field/type tokens when it JIT-compiles a method, so a game method that
// merely references a missing stub member throws (MissingMethodException / TypeLoadException) the
// first time it runs — even if the reference sits in a branch that is never taken headless. In
// the combat turn loop that surfaces as "turn loop died" and a false game_over.
//
// Members are grouped by whether any referencing type is gameplay code (anything outside the
// UI-only namespaces below). Exit code 1 when gameplay code references a missing member.

using Mono.Cecil;
using Mono.Cecil.Cil;

var argv = args.ToList();
string Opt(string name, string dflt)
{
    var i = argv.IndexOf(name);
    return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : dflt;
}

var repo = FindRepoRoot();
var sts2Path = Opt("--sts2", Path.Combine(repo, "lib", "sts2.dll"));
var stubPath = Opt("--stub", Path.Combine(repo, "src", "Sts2Headless", "bin", "Debug", "net9.0", "GodotSharp.dll"));
var referencePath = Opt("--reference", "");
var showAll = argv.Contains("--all");

foreach (var p in new[] { sts2Path, stubPath })
    if (!File.Exists(p)) { Console.Error.WriteLine($"not found: {p}"); return 2; }

// Namespaces that only run with a Godot scene tree (never headless).
string[] uiOnlyPrefixes =
{
    "MegaCrit.Sts2.Core.Nodes", "MegaCrit.sts2.Core.Nodes", "MegaCrit.Sts2.addons",
    "MegaCrit.Sts2.Core.AutoSlay", "MegaCrit.Sts2.Core.DevConsole", "MegaCrit.Sts2.Core.Bindings",
    "GodotPlugins", "RiderTestRunner",
};

var stub = LoadWithResolver(stubPath, Path.GetDirectoryName(stubPath)!);
var reference = referencePath.Length > 0 ? LoadWithResolver(referencePath, Path.GetDirectoryName(referencePath)!) : null;
var game = ModuleDefinition.ReadModule(sts2Path, new ReaderParameters { ReadingMode = ReadingMode.Deferred });

// key -> (description, referencing types)
var missing = new Dictionary<string, (string Kind, string Text, string? Real, SortedSet<string> Users)>();

void Note(string kind, string key, string text, string? real, TypeDefinition user)
{
    if (!missing.TryGetValue(key, out var entry))
        missing[key] = entry = (kind, text, real, new SortedSet<string>());
    entry.Users.Add(Outer(user).FullName);
}

foreach (var type in game.GetTypes())
{
    foreach (var field in type.Fields) CheckType(field.FieldType, type);
    foreach (var method in type.Methods)
    {
        CheckType(method.ReturnType, type);
        foreach (var p in method.Parameters) CheckType(p.ParameterType, type);
        if (!method.HasBody) continue;
        foreach (var v in method.Body.Variables) CheckType(v.VariableType, type);
        foreach (var ins in method.Body.Instructions)
        {
            switch (ins.Operand)
            {
                case MethodReference mr: CheckMethod(mr, type); break;
                case FieldReference fr: CheckField(fr, type); break;
                case TypeReference tr: CheckType(tr, type); break;
            }
        }
    }
}

bool IsGodot(TypeReference? t)
{
    while (t is TypeSpecification spec) t = spec.ElementType;
    if (t == null || t is GenericParameter) return false;
    var scope = t.DeclaringType != null ? OuterRef(t).Scope : t.Scope;
    return scope is AssemblyNameReference anr && anr.Name == "GodotSharp";
}

void CheckType(TypeReference? t, TypeDefinition user)
{
    if (t == null) return;
    if (t is GenericInstanceType git) foreach (var a in git.GenericArguments) CheckType(a, user);
    while (t is TypeSpecification spec) t = spec.ElementType;
    if (!IsGodot(t)) return;
    if (FindType(stub, t!) == null)
        Note("type", "T:" + t!.FullName, t.FullName, reference != null && FindType(reference, t) != null ? "exists in real GodotSharp" : null, user);
}

void CheckMethod(MethodReference mr, TypeDefinition user)
{
    CheckType(mr.ReturnType, user);
    foreach (var p in mr.Parameters) CheckType(p.ParameterType, user);
    if (mr is GenericInstanceMethod gim)
    {
        foreach (var a in gim.GenericArguments) CheckType(a, user);
        mr = gim.ElementMethod; // compare against the open generic definition (!!0 etc.)
    }
    if (!IsGodot(mr.DeclaringType)) return;
    var declaring = FindType(stub, mr.DeclaringType);
    if (declaring == null) return; // reported as a missing type
    var sig = Signature(mr);
    if (FindMethod(declaring, mr) != null) return;
    string? real = null;
    if (reference != null)
    {
        var rt = FindType(reference, mr.DeclaringType);
        var rm = rt == null ? null : FindMethod(rt, mr);
        real = rm == null ? "NOT in real GodotSharp" : $"real: {rm.FullName}";
    }
    Note("method", "M:" + sig, sig, real, user);
}

void CheckField(FieldReference fr, TypeDefinition user)
{
    CheckType(fr.FieldType, user);
    if (!IsGodot(fr.DeclaringType)) return;
    var declaring = FindType(stub, fr.DeclaringType);
    if (declaring == null) return;
    if (FindField(declaring, fr.Name) != null) return;
    Note("field", "F:" + fr.FullName, fr.FullName, null, user);
}

bool IsUiOnly(string typeName) => uiOnlyPrefixes.Any(typeName.StartsWith);

var gameplay = missing.Where(kv => kv.Value.Users.Any(u => !IsUiOnly(u))).OrderBy(kv => kv.Key).ToList();
var uiOnly = missing.Where(kv => kv.Value.Users.All(IsUiOnly)).OrderBy(kv => kv.Key).ToList();

Console.WriteLine($"GodotSharp references in sts2.dll missing from the stub: {missing.Count} " +
                  $"({gameplay.Count} reachable from gameplay code, {uiOnly.Count} UI-only)\n");
Console.WriteLine("== Referenced from gameplay code ==");
foreach (var (_, v) in gameplay)
{
    Console.WriteLine($"  [{v.Kind}] {v.Text}");
    if (v.Real != null) Console.WriteLine($"      {v.Real}");
    Console.WriteLine($"      used by: {string.Join(", ", v.Users.Where(u => !IsUiOnly(u)).Take(6))}");
}
if (showAll)
{
    Console.WriteLine("\n== UI-only ==");
    foreach (var (_, v) in uiOnly) Console.WriteLine($"  [{v.Kind}] {v.Text}");
}
return gameplay.Count > 0 ? 1 : 0;

// ─── helpers ───

static ModuleDefinition LoadWithResolver(string path, string dir)
{
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(dir);
    return ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Deferred });
}

static TypeDefinition Outer(TypeDefinition t)
{
    while (t.DeclaringType != null) t = t.DeclaringType;
    return t;
}

static TypeReference OuterRef(TypeReference t)
{
    while (t.DeclaringType != null) t = t.DeclaringType;
    return t;
}

static TypeDefinition? FindType(ModuleDefinition module, TypeReference t)
{
    if (t.DeclaringType != null)
    {
        var outer = FindType(module, t.DeclaringType);
        return outer?.NestedTypes.FirstOrDefault(n => n.Name == t.Name);
    }
    return module.GetType(t.Namespace, t.Name);
}

static string Norm(TypeReference t) => t.FullName.Replace("/", "+");

static string Signature(MethodReference m) =>
    $"{Norm(m.ReturnType)} {Norm(m.DeclaringType)}::{m.Name}" +
    (m.HasGenericParameters ? $"`{m.GenericParameters.Count}" : "") +
    $"({string.Join(", ", m.Parameters.Select(p => Norm(p.ParameterType)))})";

// Match by name, generic arity and parameter/return type names, walking base types as the
// runtime does for inherited members. Members of a generic base (Array : List<Variant>) are
// compared with the base's type arguments substituted for its !N parameters.
static MethodDefinition? FindMethod(TypeDefinition type, MethodReference mr)
{
    TypeDefinition? t = type;
    string[] typeArgs = Array.Empty<string>();
    while (t != null)
    {
        foreach (var m in t.Methods)
        {
            if (m.Name != mr.Name || m.Parameters.Count != mr.Parameters.Count) continue;
            if (m.GenericParameters.Count != mr.GenericParameters.Count) continue;
            if (Canon(m.ReturnType, typeArgs) != Canon(mr.ReturnType)) continue;
            bool ok = true;
            for (int i = 0; i < m.Parameters.Count && ok; i++)
                ok = Canon(m.Parameters[i].ParameterType, typeArgs) == Canon(mr.Parameters[i].ParameterType);
            if (ok) return m;
        }
        if (mr.Name == ".ctor") break; // constructors are not inherited
        var baseRef = t.BaseType;
        typeArgs = baseRef is GenericInstanceType git
            ? git.GenericArguments.Select(a => Canon(a, typeArgs)).ToArray()
            : Array.Empty<string>();
        t = SafeBase(t);
    }
    return null;
}

static FieldDefinition? FindField(TypeDefinition type, string name)
{
    for (TypeDefinition? t = type; t != null; t = SafeBase(t))
    {
        var f = t.Fields.FirstOrDefault(x => x.Name == name);
        if (f != null) return f;
    }
    return null;
}

static TypeDefinition? SafeBase(TypeDefinition t)
{
    try { return t.BaseType?.Resolve(); } catch { return null; }
}

// Generic parameters are compared by position (!0 / !!0), everything else by full name.
// Type parameters (!N) are replaced with typeArgs[N] when comparing inherited members.
static string Canon(TypeReference t, string[]? typeArgs = null) => t switch
{
    GenericParameter { Type: GenericParameterType.Type } gp when typeArgs != null && gp.Position < typeArgs.Length
        => typeArgs[gp.Position],
    GenericParameter gp => (gp.Type == GenericParameterType.Method ? "!!" : "!") + gp.Position,
    GenericInstanceType git => Canon(git.ElementType, typeArgs) + "<" + string.Join(",", git.GenericArguments.Select(a => Canon(a, typeArgs))) + ">",
    ByReferenceType br => Canon(br.ElementType, typeArgs) + "&",
    ArrayType at => Canon(at.ElementType, typeArgs) + "[]",
    PointerType pt => Canon(pt.ElementType, typeArgs) + "*",
    RequiredModifierType rm => Canon(rm.ElementType, typeArgs),
    OptionalModifierType om => Canon(om.ElementType, typeArgs),
    _ => t.FullName.Replace("/", "+"),
};

static string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    for (int i = 0; i < 12 && dir != null; i++)
    {
        if (File.Exists(Path.Combine(dir, "setup.sh"))) return dir;
        dir = Directory.GetParent(dir)?.FullName;
    }
    return Directory.GetCurrentDirectory();
}
