using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;

namespace GodotStubAudit;

// Both sides of the comparison render types into the same canonical text:
//   Ns.Outer/Inner, Ns.Gen`2<A,B>, T[], T[,], T&, T*, !0 (type generic param), !!0 (method generic param)

/// <summary>Canonical type names for signatures decoded from the game assembly.</summary>
internal sealed class SignatureNames : ISignatureTypeProvider<string, object?>
{
    private readonly MetadataReader _md;
    public SignatureNames(MetadataReader md) => _md = md;

    public string TypeRefName(TypeReferenceHandle h)
    {
        var tr = _md.GetTypeReference(h);
        string name = _md.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return TypeRefName((TypeReferenceHandle)tr.ResolutionScope) + "/" + name;
        string ns = _md.GetString(tr.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private string TypeDefName(TypeDefinitionHandle h)
    {
        var td = _md.GetTypeDefinition(h);
        string name = _md.GetString(td.Name);
        if (td.IsNested) return TypeDefName(td.GetDeclaringType()) + "/" + name;
        string ns = _md.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => TypeRefName(h);
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => TypeDefName(h);
    public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) =>
        r.GetTypeSpecification(h).DecodeSignature(this, c);
    public string GetSZArrayType(string e) => e + "[]";
    public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetPointerType(string e) => e + "*";
    public string GetPinnedType(string e) => e;
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object? c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object? c, int i) => "!" + i;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr(" + string.Join(",", s.ParameterTypes) + "):" + s.ReturnType;
    public string GetModifiedType(string m, string u, bool req) => u;
    public string GetPrimitiveType(PrimitiveTypeCode c) => c switch
    {
        PrimitiveTypeCode.Void => "System.Void",
        PrimitiveTypeCode.Boolean => "System.Boolean",
        PrimitiveTypeCode.Char => "System.Char",
        PrimitiveTypeCode.SByte => "System.SByte",
        PrimitiveTypeCode.Byte => "System.Byte",
        PrimitiveTypeCode.Int16 => "System.Int16",
        PrimitiveTypeCode.UInt16 => "System.UInt16",
        PrimitiveTypeCode.Int32 => "System.Int32",
        PrimitiveTypeCode.UInt32 => "System.UInt32",
        PrimitiveTypeCode.Int64 => "System.Int64",
        PrimitiveTypeCode.UInt64 => "System.UInt64",
        PrimitiveTypeCode.Single => "System.Single",
        PrimitiveTypeCode.Double => "System.Double",
        PrimitiveTypeCode.String => "System.String",
        PrimitiveTypeCode.IntPtr => "System.IntPtr",
        PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
        PrimitiveTypeCode.Object => "System.Object",
        PrimitiveTypeCode.TypedReference => "System.TypedReference",
        _ => c.ToString(),
    };
}

/// <summary>The stub assembly loaded for inspection (never executed), with its BCL base types.</summary>
internal sealed class StubIndex : IDisposable
{
    private readonly MetadataLoadContext _ctx;
    private readonly Assembly _asm;
    private readonly Dictionary<string, Type> _types = new();

    public StubIndex(string stubPath)
    {
        var paths = Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll").ToList();
        paths.Add(stubPath);
        _ctx = new MetadataLoadContext(new PathAssemblyResolver(paths), "System.Private.CoreLib");
        _asm = _ctx.LoadFromAssemblyPath(stubPath);
        foreach (var t in _asm.GetTypes()) _types[Canon(t)] = t;
    }

    public void Dispose() => _ctx.Dispose();

    public Type? FindType(string canonicalName) => _types.GetValueOrDefault(canonicalName);

    private const BindingFlags Declared =
        BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>Resolve a member reference like the runtime would: exact type first, then base types.</summary>
    public (bool present, string? note) Resolve(MemberRefInfo m)
    {
        var (match, sameName) = Find(m);
        if (match != null) return (true, null);
        if (FindType(m.TypeName) == null) return (false, "type missing");
        return (false, sameName.Count == 0 ? null : "has " + string.Join(" | ", sameName.Distinct()));
    }

    public (MemberInfo? match, List<string> sameName) Find(MemberRefInfo m)
    {
        var sameName = new List<string>();
        var type = FindType(m.TypeName);
        if (type == null) return (null, sameName);

        for (Type? t = type; t != null; t = m.Name == ".ctor" ? null : t.BaseType)
        {
            if (m.Kind == "field")
            {
                foreach (var f in t.GetFields(Declared).Where(f => f.Name == m.Name))
                {
                    if (Canon(f.FieldType) == m.ReturnType) return (f, sameName); // field sigs carry no static flag
                    sameName.Add($"field {Canon(f.FieldType)}");
                }
                if (t.GetProperties(Declared).FirstOrDefault(p => p.Name == m.Name) is { } prop)
                    sameName.Add($"property {Canon(prop.PropertyType)} (field expected)");
                continue;
            }

            IEnumerable<MethodBase> candidates = m.Name == ".ctor"
                ? t.GetConstructors(Declared)
                : t.GetMethods(Declared).Where(x => x.Name == m.Name);
            foreach (var c in candidates)
            {
                var ps = c.GetParameters();
                int arity = c.IsGenericMethodDefinition ? c.GetGenericArguments().Length : 0;
                string ret = c is MethodInfo mi ? Canon(mi.ReturnType) : "System.Void";
                bool match = arity == m.GenericArity
                             && !c.IsStatic == m.IsInstance
                             && ps.Length == m.Parameters.Length
                             && ret == m.ReturnType
                             && ps.Select(p => Canon(p.ParameterType)).SequenceEqual(m.Parameters);
                if (match) return (c, sameName);
                sameName.Add($"{(c.IsStatic ? "static " : "")}{Canon(c.DeclaringType!)}::{c.Name}{(arity > 0 ? $"<{arity}>" : "")}({string.Join(", ", ps.Select(p => Canon(p.ParameterType)))}) : {ret}");
            }
        }
        return (null, sameName);
    }

    /// <summary>C#-ish declaration of a member, with parameter names and defaults.</summary>
    public static string Describe(MemberInfo member)
    {
        string owner = Canon(member.DeclaringType!);
        switch (member)
        {
            case FieldInfo f:
                return $"{(f.IsStatic ? "static " : "")}{(f.IsLiteral ? "const " : "")}{Canon(f.FieldType)} {owner}.{f.Name}";
            case MethodBase mb:
                if (mb.IsSpecialName && mb is MethodInfo acc && (mb.Name.StartsWith("get_") || mb.Name.StartsWith("set_")))
                {
                    var prop = mb.DeclaringType!.GetProperties(Declared).FirstOrDefault(p => p.GetMethod == mb || p.SetMethod == mb);
                    if (prop != null)
                    {
                        string accessors = (prop.GetMethod != null ? "get; " : "") + (prop.SetMethod != null ? "set; " : "");
                        return $"{(acc.IsStatic ? "static " : "")}{Canon(prop.PropertyType)} {owner}.{prop.Name} {{ {accessors}}}";
                    }
                }
                string ps = string.Join(", ", mb.GetParameters().Select(p =>
                {
                    string d = "";
                    if (p.HasDefaultValue)
                    {
                        object? v = p.RawDefaultValue;
                        d = " = " + (v switch { null => "default", string str => $"\"{str}\"", bool b => b ? "true" : "false", _ => v.ToString() });
                    }
                    return $"{Canon(p.ParameterType)} {p.Name}{d}";
                }));
                string gen = mb.IsGenericMethodDefinition ? "<" + string.Join(", ", mb.GetGenericArguments().Select(a => a.Name)) + ">" : "";
                string ret = mb is MethodInfo mi ? Canon(mi.ReturnType) + " " : "";
                return $"{(mb.IsStatic ? "static " : "")}{(mb.IsVirtual && !mb.IsFinal ? "virtual " : "")}{ret}{owner}.{mb.Name}{gen}({ps})";
            default:
                return member.ToString() ?? "";
        }
    }

    /// <summary>Underlying type of an enum ("System.Int64"), or null if not an enum here.</summary>
    public string? EnumUnderlyingType(string canonicalName)
    {
        var t = FindType(canonicalName);
        return t is { IsEnum: true } ? Canon(t.GetEnumUnderlyingType()) : null;
    }

    /// <summary>Enum members (name → value) for an enum type, or null if not an enum here.</summary>
    public Dictionary<string, long>? EnumValues(string canonicalName)
    {
        var t = FindType(canonicalName);
        if (t == null || !t.IsEnum) return null;
        return t.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .ToDictionary(f => f.Name, f => Convert.ToInt64(f.GetRawConstantValue()));
    }

    public static string Canon(Type t)
    {
        if (t.IsByRef) return Canon(t.GetElementType()!) + "&";
        if (t.IsPointer) return Canon(t.GetElementType()!) + "*";
        if (t.IsArray)
            return Canon(t.GetElementType()!) + (t.IsSZArray ? "[]" : "[" + new string(',', t.GetArrayRank() - 1) + "]");
        if (t.IsGenericParameter)
            return (t.DeclaringMethod != null ? "!!" : "!") + t.GenericParameterPosition;
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
            return Canon(t.GetGenericTypeDefinition()) + "<" + string.Join(",", t.GetGenericArguments().Select(Canon)) + ">";
        if (t.IsNested) return Canon(t.DeclaringType!) + "/" + t.Name;
        return string.IsNullOrEmpty(t.Namespace) ? t.Name : t.Namespace + "." + t.Name;
    }
}
