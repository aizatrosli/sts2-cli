// Fixture "sts2.dll" for GodotStubAudit's reachable-ui tier. Each Missing.* member is used from
// one shape of call path; tests/test_godot_stubs.py asserts the tier the audit gives it.
using Godot;

namespace MegaCrit.Sts2.Core.Models
{
    using MegaCrit.Sts2.Core.Nodes;

    public class Card
    {
        public void OnPlay(NBase node, INode iface)
        {
            Missing.InGameplay();            // gameplay
            NVfx.Play();                     // 1 edge
            NChain.A();                      // 1 edge → B (2) → C (3)
            node.Show();                     // virtual: overrides at 1 edge
            iface.Ping();                    // interface: implementations at 1 edge
            _ = NAsync.RunAsync();           // async body in MoveNext, still 1 edge
            NLambda.Make();                  // lambda body at 2 edges
            NHolder.Touch();                 // NHolder has a MissingType field
            NGeneric<int>.Do();              // method on a generic type instantiation
            NStatic.Poke();                  // runs NStatic's static constructor
            NPresent.Fine();                 // uses only members the stub has
        }
    }
}

namespace MegaCrit.Sts2.Core.Nodes
{
    public static class NVfx
    {
        public static void Play() => Missing.OneHop();
    }

    public static class NChain
    {
        public static void A() => B();
        public static void B()
        {
            Missing.TwoHop();
            C();
        }
        public static void C() => Missing.ThreeHop();
    }

    public class NBase
    {
        public virtual void Show() { }
    }

    public class NDerived : NBase
    {
        public override void Show() => Missing.ViaOverride();
    }

    public class NMore : NDerived
    {
        public override void Show() => Missing.ViaOverrideOfOverride();
    }

    public interface INode
    {
        void Ping();
    }

    public class NImpl : INode
    {
        public void Ping() => Missing.ViaInterface();
    }

    public class NExplicit : INode
    {
        void INode.Ping() => Missing.ViaExplicitInterface();
    }

    public static class NAsync
    {
        public static async System.Threading.Tasks.Task RunAsync()
        {
            await System.Threading.Tasks.Task.Yield();
            Missing.InAsync();
        }
    }

    public static class NLambda
    {
        public static System.Action Make() => () => Missing.InLambda();
    }

    public class NHolder
    {
        public MissingType? Field;
        public static void Touch() { }
    }

    public static class NGeneric<T>
    {
        public static void Do() => Missing.InGeneric();
    }

    public static class NStatic
    {
        static NStatic() => Missing.InCctor();
        public static void Poke() { }
    }

    public static class NPresent
    {
        public static void Fine() => Present.Ok();
    }

    public static class NOrphan
    {
        public static void Never() => Missing.Orphan();
    }
}

namespace MegaCrit.Sts2.Core.DevConsole
{
    public static class Cmd
    {
        public static void Run() => Missing.ToolOnly();
    }
}
