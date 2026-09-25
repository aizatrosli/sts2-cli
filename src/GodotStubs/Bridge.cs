namespace Godot.Bridge;

public static class ScriptManagerBridge
{
    public static void FrameworkGetGodotMethodList(IntPtr handle) { }
}

public static class CSharpInstanceBridge { }

// Types used by source-generated bridge code (GetGodotPropertyList, SaveGodotObjectData, ...).
// The engine is what calls that code, so headless only needs them to exist.

public struct PropertyInfo
{
    public Variant.Type Type { get; init; }
    public StringName Name { get; init; }
    public PropertyHint Hint { get; init; }
    public string HintString { get; init; }
    public PropertyUsageFlags Usage { get; init; }
    public StringName? ClassName { get; init; }
    public bool Exported { get; init; }

    public PropertyInfo(Variant.Type type, StringName name, PropertyHint hint, string hintString, PropertyUsageFlags usage, bool exported)
        : this(type, name, hint, hintString, usage, className: null, exported) { }

    public PropertyInfo(Variant.Type type, StringName name, PropertyHint hint, string hintString, PropertyUsageFlags usage, StringName? className, bool exported)
    {
        Type = type; Name = name; Hint = hint; HintString = hintString; Usage = usage; ClassName = className; Exported = exported;
    }
}

public struct MethodInfo
{
    public StringName Name { get; init; }
    public PropertyInfo ReturnVal { get; init; }
    public MethodFlags Flags { get; init; }
    public int Id { get; init; }
    public List<PropertyInfo>? Arguments { get; init; }
    public List<Variant>? DefaultArguments { get; init; }

    public MethodInfo(StringName name, PropertyInfo returnVal, MethodFlags flags, List<PropertyInfo>? arguments, List<Variant>? defaultArguments)
    {
        Name = name; ReturnVal = returnVal; Flags = flags; Id = 0; Arguments = arguments; DefaultArguments = defaultArguments;
    }
}

public sealed class GodotSerializationInfo : IDisposable
{
    private readonly Dictionary<StringName, Variant> _properties = new();
    private readonly Dictionary<StringName, Delegate> _signalEvents = new();

    public void AddProperty(StringName name, Variant value) => _properties[name] = value;
    public bool TryGetProperty(StringName name, out Variant value) => _properties.TryGetValue(name, out value);
    public void AddSignalEventDelegate(StringName name, Delegate eventDelegate) => _signalEvents[name] = eventDelegate;
    public bool TryGetSignalEventDelegate<T>(StringName name, out T? value) where T : Delegate
    {
        value = _signalEvents.TryGetValue(name, out var d) ? d as T : null;
        return value != null;
    }
    public void Dispose() { }
}
