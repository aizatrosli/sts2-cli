namespace Godot.Collections;

// Godot Array<T> wrapper
public partial class Array<T> : List<T>
{
    public Array() { }
    public Array(IEnumerable<T> items) : base(items) { }
    // Godot's Array<T>.GetEnumerator returns IEnumerator<T> (List<T> returns a struct enumerator).
    public new IEnumerator<T> GetEnumerator() => base.GetEnumerator();
}

// Godot Dictionary
public class Dictionary<TKey, TValue> : System.Collections.Generic.Dictionary<TKey, TValue>
    where TKey : notnull
{
    public Dictionary() { }
}

// Non-generic Array
public class Array : List<Variant>
{
    public Array() { }
}

// Non-generic Dictionary (Variant keys and values), e.g. OS.GetMemoryInfo, CharFXTransform.Env.
public class Dictionary : System.Collections.Generic.Dictionary<Variant, Variant>
{
    public Dictionary() { }
    // Godot's Dictionary.GetEnumerator returns an interface enumerator.
    public new IEnumerator<KeyValuePair<Variant, Variant>> GetEnumerator() => base.GetEnumerator();
}
