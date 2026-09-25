using System.Runtime.CompilerServices;

namespace Godot;

// StringName - wraps string (must be a class, not struct, to match real Godot's type signature)
public sealed partial class StringName : IDisposable
{
    private readonly string? _name;
    public StringName() => _name = "";
    public StringName(string name) => _name = name;
    public static implicit operator StringName(string s) => new(s);
    public static implicit operator string(StringName s) => s?._name ?? "";
    public override string ToString() => _name ?? "";
    public override int GetHashCode() => (_name ?? "").GetHashCode();
    public override bool Equals(object? obj) => obj switch
    {
        StringName sn => _name == sn._name,
        string s => _name == s,
        _ => false
    };
    public static bool operator ==(StringName? a, StringName? b) => (a?._name ?? "") == (b?._name ?? "");
    public static bool operator !=(StringName? a, StringName? b) => !(a == b);
    public void Dispose() { }
}

// NodePath (must be a class to match real Godot's type signature)
public sealed class NodePath : IDisposable
{
    private readonly string _path;
    public NodePath() => _path = "";
    public NodePath(string path) => _path = path;
    public static implicit operator NodePath(string s) => new(s);
    public static implicit operator string(NodePath p) => p?._path ?? "";
    public override string ToString() => _path ?? "";
    public void Dispose() { }
}

// Variant - can hold any type
public partial struct Variant
{
    public enum Type
    {
        Nil, Bool, Int, Float, String, Vector2, Vector2I, Rect2, Vector3,
        Transform2D, Color, StringName, NodePath, Object, Dictionary, Array, Signal, Callable
    }

    private readonly object? _value;
    public Variant(object? value) => _value = value;

    public static Variant From<T>(in T from) => new(from);
    public static Variant CreateFrom<T>(T value) => new(value);

    public T As<T>() => _value is T t ? t : default!;
    public object? Obj => _value;

    public static implicit operator Variant(bool v) => new(v);
    public static implicit operator Variant(int v) => new(v);
    public static implicit operator Variant(long v) => new(v);
    public static implicit operator Variant(float v) => new(v);
    public static implicit operator Variant(double v) => new(v);
    public static implicit operator Variant(string v) => new(v);
    public static implicit operator Variant(StringName v) => new(v);
    public static implicit operator Variant(GodotObject v) => new(v);
    public static implicit operator Variant(Vector2 v) => new(v);
    public static implicit operator Variant(Color v) => new(v);

    // Godot converts out of Variant explicitly (op_Explicit), which is what sts2.dll references.
    public static explicit operator bool(Variant v) => v.AsBool();
    public static explicit operator int(Variant v) => (int)v.AsInt64();
    public static explicit operator long(Variant v) => v.AsInt64();
    public static explicit operator ulong(Variant v) => (ulong)v.AsInt64();
    public static explicit operator float(Variant v) => v.AsSingle();
    public static explicit operator double(Variant v) => v.AsDouble();
    public static explicit operator string(Variant v) => v.AsString();
    public static explicit operator Color(Variant v) => v._value is Color c ? c : default;
}

// Callable - wraps a delegate
public partial struct Callable
{
    private readonly Delegate? _delegate;
    public Callable(Delegate? d) => _delegate = d;

    public static Callable From(Action action) => new(action);
    public static Callable From<T>(Action<T> action) => new(action);
    public static Callable From<T1, T2>(Action<T1, T2> action) => new(action);
    public static Callable From<TResult>(Func<TResult> func) => new(func);

    public void Call(params Variant[] args) => (_delegate as Action)?.Invoke();
    public void CallDeferred(params Variant[] args) => Call(args);
}

// Signal-related
[AttributeUsage(AttributeTargets.Delegate)]
public class SignalAttribute : Attribute { }

// IAwaiter - interface referenced by sts2.dll for async/await patterns
public interface IAwaiter : INotifyCompletion
{
    bool IsCompleted { get; }
    void GetResult();
}

public interface IAwaiter<out T> : INotifyCompletion
{
    bool IsCompleted { get; }
    T GetResult();
}

// SignalAwaiter - awaitable (must implement IAwaiter<Variant[]> to match real Godot)
public class SignalAwaiter : IAwaiter<Variant[]>
{
    private bool _completed = true;
    private Action? _continuation;

    public IAwaiter<Variant[]> GetAwaiter() => this;
    public bool IsCompleted => _completed;
    public Variant[] GetResult() => Array.Empty<Variant>();
    public void OnCompleted(Action continuation)
    {
        if (_completed)
            continuation();
        else
            _continuation = continuation;
    }

    internal void Complete()
    {
        _completed = true;
        _continuation?.Invoke();
    }
}

// Export attribute
public enum PropertyHint
{
    None, Range, Enum, ResourceType, NodeType, TypeString, File, Dir, GlobalFile, GlobalDir
}

[Flags]
public enum PropertyUsageFlags
{
    None = 0,
    Default = 1,
    ScriptVariable = 2,
    Storage = 4,
    Editor = 8
}

public enum MethodFlags { Normal, Editor, Virtual }

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class ExportAttribute : Attribute
{
    public ExportAttribute() { }
    public ExportAttribute(PropertyHint hint, string hintString = "") { }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class ExportGroupAttribute : Attribute
{
    public ExportGroupAttribute(string name, string prefix = "") { }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class ExportCategoryAttribute : Attribute
{
    public ExportCategoryAttribute(string name) { }
}

[AttributeUsage(AttributeTargets.Class)]
public class ScriptPathAttribute : Attribute
{
    public ScriptPathAttribute(string path) { }
}

[AttributeUsage(AttributeTargets.Class)]
public class GlobalClassAttribute : Attribute { }
