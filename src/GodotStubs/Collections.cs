namespace Godot.Collections;

// Godot Array<T> wrapper
public class Array<T> : List<T>
{
    public Array() { }
    public Array(IEnumerable<T> items) : base(items) { }

    // GodotSharp's Array<T> enumerates through IEnumerator<T>; sts2 binds to that
    // signature, not List<T>'s struct enumerator.
    public new IEnumerator<T> GetEnumerator() => base.GetEnumerator();

    public static explicit operator Array<T>(Variant from) => from.AsGodotArray<T>();
    public static implicit operator Variant(Array<T> from) => Variant.CreateFrom(from);
}

// Godot Dictionary
public class Dictionary<TKey, TValue> : System.Collections.Generic.Dictionary<TKey, TValue>
    where TKey : notnull
{
    public Dictionary() { }
    public Dictionary(IDictionary<TKey, TValue> dictionary) : base(dictionary) { }
}

// Non-generic Dictionary (Variant → Variant)
public class Dictionary : System.Collections.Generic.Dictionary<Variant, Variant>
{
    public Dictionary() { }
    public Dictionary(IDictionary<Variant, Variant> dictionary) : base(dictionary) { }

    // Same as Array<T>: sts2 binds to the IEnumerator<> signature.
    public new IEnumerator<KeyValuePair<Variant, Variant>> GetEnumerator() => base.GetEnumerator();
}

// Non-generic Array
public class Array : List<Variant>
{
    public Array() { }
    public Array(IEnumerable<Variant> items) : base(items) { }
}
