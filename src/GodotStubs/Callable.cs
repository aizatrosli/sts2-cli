using System.Reflection;

namespace Godot;

/// <summary>
/// A delegate, or a method name on an object. Call invokes it with Variant arguments
/// converted to the delegate's parameter types. Headless has no idle frame, so
/// CallDeferred runs now and reports (rather than throws) failures, like Godot.
/// </summary>
public readonly struct Callable : IEquatable<Callable>
{
    private readonly Delegate? _delegate;
    private readonly GodotObject? _target;
    private readonly StringName? _method;

    // Not in GodotSharp (it is internal there); kept for existing stub users.
    public Callable(Delegate? d) { _delegate = d; _target = null; _method = null; }

    public Callable(GodotObject target, StringName method) { _delegate = null; _target = target; _method = method; }

    public Delegate? Delegate => _delegate;
    public GodotObject? Target => _target ?? _delegate?.Target as GodotObject;
    public StringName? Method => _method ?? (_delegate != null ? new StringName(_delegate.Method.Name) : null);

    public static Callable From(Action action) => new(action);
    public static Callable From<T0>(Action<T0> action) => new(action);
    public static Callable From<T0, T1>(Action<T0, T1> action) => new(action);
    public static Callable From<T0, T1, T2>(Action<T0, T1, T2> action) => new(action);
    public static Callable From<T0, T1, T2, T3>(Action<T0, T1, T2, T3> action) => new(action);
    public static Callable From<T0, T1, T2, T3, T4>(Action<T0, T1, T2, T3, T4> action) => new(action);
    public static Callable From<TResult>(Func<TResult> func) => new(func);
    public static Callable From<T0, TResult>(Func<T0, TResult> func) => new(func);
    public static Callable From<T0, T1, TResult>(Func<T0, T1, TResult> func) => new(func);
    public static Callable From<T0, T1, T2, TResult>(Func<T0, T1, T2, TResult> func) => new(func);

    public Variant Call(params Variant[] args)
    {
        if (_delegate != null)
        {
            var ps = _delegate.GetType().GetMethod("Invoke")!.GetParameters();
            var values = new object?[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                values[i] = i < args.Length
                    ? args[i].ConvertTo(ps[i].ParameterType)
                    : ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
            }
            try
            {
                return Variant.FromObject(_delegate.DynamicInvoke(values));
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
        return _target != null && _method != null ? _target.Call(_method, args) : default;
    }

    public Variant Callv(Godot.Collections.Array args) => Call(args.ToArray());

    public void CallDeferred(params Variant[] args)
    {
        try
        {
            Call(args);
        }
        catch (Exception e)
        {
            GD.PushError($"Callable.CallDeferred {Method}: {e.Message}");
        }
    }

    public bool IsValid => _delegate != null || (_target != null && _method != null);

    public static bool operator ==(Callable left, Callable right) => left.Equals(right);
    public static bool operator !=(Callable left, Callable right) => !left.Equals(right);
    public override bool Equals(object? obj) => obj is Callable other && Equals(other);
    public bool Equals(Callable other) =>
        Equals(_delegate, other._delegate) && ReferenceEquals(_target, other._target) && Equals(_method, other._method);
    public override int GetHashCode() => HashCode.Combine(_delegate, _target, _method);
}
