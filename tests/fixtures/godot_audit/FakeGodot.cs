// A stand-in GodotSharp for the GodotStubAudit fixture. Built twice: Real/ (everything) is
// what FixtureGame compiles against, Stub/ (STUB defined) lacks the members the audit must
// report, the way src/GodotStubs lacks members of the real GodotSharp.
namespace Godot;

public static class Present
{
    public static void Ok() { }
}

public static class Missing
{
#if !STUB
    public static void InGameplay() { }
    public static void OneHop() { }
    public static void TwoHop() { }
    public static void ThreeHop() { }
    public static void ViaOverride() { }
    public static void ViaOverrideOfOverride() { }
    public static void ViaInterface() { }
    public static void ViaExplicitInterface() { }
    public static void InAsync() { }
    public static void InLambda() { }
    public static void InGeneric() { }
    public static void InCctor() { }
    public static void Orphan() { }
    public static void ToolOnly() { }
#endif
}

#if !STUB
public class MissingType { }
#endif
