namespace Godot.NativeInterop;

using Godot;

// Used by source-generated bridge code, which only the engine calls. Headless has no
// native variants, so conversions produce defaults.
public static class VariantUtils
{
    public static T ConvertTo<[MustBeVariant] T>(in godot_variant variant) => default!;
    public static godot_variant CreateFrom<[MustBeVariant] T>(in T from) => default;
}

// Low-level native interop types used in generated bridge code
public struct godot_string_name { }
public struct godot_variant { }
public enum godot_bool : byte
{
    True = 1,
    False = 0,
}
public struct NativeGodotVariant { }
public struct NativeGodotString { }
public struct NativeGodotStringName { }

// NativeVariantPtrArgs - used in signal dispatch bridge
public readonly struct NativeVariantPtrArgs
{
    public readonly ref godot_variant this[int index] => ref System.Runtime.CompilerServices.Unsafe.NullRef<godot_variant>();
    public int Count => 0;
}
