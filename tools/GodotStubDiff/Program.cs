// GodotStubDiff — differential test of the stub's value types against the real GodotSharp.dll.
//
// Loads both assemblies side by side (separate AssemblyLoadContexts), and for every public
// method, operator, property getter and constructor that exists with the same signature in
// both, calls them with the same random arguments and compares the results field by field.
// Only managed code can be compared: cases where the real implementation calls into the
// engine (DllNotFoundException etc.) are skipped and reported as "native".
//
// Usage:
//   dotnet run --project tools/GodotStubDiff -- --real <GodotSharp.dll from the game dir>
//       [--stub PATH] [--cases N] [--seed N] [--type Godot.Vector2]...
// Exit code 1 when any method disagrees.

using System.Reflection;
using System.Runtime.Loader;

namespace GodotStubDiff;

internal static class Program
{
    private static readonly string[] DefaultTypes =
    {
        "Godot.Vector2", "Godot.Vector2I", "Godot.Vector3", "Godot.Quaternion", "Godot.Rect2",
        "Godot.Transform2D", "Godot.Color", "Godot.Colors", "Godot.Mathf", "Godot.StringExtensions",
    };

    private static int Main(string[] args)
    {
        string? realPath = null;
        string stubPath = Path.Combine(FindRepoRoot(), "src", "Sts2Headless", "bin", "Debug", "net9.0", "GodotSharp.dll");
        int cases = 300, seed = 12345;
        var types = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--real": realPath = args[++i]; break;
                case "--stub": stubPath = args[++i]; break;
                case "--cases": cases = int.Parse(args[++i]); break;
                case "--seed": seed = int.Parse(args[++i]); break;
                case "--type": types.Add(args[++i]); break;
                default: Console.Error.WriteLine($"unknown argument: {args[i]}"); return 2;
            }
        }
        if (realPath == null || !File.Exists(realPath) || !File.Exists(stubPath))
        {
            Console.Error.WriteLine("usage: GodotStubDiff --real <real GodotSharp.dll> [--stub PATH] [--cases N] [--seed N] [--type NAME]...");
            return 2;
        }
        if (types.Count == 0) types.AddRange(DefaultTypes);

        var real = new AssemblyLoadContext("real").LoadFromAssemblyPath(Path.GetFullPath(realPath));
        var stub = new AssemblyLoadContext("stub").LoadFromAssemblyPath(Path.GetFullPath(stubPath));

        int compared = 0, native = 0, missing = 0, failed = 0;
        foreach (var typeName in types)
        {
            var st = stub.GetType(typeName);
            var rt = real.GetType(typeName);
            if (st == null || rt == null)
            {
                Console.WriteLine($"{typeName}: not in {(st == null ? "stub" : "real")} assembly");
                continue;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            IEnumerable<MethodBase> members = st.GetMethods(flags).Where(m => !m.IsGenericMethodDefinition)
                .Cast<MethodBase>().Concat(st.GetConstructors());
            foreach (var sm in members.OrderBy(m => m.Name))
            {
                if (sm.Name.StartsWith("set_") || sm.Name is "GetHashCode" or "Equals" or "Deconstruct" or "GetType") continue;
                var ps = sm.GetParameters();
                if (ps.Any(p => p.IsOut || !Gen.Supported(p.ParameterType))) continue;
                if (!sm.IsStatic && sm is not ConstructorInfo && !Gen.Supported(st)) continue;

                var rm = FindCounterpart(rt, sm);
                string sig = $"{typeName}.{(sm is ConstructorInfo ? ".ctor" : sm.Name)}({string.Join(", ", ps.Select(p => p.ParameterType.Name))})";
                if (rm == null) { missing++; continue; }
                // Engine entry points are null function pointers outside Godot: calling one
                // crashes the process, so never invoke a method that can reach them.
                if (NativeScan.ReachesNative(rm)) { native++; continue; }

                var rng = new Random(seed ^ sig.GetHashCode(StringComparison.Ordinal));
                int ran = 0, nativeCases = 0;
                string? mismatch = null;
                for (int c = 0; c < cases && mismatch == null; c++)
                {
                    var spec = new Gen.Spec(rng, ps.Select(p => p.ParameterType).ToArray(), sm.IsStatic || sm is ConstructorInfo ? null : st, sig);
                    var (rOk, rVal, rErr) = Invoke(rm, spec, real);
                    if (rErr is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or NullReferenceException or AccessViolationException)
                    {
                        nativeCases++;
                        continue;
                    }
                    var (sOk, sVal, sErr) = Invoke(sm, spec, stub);
                    ran++;
                    if (rOk != sOk)
                        mismatch = $"{spec}: real {(rOk ? Show(rVal) : "threw " + rErr!.GetType().Name)}, stub {(sOk ? Show(sVal) : "threw " + sErr!.GetType().Name + ": " + sErr.Message)}";
                    else if (rOk && !Same(rVal, sVal))
                        mismatch = $"{spec}: real {Show(rVal)}, stub {Show(sVal)}";
                }

                if (ran == 0 && nativeCases > 0) { native++; continue; }
                compared++;
                if (mismatch != null)
                {
                    failed++;
                    Console.WriteLine($"MISMATCH {sig}\n    {mismatch}");
                }
            }
        }

        // The case conversions are native in GodotSharp, so check the stub's ports against
        // the examples in Godot's String class reference instead.
        var se = stub.GetType("Godot.StringExtensions") ?? typeof(object);
        foreach (var (method, input, expected) in KnownAnswers)
        {
            compared++;
            var m = se.GetMethod(method, new[] { typeof(string) });
            if (m == null)
            {
                failed++;
                Console.WriteLine($"MISSING  Godot.StringExtensions.{method}(String)");
                continue;
            }
            var got = (string?)m.Invoke(null, new object[] { input });
            if (got != expected)
            {
                failed++;
                Console.WriteLine($"MISMATCH Godot.StringExtensions.{method}(\"{input}\"): expected \"{expected}\", stub \"{got}\"");
            }
        }

        Console.WriteLine($"\ncompared {compared} members ({cases} cases each), {failed} mismatched; " +
                          $"{native} skipped (real implementation is native), {missing} stub-only members not in the real assembly");
        return failed > 0 ? 1 : 0;
    }

    private static readonly (string Method, string Input, string Expected)[] KnownAnswers =
    {
        ("Capitalize", "move_local_x", "Move Local X"),
        ("Capitalize", "sceneFile_path", "Scene File Path"),
        ("Capitalize", "2D, FPS, PNG", "2d, Fps, Png"),
        ("Capitalize", "IRONCLAD", "Ironclad"),
        ("Capitalize", "OVERGROWTH", "Overgrowth"),
        ("ToSnakeCase", "Node2D", "node_2d"),
        ("ToSnakeCase", "2nd place", "2_nd_place"),
        ("ToSnakeCase", "Texture3DAssetFolder", "texture_3d_asset_folder"),
        ("ToPascalCase", "move_local_x", "MoveLocalX"),
        ("ToCamelCase", "move_local_x", "moveLocalX"),
        ("ToKebabCase", "Node2D", "node-2d"),
        ("ToKebabCase", "Texture3DAssetFolder", "texture-3d-asset-folder"),
    };

    private static MethodBase? FindCounterpart(Type rt, MethodBase sm)
    {
        var names = sm.GetParameters().Select(p => p.ParameterType.FullName).ToArray();
        IEnumerable<MethodBase> candidates = sm is ConstructorInfo
            ? rt.GetConstructors()
            : rt.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => m.Name == sm.Name);
        return candidates.FirstOrDefault(m =>
            m.IsStatic == sm.IsStatic &&
            m.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(names) &&
            (m is not MethodInfo mi || mi.ReturnType.FullName == ((MethodInfo)sm).ReturnType.FullName));
    }

    private static (bool ok, object? value, Exception? error) Invoke(MethodBase m, Gen.Spec spec, Assembly asm)
    {
        try
        {
            var args = spec.Args.Select((a, i) => Gen.Materialize(a, m.GetParameters()[i].ParameterType)).ToArray();
            object? result = m switch
            {
                ConstructorInfo ci => ci.Invoke(args),
                _ => m.Invoke(spec.Receiver == null ? null : Gen.Materialize(spec.Receiver, m.DeclaringType!), args),
            };
            return (true, result, null);
        }
        catch (TargetInvocationException e) { return (false, null, e.InnerException ?? e); }
        catch (Exception e) { return (false, null, e); }
    }

    // Results come from different load contexts, so compare structurally.
    private static bool Same(object? a, object? b)
    {
        if (a == null || b == null) return a == null && b == null;
        switch (a)
        {
            case float fa when b is float fb: return Close(fa, fb, 1e-5);
            case double da when b is double db: return Close(da, db, 1e-12);
            case string or bool or int or long or uint or ulong or byte:
                return a.Equals(b);
        }
        var ta = a.GetType();
        if (ta.IsEnum) return Convert.ToInt64(a) == Convert.ToInt64(b);
        if (ta.IsValueType)
        {
            var fb = b.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var f in ta.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var other = fb.FirstOrDefault(x => x.Name == f.Name);
                if (other == null || !Same(f.GetValue(a), other.GetValue(b))) return false;
            }
            return true;
        }
        return a.ToString() == b.ToString();
    }

    private static bool Close(double a, double b, double rel)
    {
        if (double.IsNaN(a) || double.IsNaN(b)) return double.IsNaN(a) && double.IsNaN(b);
        if (double.IsInfinity(a) || double.IsInfinity(b)) return a == b;
        return Math.Abs(a - b) <= rel * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
    }

    internal static string Show(object? v)
    {
        if (v == null) return "null";
        var t = v.GetType();
        if (!t.IsValueType || t.IsPrimitive || t.IsEnum) return v is string s ? $"\"{s}\"" : v.ToString()!;
        return "{" + string.Join(", ", t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(f => $"{f.Name.Trim('_')}={Show(f.GetValue(v))}")) + "}";
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "GodotStubs"))) return dir.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}

/// <summary>Finds real-assembly methods that can reach engine calls (NativeFuncs / calli).</summary>
internal static class NativeScan
{
    private static readonly Dictionary<short, System.Reflection.Emit.OperandType> Operands = typeof(System.Reflection.Emit.OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value, o => o.OperandType);

    private static readonly Dictionary<MethodBase, bool> Memo = new();

    public static bool ReachesNative(MethodBase m, int depth = 0)
    {
        if (Memo.TryGetValue(m, out bool known)) return known;
        Memo[m] = false; // break recursion cycles
        bool result = Scan(m, depth);
        Memo[m] = result;
        return result;
    }

    private static bool Scan(MethodBase m, int depth)
    {
        if (m.DeclaringType?.FullName?.StartsWith("Godot.NativeInterop.NativeFuncs", StringComparison.Ordinal) == true) return true;
        if ((m.MethodImplementationFlags & MethodImplAttributes.InternalCall) != 0 || m.IsAbstract) return false;
        byte[]? il;
        try { il = m.GetMethodBody()?.GetILAsByteArray(); }
        catch { return true; }
        if (il == null) return false;

        var godot = m.Module.Assembly;
        for (int pos = 0; pos < il.Length;)
        {
            short value = il[pos++];
            if (value == 0xFE) value = unchecked((short)(0xFE00 | il[pos++]));
            if (value == System.Reflection.Emit.OpCodes.Calli.Value) return true;
            switch (Operands[value])
            {
                case System.Reflection.Emit.OperandType.InlineNone: break;
                case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
                case System.Reflection.Emit.OperandType.ShortInlineI:
                case System.Reflection.Emit.OperandType.ShortInlineVar: pos += 1; break;
                case System.Reflection.Emit.OperandType.InlineVar: pos += 2; break;
                case System.Reflection.Emit.OperandType.InlineI8:
                case System.Reflection.Emit.OperandType.InlineR: pos += 8; break;
                case System.Reflection.Emit.OperandType.InlineSwitch: pos += 4 + 4 * BitConverter.ToInt32(il, pos); break;
                case System.Reflection.Emit.OperandType.InlineMethod:
                    int token = BitConverter.ToInt32(il, pos);
                    pos += 4;
                    MethodBase? callee;
                    try
                    {
                        callee = m.Module.ResolveMethod(token,
                            m.DeclaringType?.IsGenericType == true ? m.DeclaringType.GetGenericArguments() : null,
                            m.IsGenericMethod ? m.GetGenericArguments() : null);
                    }
                    catch { return true; }
                    if (callee == null) break;
                    if (callee.DeclaringType?.FullName?.StartsWith("Godot.NativeInterop.NativeFuncs", StringComparison.Ordinal) == true) return true;
                    if (callee.Module.Assembly == godot && depth < 8 && ReachesNative(callee, depth + 1)) return true;
                    break;
                default: pos += 4; break;
            }
        }
        return false;
    }
}

/// <summary>Random arguments described independently of either assembly, then materialized
/// as real or stub instances with identical field values.</summary>
internal static class Gen
{
    // A Godot value type as a bag of field values.
    internal sealed record Fields(string Type, object[] Values);

    internal sealed class Spec
    {
        public readonly object?[] Args;
        public readonly Fields? Receiver;

        public Spec(Random rng, Type[] parameterTypes, Type? receiverType, string sig)
        {
            Args = parameterTypes.Select(t => Make(rng, t, sig)).ToArray();
            Receiver = receiverType == null ? null : (Fields)Make(rng, receiverType, sig)!;
        }

        public override string ToString() =>
            (Receiver != null ? $"this={Show(Receiver)} " : "") + "args=(" + string.Join(", ", Args.Select(Show)) + ")";

        private static string Show(object? a) => a switch
        {
            Fields f => f.Type.Replace("Godot.", "") + "(" + string.Join(", ", f.Values.Select(Show)) + ")",
            string s => $"\"{s}\"",
            null => "null",
            _ => Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture)!,
        };
    }

    private static readonly string[] Strings =
    {
        "", "a", "hello", "OVERGROWTH", "IRONCLAD", "Ironclad", "CrystalSphereItemBomb", "camelCaseName",
        "snake_case_name", "HTTPServer2Go", "res://scenes/backgrounds/x/layers", "user://saves/profile1/run.save",
        "/abs/path/file.txt", "C:\\win\\path.cfg", "dir/", "file.tar.gz", ".hidden", "#ff8800", "f80c", "#12345678",
        "red", "dark_red", "Dark Red", "  padded  ", "Vector2I", "a2b", "ABC", "x_y__z",
    };

    private static readonly float[] Floats = { 0f, 1f, -1f, 0.5f, -0.5f, 2.5f, -2.5f, 3f, 1e-7f, 100f, MathF.PI, -MathF.PI / 2 };

    private static float F(Random r) => r.Next(3) == 0 ? Floats[r.Next(Floats.Length)] : (float)Math.Round(r.NextDouble() * 20 - 10, 3);
    private static float Unit(Random r) => r.Next(4) == 0 ? Floats[r.Next(4)] : (float)Math.Round(r.NextDouble(), 3);
    private static int I(Random r) => r.Next(4) == 0 ? new[] { 0, 1, -1, int.MaxValue / 2 }[r.Next(4)] : r.Next(-12, 13);

    public static bool Supported(Type t)
    {
        if (t.IsByRef) return false;
        if (Nullable.GetUnderlyingType(t) is { } u) return Supported(u);
        return t.FullName switch
        {
            "System.Single" or "System.Double" or "System.Int32" or "System.Int64" or "System.UInt32" or "System.Boolean"
                or "System.String" or "System.Byte" or "Godot.Vector2" or "Godot.Vector2I" or "Godot.Vector3"
                or "Godot.Quaternion" or "Godot.Color" or "Godot.Rect2" or "Godot.Transform2D" => true,
            _ => false,
        };
    }

    private static object? Make(Random r, Type t, string sig)
    {
        if (Nullable.GetUnderlyingType(t) is { } u) return r.Next(3) == 0 ? null : Make(r, u, sig);
        bool colorish = sig.StartsWith("Godot.Color", StringComparison.Ordinal);
        return t.FullName switch
        {
            "System.Single" => colorish ? Unit(r) : F(r),
            "System.Double" => (double)F(r),
            "System.Int32" => I(r),
            "System.Int64" => (long)I(r),
            "System.UInt32" => (uint)r.NextInt64(0, uint.MaxValue),
            "System.Byte" => (byte)r.Next(256),
            "System.Boolean" => r.Next(2) == 0,
            "System.String" => Strings[r.Next(Strings.Length)],
            "Godot.Vector2" => new Fields(t.FullName!, new object[] { F(r), F(r) }),
            "Godot.Vector2I" => new Fields(t.FullName!, new object[] { r.Next(-12, 13), r.Next(-12, 13) }),
            "Godot.Vector3" => new Fields(t.FullName!, new object[] { F(r), F(r), F(r) }),
            "Godot.Quaternion" => new Fields(t.FullName!, new object[] { F(r), F(r), F(r), F(r) }),
            "Godot.Color" => new Fields(t.FullName!, new object[] { Unit(r), Unit(r), Unit(r), Unit(r) }),
            "Godot.Rect2" => new Fields(t.FullName!, new object[] { Make(r, typeof(Probe.V2), sig)!, Make(r, typeof(Probe.V2), sig)! }),
            "Godot.Transform2D" => new Fields(t.FullName!, new object[]
            {
                new Fields("Godot.Vector2", new object[] { F(r), F(r) }),
                new Fields("Godot.Vector2", new object[] { F(r), F(r) }),
                new Fields("Godot.Vector2", new object[] { F(r), F(r) }),
            }),
            _ when t == typeof(Probe.V2) => new Fields("Godot.Vector2", new object[] { F(r), F(r) }),
            _ => throw new NotSupportedException(t.FullName),
        };
    }

    /// <summary>Build the argument as an instance of <paramref name="target"/> (real or stub).</summary>
    public static object? Materialize(object? arg, Type target)
    {
        if (arg is not Fields f) return arg;
        var type = Nullable.GetUnderlyingType(target) ?? target;
        object instance = Activator.CreateInstance(type)!;
        // Declared instance fields in metadata order: X,Y / R,G,B,A / _position,_size / X,Y,Origin.
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderBy(x => x.MetadataToken).ToArray();
        for (int i = 0; i < f.Values.Length; i++)
            fields[i].SetValue(instance, Materialize(f.Values[i], fields[i].FieldType));
        return instance;
    }

    // Placeholder type for nested Vector2 generation inside Rect2.
    private static class Probe { internal struct V2 { } }
}
