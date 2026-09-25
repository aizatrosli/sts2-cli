using System.Reflection;
using System.Runtime.CompilerServices;

namespace Godot;

public class GodotObject : IDisposable
{
    public enum ConnectFlags : long
    {
        Deferred = 1,
        Persist = 2,
        OneShot = 4,
        ReferenceCounted = 8,
        AppendSourceObject = 16,
    }

    public class MethodName { }
    public class PropertyName { }
    public class SignalName { }

    // Signal connections are recorded (so Connect/IsConnected/Disconnect agree) but never
    // fired: headless has no engine emitting signals, and EmitSignal stays a no-op.
    private List<(StringName Signal, Callable Callable)>? _connections;

    public static bool IsInstanceValid(GodotObject? obj) => obj != null;
    // Instance ids are not tracked headless.
    public static GodotObject? InstanceFromId(ulong instanceId) => null;
    public virtual bool IsQueuedForDeletion() => false;

    // ToSignal - must be on GodotObject (not Node) to match real Godot
    public SignalAwaiter ToSignal(GodotObject source, StringName signal)
    {
        return new SignalAwaiter();
    }

    /// <summary>Name of the closest engine (stub) class, like Godot's get_class().</summary>
    public string GetClass()
    {
        var stub = typeof(GodotObject).Assembly;
        Type t = GetType();
        while (t.Assembly != stub && t.BaseType != null) t = t.BaseType;
        return t.Name == nameof(GodotObject) ? "Object" : t.Name;
    }

    public bool IsClass(string @class)
    {
        var stub = typeof(GodotObject).Assembly;
        for (Type? t = GetType(); t != null; t = t.BaseType)
        {
            if (t.Assembly == stub && (t.Name == @class || (t == typeof(GodotObject) && @class == "Object"))) return true;
        }
        return false;
    }

    public ulong GetInstanceId() => (ulong)RuntimeHelpers.GetHashCode(this);

    public bool HasMethod(StringName method) => FindMethods(method).Any();

    public bool HasSignal(StringName signal) =>
        GetType().GetEvent(signal.ToString(), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null;

    public Error Connect(StringName signal, Callable callable, uint flags = 0)
    {
        (_connections ??= new()).Add((signal, callable));
        return Error.Ok;
    }

    public void Disconnect(StringName signal, Callable callable)
    {
        int i = _connections?.FindIndex(c => c.Signal == signal && c.Callable.Equals(callable)) ?? -1;
        if (i >= 0) _connections!.RemoveAt(i);
    }

    public bool IsConnected(StringName signal, Callable callable) =>
        _connections?.Any(c => c.Signal == signal && c.Callable.Equals(callable)) ?? false;

    public Error EmitSignal(StringName signal, params Variant[] args) => Error.Ok;

    public Godot.Collections.Array<Godot.Collections.Dictionary> GetSignalList() => new();
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetIncomingConnections() => new();
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetSignalConnectionList(StringName signal) => new();
    public void SetScript(Variant script) { }

    /// <summary>Invoke a C# method by name (PascalCase, or Godot's snake_case spelling).</summary>
    public Variant Call(StringName method, params Variant[] args)
    {
        foreach (var m in FindMethods(method))
        {
            var ps = m.GetParameters();
            if (args.Length > ps.Length || ps.Skip(args.Length).Any(p => !p.HasDefaultValue)) continue;
            var values = new object?[ps.Length];
            for (int i = 0; i < ps.Length; i++)
                values[i] = i < args.Length ? args[i].ConvertTo(ps[i].ParameterType) : ps[i].DefaultValue;
            return Variant.FromObject(m.Invoke(this, values));
        }
        return default;
    }

    /// <summary>Headless has no idle frame to defer to, so the call runs now. As in Godot,
    /// a failing deferred call is reported, not thrown back at the caller.</summary>
    public Variant CallDeferred(StringName method, params Variant[] args)
    {
        try
        {
            Call(method, args);
        }
        catch (Exception e)
        {
            GD.PushError($"CallDeferred {GetType().Name}.{method}: {(e as TargetInvocationException)?.InnerException?.Message ?? e.Message}");
        }
        return default;
    }

    public Variant Get(StringName property)
    {
        var p = GetType().GetProperty(property.ToString(), BindingFlags.Public | BindingFlags.Instance);
        return p != null && p.CanRead ? Variant.FromObject(p.GetValue(this)) : default;
    }

    public void Set(StringName property, Variant value)
    {
        var p = GetType().GetProperty(property.ToString(), BindingFlags.Public | BindingFlags.Instance);
        if (p != null && p.CanWrite) p.SetValue(this, value.ConvertTo(p.PropertyType));
    }

    public void SetDeferred(StringName property, Variant value) => Set(property, value);

    private Dictionary<StringName, Variant>? _meta;
    public void SetMeta(StringName name, Variant value) => (_meta ??= new())[name] = value;
    public Variant GetMeta(StringName name, Variant @default = default) =>
        _meta != null && _meta.TryGetValue(name, out var v) ? v : @default;
    public bool HasMeta(StringName name) => _meta?.ContainsKey(name) ?? false;
    public void RemoveMeta(StringName name) => _meta?.Remove(name);

    public void Notification(int what, bool reversed = false) { }
    public void Free() { }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing) { }

    private IEnumerable<System.Reflection.MethodInfo> FindMethods(StringName method)
    {
        string name = method.ToString();
        string pascal = StringExtensions.ToPascalCase(name);
        return GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => !m.IsGenericMethodDefinition && (m.Name == name || m.Name == pascal));
    }

    // Bridge methods overridden in generated code
    protected virtual void SaveGodotObjectData(Bridge.GodotSerializationInfo info) { }
    protected virtual void RestoreGodotObjectData(Bridge.GodotSerializationInfo info) { }
    protected virtual bool InvokeGodotClassMethod(in NativeInterop.godot_string_name method, NativeInterop.NativeVariantPtrArgs args, out NativeInterop.godot_variant ret) { ret = default; return false; }
    protected virtual bool HasGodotClassMethod(in NativeInterop.godot_string_name method) => false;
    protected virtual bool SetGodotClassPropertyValue(in NativeInterop.godot_string_name name, in NativeInterop.godot_variant value) => false;
    protected virtual bool GetGodotClassPropertyValue(in NativeInterop.godot_string_name name, out NativeInterop.godot_variant value) { value = default; return false; }
    protected virtual void RaiseGodotClassSignalCallbacks(in NativeInterop.godot_string_name signal, NativeInterop.NativeVariantPtrArgs args) { }
    protected virtual bool HasGodotClassSignal(in NativeInterop.godot_string_name signal) => false;
    public virtual void _Notification(int what) { }
}

public class Node : GodotObject
{
    public enum ProcessModeEnum : long
    {
        Inherit = 0,
        Pausable = 1,
        WhenPaused = 2,
        Always = 3,
        Disabled = 4,
    }

    public enum InternalMode : long
    {
        Disabled = 0,
        Front = 1,
        Back = 2,
    }

    private Node? _parent;
    private readonly List<Node> _children = new();

    public new class MethodName : GodotObject.MethodName
    {
        public static readonly StringName AddChild = "AddChild";
        public static readonly StringName AddSibling = "AddSibling";
        public static readonly StringName MoveChild = "MoveChild";
        public static readonly StringName RemoveChild = "RemoveChild";
        public static readonly StringName QueueFree = "QueueFree";
        public static readonly StringName _Ready = "_Ready";
    }

    public new class PropertyName : GodotObject.PropertyName { }
    public new class SignalName : GodotObject.SignalName
    {
        public static readonly StringName ProcessFrame = "ProcessFrame";
        public static readonly StringName Ready = "ready";
        public static readonly StringName TreeEntered = "tree_entered";
        public static readonly StringName TreeExiting = "tree_exiting";
        public static readonly StringName TreeExited = "tree_exited";
    }

    public virtual StringName Name { get; set; } = "";
    public void SetName(string name) => Name = name;
    public string SceneFilePath { get; set; } = "";
    public ProcessModeEnum ProcessMode { get; set; }
    public void SetProcess(bool enable) { }
    public void SetPhysicsProcess(bool enable) { }
    public void SetProcessInput(bool enable) { }
    public Window GetWindow() => GetTree().Root;

    // Tree notifications never fire headless (nodes never enter or leave a tree).
#pragma warning disable CS0067
    public event Action? TreeEntered;
    public event Action? TreeExiting;
    public event Action? TreeExited;
    public event Action? Ready;
#pragma warning restore CS0067

    public Node? GetParent() => _parent;
    // Lenient where Godot casts: headless node trees are not the real scenes.
    public T? GetParent<T>() where T : class => _parent as T;

    public Godot.Collections.Array<Node> GetChildren(bool includeInternal = false)
    {
        return new Godot.Collections.Array<Node>(_children);
    }

    public Node? GetChild(int idx, bool includeInternal = false)
    {
        if (idx < 0) idx += _children.Count;
        return idx >= 0 && idx < _children.Count ? _children[idx] : null;
    }

    public T? GetChild<T>(int idx, bool includeInternal = false) where T : class => GetChild(idx, includeInternal) as T;
    public T? GetChildOrNull<T>(int idx, bool includeInternal = false) where T : class => GetChild(idx, includeInternal) as T;

    // No scenes are loaded headless, so path lookups find nothing.
    public Node? GetNodeOrNull(NodePath path) => null;
    public Node GetNode(NodePath path) => null!;
    public T? GetNodeOrNull<T>(string path) where T : class => null;
    public T? GetNodeOrNull<T>(NodePath path) where T : class => null;
    public T GetNode<T>(string path) where T : class => default!;
    public T GetNode<T>(NodePath path) where T : class => default!;
    public bool HasNode(NodePath path) => false;

    public NodePath GetPath()
    {
        var names = new List<string>();
        for (Node? n = this; n != null; n = n._parent) names.Add(n.Name.ToString());
        names.Reverse();
        return new NodePath("/" + string.Join("/", names));
    }

    // Like Godot, a node that already has a parent is not added again (Godot logs an error).
    public virtual void AddChild(Node child, bool forceReadableName = false, InternalMode @internal = InternalMode.Disabled)
    {
        if (child._parent != null || child == this) return;
        child._parent = this;
        _children.Add(child);
    }

    public void AddSibling(Node sibling, bool forceReadableName = false)
    {
        if (_parent == null || sibling._parent != null) return;
        sibling._parent = _parent;
        _parent._children.Insert(_parent._children.IndexOf(this) + 1, sibling);
    }

    public virtual void RemoveChild(Node child)
    {
        child._parent = null;
        _children.Remove(child);
    }

    public void MoveChild(Node childNode, int toIndex)
    {
        if (!_children.Remove(childNode)) return;
        if (toIndex < 0) toIndex += _children.Count + 1;
        _children.Insert(Math.Clamp(toIndex, 0, _children.Count), childNode);
    }

    public void Reparent(Node newParent, bool keepGlobalTransform = true)
    {
        _parent?.RemoveChild(this);
        newParent.AddChild(this);
    }

    public virtual void QueueFree() { }

    public SceneTree GetTree() => Engine.GetMainLoop() as SceneTree ?? new SceneTree();

    public Tween CreateTween() => new Tween();
    public Viewport GetViewport() => new Viewport();
    public double GetProcessDeltaTime() => 0.016;
    public bool IsAncestorOf(Node node)
    {
        for (Node? n = node._parent; n != null; n = n._parent)
        {
            if (n == this) return true;
        }
        return false;
    }
    public bool IsInsideTree() => false;
    // Nodes never enter a scene tree headless, so _Ready never runs.
    public bool IsNodeReady() => false;
    public int GetChildCount(bool includeInternal = false) => _children.Count;
    public int GetIndex(bool includeInternal = false) => _parent?._children.IndexOf(this) ?? -1;

    public virtual void _Ready() { }
    public virtual void _EnterTree() { }
    public virtual void _ExitTree() { }
    public virtual void _Process(double delta) { }
    public virtual void _PhysicsProcess(double delta) { }
    public virtual void _Input(InputEvent @event) { }
    public virtual void _UnhandledInput(InputEvent @event) { }
    public virtual void _UnhandledKeyInput(InputEvent @event) { }
}

public class SceneTree : MainLoop
{
    public new class SignalName : MainLoop.SignalName
    {
        public static readonly StringName ProcessFrame = "process_frame";
    }

    public SceneTreeTimer CreateTimer(double timeSec, bool processAlways = true, bool processInPhysics = false, bool ignoreTimeScale = false)
    {
        var timer = new SceneTreeTimer();
        // Immediately fire the timeout in headless mode
        timer.FireTimeout();
        return timer;
    }

    public Window Root { get; } = new Window();
    public Tween CreateTween() => new Tween();
}

public class SceneTreeTimer : GodotObject
{
    public new class SignalName : GodotObject.SignalName
    {
        public static readonly StringName Timeout = "timeout";
    }

    public event Action? Timeout;

    internal void FireTimeout()
    {
        Timeout?.Invoke();
    }
}

public class MainLoop : GodotObject
{
    public new class SignalName : GodotObject.SignalName { }
}

public static class Engine
{
    private static readonly SceneTree _mainLoop = new();
    public static MainLoop GetMainLoop() => _mainLoop;
    public static bool IsEditorHint() => false;
}

public static class GD
{
    public static void Print(params object[] args) => Console.Error.WriteLine(string.Join("", args));
    public static void Print(string msg) => Console.Error.WriteLine(msg);
    public static void PrintErr(params object[] args) => Console.Error.WriteLine("[ERROR] " + string.Join("", args));
    public static void PrintErr(string msg) => Console.Error.WriteLine("[ERROR] " + msg);
    public static void PushError(params object[] args) => Console.Error.WriteLine("[ERROR] " + string.Join("", args));
    public static void PushError(string msg) => Console.Error.WriteLine("[ERROR] " + msg);
    public static void PushWarning(params object[] args) { }
    public static void PushWarning(string msg) { }
    public static void PrintRich(params object[] args) { }
    public static void PrintRich(string msg) { }
    public static Variant Str(params Variant[] args) => string.Join("", args.Select(a => a.ToString()));
    // Nothing is loadable headless (see ResourceLoader.Load).
    public static T? Load<T>(string path) where T : class => null;
    public static Resource? Load(string path) => null;
    // Godot's global RNG is unseeded; only cosmetic/tooling code uses it.
    public static float Randf() => Random.Shared.NextSingle();
    public static double RandRange(double from, double to) => from + Random.Shared.NextDouble() * (to - from);
    public static int RandRange(int from, int to) => Random.Shared.Next(Math.Min(from, to), Math.Max(from, to) + 1);
}

public static class OS
{
    public static Error ShellOpen(string uri) => Error.Ok;
    public static string GetLocale() => "en";
    public static string GetName() => "headless";
    public static string GetVersion() => "0.0";
    public static string GetExecutablePath() => "";
    public static bool HasFeature(string feature) => false;
    public static bool IsDebugBuild() => false;
    public static string GetDataDir() => ".";
    public static string GetUserDataDir() => ".";
    public static string[] GetCmdlineArgs() => Array.Empty<string>();
    public static Godot.Collections.Dictionary GetMemoryInfo() => new()
    {
        ["physical"] = -1L, ["free"] = -1L, ["available"] = -1L, ["stack"] = -1L,
    };
}

public static class ProjectSettings
{
    public static string GlobalizePath(string path) => path;
    public static Variant GetSetting(string name, Variant @default = default) => @default;
    public static bool LoadResourcePack(string pack, bool replaceFiles = true, int offset = 0) => false;
}

public static class ResourceLoader
{
    public enum CacheMode : long
    {
        Ignore = 0,
        Reuse = 1,
        Replace = 2,
        IgnoreDeep = 3,
        ReplaceDeep = 4,
    }

    public enum ThreadLoadStatus : long
    {
        InvalidResource = 0,
        InProgress = 1,
        Failed = 2,
        Loaded = 3,
    }

    public static T? Load<T>(string path, string? typeHint = null, CacheMode cacheMode = CacheMode.Reuse) where T : class => null;
    public static bool Exists(string path) => false;
    public static bool Exists(string path, string typeHint) => false;
    public static void AddResourceFormatLoader(ResourceFormatLoader formatLoader, bool atFront = false) { }
    // Nothing is loadable headless: threaded requests fail the same way Load returns null.
    public static Error LoadThreadedRequest(string path, string typeHint = "", bool useSubThreads = false, CacheMode cacheMode = CacheMode.Reuse) => Error.FileNotFound;
    public static ThreadLoadStatus LoadThreadedGetStatus(string path, Godot.Collections.Array? progress = null) => ThreadLoadStatus.InvalidResource;
    public static Resource? LoadThreadedGet(string path) => null;
}

public static class Time
{
    public static ulong GetTicksMsec() => (ulong)Environment.TickCount64;
}

public class Window : Node
{
    public new class SignalName : Node.SignalName
    {
        public static readonly StringName SizeChanged = "SizeChanged";
    }
}

public class Viewport : Node
{
    public new class SignalName : Node.SignalName
    {
        public static readonly StringName GuiFocusChanged = "GuiFocusChanged";
    }

    public Vector2 GetMousePosition() => Vector2.Zero;
    // Nothing holds focus headless (Control.HasFocus is always false).
    public Control GuiGetFocusOwner() => null!;
    public void GuiReleaseFocus() { }
    public Rect2 GetVisibleRect() => new Rect2(0, 0, 1920, 1080);
}
