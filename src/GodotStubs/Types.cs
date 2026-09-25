using System.Runtime.CompilerServices;

namespace Godot;

// StringName - wraps string (must be a class, not struct, to match real Godot's type signature)
public sealed class StringName : IDisposable, IEquatable<StringName?>
{
    private readonly string? _name;
    public StringName() => _name = "";
    public StringName(string name) => _name = name;
    public bool IsEmpty => string.IsNullOrEmpty(_name);
    public static implicit operator StringName(string s) => new(s);
    public static implicit operator string(StringName s) => s?._name ?? "";
    public override string ToString() => _name ?? "";
    public override int GetHashCode() => (_name ?? "").GetHashCode();
    public bool Equals(StringName? other) => (_name ?? "") == (other?._name ?? "");
    public override bool Equals(object? obj) => obj switch
    {
        StringName sn => _name == sn._name,
        string s => _name == s,
        _ => false
    };
    public static bool operator ==(StringName? a, StringName? b) => (a?._name ?? "") == (b?._name ?? "");
    public static bool operator !=(StringName? a, StringName? b) => !(a == b);
    // Native-name comparisons from source-generated bridge code; no native names exist headless.
    public static bool operator ==(in NativeInterop.godot_string_name left, StringName right) => false;
    public static bool operator !=(in NativeInterop.godot_string_name left, StringName right) => true;
    public static bool operator ==(StringName left, in NativeInterop.godot_string_name right) => false;
    public static bool operator !=(StringName left, in NativeInterop.godot_string_name right) => true;
    public void Dispose() { }
}

// NodePath (must be a class to match real Godot's type signature)
public sealed class NodePath : IDisposable, IEquatable<NodePath?>
{
    private readonly string _path;
    public NodePath() => _path = "";
    public NodePath(string path) => _path = path ?? "";
    public static implicit operator NodePath(string s) => new(s);
    public static implicit operator string(NodePath p) => p?._path ?? "";
    public bool IsEmpty => _path.Length == 0;
    public bool IsAbsolute() => _path.StartsWith('/');
    public int GetNameCount() => NamesPart.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
    public StringName GetName(int idx) => NamesPart.Split('/', StringSplitOptions.RemoveEmptyEntries)[idx];
    public int GetSubnameCount() => Math.Max(0, _path.Split(':').Length - 1);
    public StringName GetSubname(int idx) => _path.Split(':')[idx + 1];
    public string GetConcatenatedNames() => NamesPart;
    public string GetConcatenatedSubnames() => string.Join(":", _path.Split(':').Skip(1));
    private string NamesPart => _path.Split(':')[0];
    public bool Equals(NodePath? other) => other is not null && _path == other._path;
    public override bool Equals(object? obj) => obj is NodePath p && Equals(p);
    public override int GetHashCode() => _path.GetHashCode();
    public override string ToString() => _path;
    public void Dispose() { }
}

// Signal-related
[AttributeUsage(AttributeTargets.Delegate)]
public class SignalAttribute : Attribute { }

/// <summary>A signal on an object. Awaiting it completes immediately (no engine emits it headless).</summary>
public readonly struct Signal : IAwaitable<Variant[]>
{
    private readonly GodotObject? _owner;
    private readonly StringName? _signalName;

    public Signal(GodotObject owner, StringName name) { _owner = owner; _signalName = name; }

    public GodotObject? Owner => _owner;
    public StringName Name => _signalName ?? new StringName();
    public IAwaiter<Variant[]> GetAwaiter() => new SignalAwaiter();
}

/// <summary>Opaque engine resource handle. Nothing is allocated headless, so it stays invalid.</summary>
public readonly struct Rid : IEquatable<Rid>
{
    private readonly ulong _id;
    internal Rid(ulong id) => _id = id;
    public Rid(GodotObject? from) => _id = 0;
    public ulong Id => _id;
    public bool IsValid => _id != 0;
    public static bool operator ==(Rid left, Rid right) => left._id == right._id;
    public static bool operator !=(Rid left, Rid right) => left._id != right._id;
    public override bool Equals(object? obj) => obj is Rid other && Equals(other);
    public bool Equals(Rid other) => _id == other._id;
    public override int GetHashCode() => _id.GetHashCode();
    public override string ToString() => $"RID({_id})";
}

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

public interface IAwaitable
{
    IAwaiter GetAwaiter();
}

public interface IAwaitable<out TResult>
{
    IAwaiter<TResult> GetAwaiter();
}

// SignalAwaiter - awaitable (must implement IAwaiter<Variant[]> to match real Godot)
public class SignalAwaiter : IAwaiter<Variant[]>, IAwaitable<Variant[]>
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
public enum PropertyHint : long
{
    None = 0,
    Range = 1,
    Enum = 2,
    EnumSuggestion = 3,
    ExpEasing = 4,
    Link = 5,
    Flags = 6,
    Layers2DRender = 7,
    Layers2DPhysics = 8,
    Layers2DNavigation = 9,
    Layers3DRender = 10,
    Layers3DPhysics = 11,
    Layers3DNavigation = 12,
    LayersAvoidance = 37,
    File = 13,
    Dir = 14,
    GlobalFile = 15,
    GlobalDir = 16,
    ResourceType = 17,
    MultilineText = 18,
    Expression = 19,
    PlaceholderText = 20,
    ColorNoAlpha = 21,
    ObjectId = 22,
    TypeString = 23,
    NodePathToEditedNode = 24,
    ObjectTooBig = 25,
    NodePathValidTypes = 26,
    SaveFile = 27,
    GlobalSaveFile = 28,
    IntIsObjectid = 29,
    IntIsPointer = 30,
    ArrayType = 31,
    DictionaryType = 38,
    LocaleId = 32,
    LocalizableString = 33,
    NodeType = 34,
    HideQuaternionEdit = 35,
    Password = 36,
    ToolButton = 39,
    Oneshot = 40,
    GroupEnable = 42,
    InputName = 43,
    FilePath = 44,
    Max = 45,
}

[Flags]
public enum PropertyUsageFlags : long
{
    None = 0,
    Storage = 2,
    Editor = 4,
    Internal = 8,
    Checkable = 16,
    Checked = 32,
    Group = 64,
    Category = 128,
    Subgroup = 256,
    ClassIsBitfield = 512,
    NoInstanceState = 1024,
    RestartIfChanged = 2048,
    ScriptVariable = 4096,
    StoreIfNull = 8192,
    UpdateAllIfModified = 16384,
    ScriptDefaultValue = 32768,
    ClassIsEnum = 65536,
    NilIsVariant = 131072,
    Array = 262144,
    AlwaysDuplicate = 524288,
    NeverDuplicate = 1048576,
    HighEndGfx = 2097152,
    NodePathFromSceneRoot = 4194304,
    ResourceNotPersistent = 8388608,
    KeyingIncrements = 16777216,
    DeferredSetResource = 33554432,
    EditorInstantiateObject = 67108864,
    EditorBasicSetting = 134217728,
    ReadOnly = 268435456,
    Secret = 536870912,
    Default = 6,
    NoEditor = 2,
}

[Flags]
public enum MethodFlags : long
{
    Normal = 1,
    Editor = 2,
    Const = 4,
    Virtual = 8,
    Vararg = 16,
    Static = 32,
    ObjectCore = 64,
    VirtualRequired = 128,
    Default = 1,
}

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

[AttributeUsage(AttributeTargets.GenericParameter)]
public class MustBeVariantAttribute : Attribute { }
