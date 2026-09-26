using System.Globalization;
using System.Reflection;

namespace Godot;

/// <summary>
/// Boxed value with Godot's Variant semantics: integers are stored as long, floats as
/// double, enums as their long value, and conversions between the numeric/bool/string
/// kinds follow Godot instead of failing. <see cref="VariantType"/> reports the real
/// <see cref="Type"/> ordinals, which sts2 compares against as compiled constants.
/// </summary>
public struct Variant : IDisposable
{
    public enum Type : long
    {
        Nil = 0,
        Bool = 1,
        Int = 2,
        Float = 3,
        String = 4,
        Vector2 = 5,
        Vector2I = 6,
        Rect2 = 7,
        Rect2I = 8,
        Vector3 = 9,
        Vector3I = 10,
        Transform2D = 11,
        Vector4 = 12,
        Vector4I = 13,
        Plane = 14,
        Quaternion = 15,
        Aabb = 16,
        Basis = 17,
        Transform3D = 18,
        Projection = 19,
        Color = 20,
        StringName = 21,
        NodePath = 22,
        Rid = 23,
        Object = 24,
        Callable = 25,
        Signal = 26,
        Dictionary = 27,
        Array = 28,
        PackedByteArray = 29,
        PackedInt32Array = 30,
        PackedInt64Array = 31,
        PackedFloat32Array = 32,
        PackedFloat64Array = 33,
        PackedStringArray = 34,
        PackedVector2Array = 35,
        PackedVector3Array = 36,
        PackedColorArray = 37,
        PackedVector4Array = 38,
        Max = 39,
    }

    private readonly object? _value;

    public Variant(object? value) => _value = Normalize(value);

    public readonly object? Obj => _value;

    public readonly Type VariantType => _value switch
    {
        null => Type.Nil,
        bool => Type.Bool,
        long => Type.Int,
        double => Type.Float,
        string => Type.String,
        Vector2 => Type.Vector2,
        Vector2I => Type.Vector2I,
        Rect2 => Type.Rect2,
        Vector3 => Type.Vector3,
        Transform2D => Type.Transform2D,
        Quaternion => Type.Quaternion,
        Color => Type.Color,
        StringName => Type.StringName,
        NodePath => Type.NodePath,
        Rid => Type.Rid,
        GodotObject => Type.Object,
        Callable => Type.Callable,
        Signal => Type.Signal,
        Godot.Collections.Dictionary => Type.Dictionary,
        Godot.Collections.Array => Type.Array,
        byte[] => Type.PackedByteArray,
        int[] => Type.PackedInt32Array,
        long[] => Type.PackedInt64Array,
        float[] => Type.PackedFloat32Array,
        double[] => Type.PackedFloat64Array,
        string[] => Type.PackedStringArray,
        Vector2[] => Type.PackedVector2Array,
        Vector3[] => Type.PackedVector3Array,
        Color[] => Type.PackedColorArray,
        _ when _value.GetType().IsGenericType && _value is System.Collections.IDictionary => Type.Dictionary,
        _ when _value is System.Collections.IList => Type.Array,
        _ => Type.Object,
    };

    public static Variant From<[MustBeVariant] T>(in T from) => new(from);
    public readonly T As<[MustBeVariant] T>() => (T)ConvertTo(typeof(T))!;

    internal static Variant FromObject(object? value) => value is Variant v ? v : new Variant(value);

    private static object? Normalize(object? value) => value switch
    {
        null => null,
        Variant v => v._value,
        Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture),
        sbyte or short or int or byte or ushort or uint or ulong => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        char c => (long)c,
        float f => (double)f,
        _ => value,
    };

    /// <summary>Convert the held value to <paramref name="target"/>; mismatches give the default value.</summary>
    internal readonly object? ConvertTo(System.Type target)
    {
        if (target == typeof(Variant)) return this;
        if (target == typeof(object)) return _value;
        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        object? v = _value;

        if (underlying.IsEnum)
            return Enum.ToObject(underlying, v is IConvertible ? ToInt64(v) : 0L);
        if (underlying == typeof(bool)) return ToBool(v);
        if (underlying == typeof(string)) return ToStr(v);
        if (underlying == typeof(StringName)) return v as StringName ?? new StringName(ToStr(v));
        if (underlying == typeof(NodePath)) return v as NodePath ?? new NodePath(ToStr(v));
        if (underlying.IsPrimitive && underlying != typeof(IntPtr) && underlying != typeof(UIntPtr))
        {
            if (v is bool b) v = b ? 1L : 0L;
            if (v is string s) v = double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0d;
            if (v is not IConvertible) return Activator.CreateInstance(underlying);
            if (v is double dv && underlying != typeof(float) && underlying != typeof(double))
                v = Math.Truncate(dv); // Godot truncates float→int
            try { return Convert.ChangeType(v, underlying, CultureInfo.InvariantCulture); }
            catch (OverflowException) { return Convert.ChangeType(unchecked((long)ToInt64(v)), underlying, CultureInfo.InvariantCulture); }
        }
        if (v != null && target.IsInstanceOfType(v)) return v;
        if (v is System.Collections.IEnumerable items && target.IsArray)
        {
            var elem = target.GetElementType()!;
            var list = items.Cast<object?>().Select(x => FromObject(x).ConvertTo(elem)).ToList();
            var arr = System.Array.CreateInstance(elem, list.Count);
            for (int i = 0; i < list.Count; i++) arr.SetValue(list[i], i);
            return arr;
        }
        if (v is System.Collections.IEnumerable src && !target.IsValueType && typeof(System.Collections.IList).IsAssignableFrom(target)
            && target.GetConstructor(System.Type.EmptyTypes) != null)
        {
            var list = (System.Collections.IList)Activator.CreateInstance(target)!;
            var elem = target.IsGenericType ? target.GetGenericArguments()[0] : typeof(Variant);
            foreach (var x in src) list.Add(FromObject(x).ConvertTo(elem));
            return list;
        }
        return target.IsValueType ? Activator.CreateInstance(target) : null;
    }

    private static long ToInt64(object v) => v switch
    {
        double d => (long)d,
        _ => Convert.ToInt64(v, CultureInfo.InvariantCulture),
    };

    private static bool ToBool(object? v) => v switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        double d => d != 0,
        string s => s.Length != 0,
        StringName sn => !sn.IsEmpty,
        _ => true,
    };

    private static string ToStr(object? v) => v switch
    {
        null => "<null>",
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    public readonly bool AsBool() => ToBool(_value);
    public readonly char AsChar() => (char)AsInt64();
    public readonly sbyte AsSByte() => As<sbyte>();
    public readonly short AsInt16() => As<short>();
    public readonly int AsInt32() => As<int>();
    public readonly long AsInt64() => As<long>();
    public readonly byte AsByte() => As<byte>();
    public readonly ushort AsUInt16() => As<ushort>();
    public readonly uint AsUInt32() => As<uint>();
    public readonly ulong AsUInt64() => As<ulong>();
    public readonly float AsSingle() => As<float>();
    public readonly double AsDouble() => As<double>();
    public readonly string AsString() => ToStr(_value);
    public readonly StringName AsStringName() => As<StringName>();
    public readonly NodePath AsNodePath() => As<NodePath>();
    public readonly GodotObject AsGodotObject() => (_value as GodotObject)!;
    public readonly Vector2 AsVector2() => As<Vector2>();
    public readonly Vector2I AsVector2I() => As<Vector2I>();
    public readonly Rect2 AsRect2() => As<Rect2>();
    public readonly Transform2D AsTransform2D() => As<Transform2D>();
    public readonly Vector3 AsVector3() => As<Vector3>();
    public readonly Quaternion AsQuaternion() => As<Quaternion>();
    public readonly Color AsColor() => As<Color>();
    public readonly Callable AsCallable() => As<Callable>();
    public readonly Signal AsSignal() => As<Signal>();
    public readonly Rid AsRid() => As<Rid>();
    public readonly byte[] AsByteArray() => As<byte[]>() ?? System.Array.Empty<byte>();
    public readonly int[] AsInt32Array() => As<int[]>() ?? System.Array.Empty<int>();
    public readonly long[] AsInt64Array() => As<long[]>() ?? System.Array.Empty<long>();
    public readonly float[] AsFloat32Array() => As<float[]>() ?? System.Array.Empty<float>();
    public readonly double[] AsFloat64Array() => As<double[]>() ?? System.Array.Empty<double>();
    public readonly string[] AsStringArray() => As<string[]>() ?? System.Array.Empty<string>();
    public readonly Vector2[] AsVector2Array() => As<Vector2[]>() ?? System.Array.Empty<Vector2>();
    public readonly Color[] AsColorArray() => As<Color[]>() ?? System.Array.Empty<Color>();
    public readonly T[] AsGodotObjectArray<T>() where T : GodotObject => As<T[]>() ?? System.Array.Empty<T>();
    public readonly Godot.Collections.Array AsGodotArray() => As<Godot.Collections.Array>() ?? new();
    public readonly Godot.Collections.Array<T> AsGodotArray<[MustBeVariant] T>() => As<Godot.Collections.Array<T>>() ?? new();
    public readonly Godot.Collections.Dictionary AsGodotDictionary() => _value as Godot.Collections.Dictionary ?? new();
    public readonly Godot.Collections.Dictionary<TKey, TValue> AsGodotDictionary<[MustBeVariant] TKey, [MustBeVariant] TValue>() where TKey : notnull =>
        _value as Godot.Collections.Dictionary<TKey, TValue> ?? new();

    public static Variant CreateFrom(bool from) => new(from);
    public static Variant CreateFrom(char from) => new(from);
    public static Variant CreateFrom(sbyte from) => new(from);
    public static Variant CreateFrom(short from) => new(from);
    public static Variant CreateFrom(int from) => new(from);
    public static Variant CreateFrom(long from) => new(from);
    public static Variant CreateFrom(byte from) => new(from);
    public static Variant CreateFrom(ushort from) => new(from);
    public static Variant CreateFrom(uint from) => new(from);
    public static Variant CreateFrom(ulong from) => new(from);
    public static Variant CreateFrom(float from) => new(from);
    public static Variant CreateFrom(double from) => new(from);
    public static Variant CreateFrom(string from) => new(from);
    public static Variant CreateFrom(StringName from) => new(from);
    public static Variant CreateFrom(NodePath from) => new(from);
    public static Variant CreateFrom(GodotObject from) => new(from);
    public static Variant CreateFrom(Vector2 from) => new(from);
    public static Variant CreateFrom(Vector2I from) => new(from);
    public static Variant CreateFrom(Rect2 from) => new(from);
    public static Variant CreateFrom(Transform2D from) => new(from);
    public static Variant CreateFrom(Vector3 from) => new(from);
    public static Variant CreateFrom(Color from) => new(from);
    public static Variant CreateFrom(Callable from) => new(from);
    public static Variant CreateFrom(Signal from) => new(from);
    public static Variant CreateFrom(Rid from) => new(from);
    public static Variant CreateFrom(byte[] from) => new(from);
    public static Variant CreateFrom(int[] from) => new(from);
    public static Variant CreateFrom(long[] from) => new(from);
    public static Variant CreateFrom(float[] from) => new(from);
    public static Variant CreateFrom(double[] from) => new(from);
    public static Variant CreateFrom(string[] from) => new(from);
    public static Variant CreateFrom(Vector2[] from) => new(from);
    public static Variant CreateFrom(Color[] from) => new(from);
    public static Variant CreateFrom(GodotObject[] from) => new(from);
    public static Variant CreateFrom(Godot.Collections.Array from) => new(from);
    public static Variant CreateFrom(Godot.Collections.Dictionary from) => new(from);
    public static Variant CreateFrom<[MustBeVariant] T>(Godot.Collections.Array<T> from) => new(from);
    public static Variant CreateFrom<[MustBeVariant] TKey, [MustBeVariant] TValue>(Godot.Collections.Dictionary<TKey, TValue> from) where TKey : notnull => new(from);

    public static implicit operator Variant(bool from) => new(from);
    public static implicit operator Variant(char from) => new(from);
    public static implicit operator Variant(sbyte from) => new(from);
    public static implicit operator Variant(short from) => new(from);
    public static implicit operator Variant(int from) => new(from);
    public static implicit operator Variant(long from) => new(from);
    public static implicit operator Variant(byte from) => new(from);
    public static implicit operator Variant(ushort from) => new(from);
    public static implicit operator Variant(uint from) => new(from);
    public static implicit operator Variant(ulong from) => new(from);
    public static implicit operator Variant(float from) => new(from);
    public static implicit operator Variant(double from) => new(from);
    public static implicit operator Variant(string from) => new(from);
    public static implicit operator Variant(StringName from) => new(from);
    public static implicit operator Variant(NodePath from) => new(from);
    public static implicit operator Variant(GodotObject from) => new(from);
    public static implicit operator Variant(Vector2 from) => new(from);
    public static implicit operator Variant(Vector2I from) => new(from);
    public static implicit operator Variant(Rect2 from) => new(from);
    public static implicit operator Variant(Transform2D from) => new(from);
    public static implicit operator Variant(Vector3 from) => new(from);
    public static implicit operator Variant(Quaternion from) => new(from);
    public static implicit operator Variant(Color from) => new(from);
    public static implicit operator Variant(Callable from) => new(from);
    public static implicit operator Variant(Signal from) => new(from);
    public static implicit operator Variant(Rid from) => new(from);
    public static implicit operator Variant(byte[] from) => new(from);
    public static implicit operator Variant(int[] from) => new(from);
    public static implicit operator Variant(long[] from) => new(from);
    public static implicit operator Variant(float[] from) => new(from);
    public static implicit operator Variant(double[] from) => new(from);
    public static implicit operator Variant(string[] from) => new(from);
    public static implicit operator Variant(Vector2[] from) => new(from);
    public static implicit operator Variant(Color[] from) => new(from);
    public static implicit operator Variant(GodotObject[] from) => new(from);
    public static implicit operator Variant(Godot.Collections.Array from) => new(from);
    public static implicit operator Variant(Godot.Collections.Dictionary from) => new(from);

    public static explicit operator bool(Variant from) => from.AsBool();
    public static explicit operator char(Variant from) => from.AsChar();
    public static explicit operator sbyte(Variant from) => from.AsSByte();
    public static explicit operator short(Variant from) => from.AsInt16();
    public static explicit operator int(Variant from) => from.AsInt32();
    public static explicit operator long(Variant from) => from.AsInt64();
    public static explicit operator byte(Variant from) => from.AsByte();
    public static explicit operator ushort(Variant from) => from.AsUInt16();
    public static explicit operator uint(Variant from) => from.AsUInt32();
    public static explicit operator ulong(Variant from) => from.AsUInt64();
    public static explicit operator float(Variant from) => from.AsSingle();
    public static explicit operator double(Variant from) => from.AsDouble();
    public static explicit operator string(Variant from) => from.AsString();
    public static explicit operator StringName(Variant from) => from.AsStringName();
    public static explicit operator NodePath(Variant from) => from.AsNodePath();
    public static explicit operator GodotObject(Variant from) => from.AsGodotObject();
    public static explicit operator Vector2(Variant from) => from.AsVector2();
    public static explicit operator Vector2I(Variant from) => from.AsVector2I();
    public static explicit operator Rect2(Variant from) => from.AsRect2();
    public static explicit operator Transform2D(Variant from) => from.AsTransform2D();
    public static explicit operator Vector3(Variant from) => from.AsVector3();
    public static explicit operator Quaternion(Variant from) => from.AsQuaternion();
    public static explicit operator Color(Variant from) => from.AsColor();
    public static explicit operator Callable(Variant from) => from.AsCallable();
    public static explicit operator Signal(Variant from) => from.AsSignal();
    public static explicit operator Rid(Variant from) => from.AsRid();
    public static explicit operator byte[](Variant from) => from.AsByteArray();
    public static explicit operator int[](Variant from) => from.AsInt32Array();
    public static explicit operator long[](Variant from) => from.AsInt64Array();
    public static explicit operator float[](Variant from) => from.AsFloat32Array();
    public static explicit operator double[](Variant from) => from.AsFloat64Array();
    public static explicit operator string[](Variant from) => from.AsStringArray();
    public static explicit operator Vector2[](Variant from) => from.AsVector2Array();
    public static explicit operator Color[](Variant from) => from.AsColorArray();
    public static explicit operator Godot.Collections.Array(Variant from) => from.AsGodotArray();
    public static explicit operator Godot.Collections.Dictionary(Variant from) => from.AsGodotDictionary();

    public readonly void Dispose() { }

    public override readonly bool Equals(object? obj) => obj is Variant other && Equals(_value, other._value);
    public override readonly int GetHashCode() => _value?.GetHashCode() ?? 0;
    public override readonly string ToString() => ToStr(_value);
}
