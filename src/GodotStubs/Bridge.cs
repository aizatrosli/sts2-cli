namespace Godot.Bridge;

// Shapes match GodotSharp 4.5 (Godot.Bridge): game classes override
// SaveGodotObjectData(Godot.Bridge.GodotSerializationInfo) and build these in generated code.
public readonly struct PropertyInfo
{
    public Variant.Type Type { get; init; }
    public StringName Name { get; init; }
    public PropertyHint Hint { get; init; }
    public string HintString { get; init; }
    public PropertyUsageFlags Usage { get; init; }
    public StringName? ClassName { get; init; }
    public bool Exported { get; init; }

    public PropertyInfo(Variant.Type type, StringName name, PropertyHint hint, string hintString, PropertyUsageFlags usage, bool exported)
        : this(type, name, hint, hintString, usage, null, exported) { }

    public PropertyInfo(Variant.Type type, StringName name, PropertyHint hint, string hintString, PropertyUsageFlags usage, StringName? className, bool exported)
    {
        Type = type; Name = name; Hint = hint; HintString = hintString; Usage = usage; ClassName = className; Exported = exported;
    }
}

public readonly struct MethodInfo
{
    public StringName Name { get; init; }
    public PropertyInfo ReturnVal { get; init; }
    public MethodFlags Flags { get; init; }
    public int Id { get; init; }
    public List<PropertyInfo>? Arguments { get; init; }
    public List<Variant>? DefaultArguments { get; init; }

    public MethodInfo(StringName name, PropertyInfo returnVal, MethodFlags flags, List<PropertyInfo>? arguments, List<Variant>? defaultArguments)
    {
        Name = name; ReturnVal = returnVal; Flags = flags; Arguments = arguments; DefaultArguments = defaultArguments;
    }
}

public sealed class GodotSerializationInfo : IDisposable
{
    private readonly Dictionary<string, Variant> _properties = new();
    private readonly Dictionary<string, Delegate> _signals = new();
    public void Dispose() { }
    public void AddProperty(StringName name, Variant value) => _properties[name] = value;
    public bool TryGetProperty(StringName name, out Variant value) => _properties.TryGetValue(name, out value);
    public void AddSignalEventDelegate(StringName name, Delegate eventDelegate) => _signals[name] = eventDelegate;
    public bool TryGetSignalEventDelegate<T>(StringName name, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T value) where T : Delegate
    {
        if (_signals.TryGetValue(name, out var d) && d is T t) { value = t; return true; }
        value = null; return false;
    }
}

public static class ScriptManagerBridge
{
    public static void FrameworkGetGodotMethodList(IntPtr handle) { }
}

public static class CSharpInstanceBridge { }
