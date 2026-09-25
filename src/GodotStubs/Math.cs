using System.Globalization;

namespace Godot;

// Value types with the same semantics as GodotSharp 4.5 (formulas follow Godot's
// MIT-licensed C# core). Game logic does real math with these (map/grid layout,
// Crystal Sphere, monster positioning), so they must compute, not no-op.
// tools/GodotStubDiff compares them against the real GodotSharp.dll with random inputs.

public struct Vector2 : IEquatable<Vector2>
{
    public enum Axis { X, Y }

    public float X;
    public float Y;

    public static Vector2 Zero => new(0, 0);
    public static Vector2 One => new(1, 1);
    public static Vector2 Inf => new(float.PositiveInfinity, float.PositiveInfinity);
    public static Vector2 Up => new(0, -1);
    public static Vector2 Down => new(0, 1);
    public static Vector2 Left => new(-1, 0);
    public static Vector2 Right => new(1, 0);

    public Vector2(float x, float y) { X = x; Y = y; }

    public float this[int index]
    {
        readonly get => index switch { 0 => X, 1 => Y, _ => throw new ArgumentOutOfRangeException(nameof(index)) };
        set { switch (index) { case 0: X = value; break; case 1: Y = value; break; default: throw new ArgumentOutOfRangeException(nameof(index)); } }
    }

    public readonly void Deconstruct(out float x, out float y) { x = X; y = Y; }

    public readonly Vector2 Abs() => new(MathF.Abs(X), MathF.Abs(Y));
    public readonly float Angle() => MathF.Atan2(Y, X);
    public readonly float AngleTo(Vector2 to) => MathF.Atan2(Cross(to), Dot(to));
    public readonly float AngleToPoint(Vector2 to) => MathF.Atan2(to.Y - Y, to.X - X);
    public readonly float Aspect() => X / Y;
    public readonly Vector2 BezierInterpolate(Vector2 control1, Vector2 control2, Vector2 end, float t) => new(
        Mathf.BezierInterpolate(X, control1.X, control2.X, end.X, t),
        Mathf.BezierInterpolate(Y, control1.Y, control2.Y, end.Y, t));
    public readonly Vector2 BezierDerivative(Vector2 control1, Vector2 control2, Vector2 end, float t) => new(
        Mathf.BezierDerivative(X, control1.X, control2.X, end.X, t),
        Mathf.BezierDerivative(Y, control1.Y, control2.Y, end.Y, t));
    public readonly Vector2 Bounce(Vector2 normal) => -Reflect(normal);
    public readonly Vector2 Ceil() => new(MathF.Ceiling(X), MathF.Ceiling(Y));
    public readonly Vector2 Clamp(Vector2 min, Vector2 max) => new(Mathf.Clamp(X, min.X, max.X), Mathf.Clamp(Y, min.Y, max.Y));
    public readonly Vector2 Clamp(float min, float max) => new(Mathf.Clamp(X, min, max), Mathf.Clamp(Y, min, max));
    public readonly float Cross(Vector2 with) => X * with.Y - Y * with.X;
    public readonly Vector2 CubicInterpolate(Vector2 b, Vector2 preA, Vector2 postB, float weight) => new(
        Mathf.CubicInterpolate(X, b.X, preA.X, postB.X, weight),
        Mathf.CubicInterpolate(Y, b.Y, preA.Y, postB.Y, weight));
    public readonly Vector2 DirectionTo(Vector2 to) => new Vector2(to.X - X, to.Y - Y).Normalized();
    public readonly float DistanceSquaredTo(Vector2 to) => (X - to.X) * (X - to.X) + (Y - to.Y) * (Y - to.Y);
    public readonly float DistanceTo(Vector2 to) => MathF.Sqrt(DistanceSquaredTo(to));
    public readonly float Dot(Vector2 with) => X * with.X + Y * with.Y;
    public readonly Vector2 Floor() => new(MathF.Floor(X), MathF.Floor(Y));
    public readonly Vector2 Inverse() => new(1 / X, 1 / Y);
    public readonly bool IsFinite() => float.IsFinite(X) && float.IsFinite(Y);
    public readonly bool IsNormalized() => MathF.Abs(LengthSquared() - 1.0f) < Mathf.Epsilon;
    public readonly float Length() => MathF.Sqrt(X * X + Y * Y);
    public readonly float LengthSquared() => X * X + Y * Y;
    public readonly Vector2 Lerp(Vector2 to, float weight) => new(Mathf.Lerp(X, to.X, weight), Mathf.Lerp(Y, to.Y, weight));
    public readonly Vector2 Slerp(Vector2 to, float weight)
    {
        float startLengthSquared = LengthSquared();
        float endLengthSquared = to.LengthSquared();
        // Zero length vectors have no angle: lerp instead.
        if (startLengthSquared == 0.0f || endLengthSquared == 0.0f) return Lerp(to, weight);
        float startLength = MathF.Sqrt(startLengthSquared);
        float resultLength = Mathf.Lerp(startLength, MathF.Sqrt(endLengthSquared), weight);
        return Rotated(AngleTo(to) * weight) * (resultLength / startLength);
    }
    public readonly Vector2 LimitLength(float length = 1.0f)
    {
        Vector2 v = this;
        float l = Length();
        if (l > 0 && length < l)
        {
            v /= l;
            v *= length;
        }
        return v;
    }
    public readonly Vector2 Max(Vector2 with) => new(Mathf.Max(X, with.X), Mathf.Max(Y, with.Y));
    public readonly Vector2 Max(float with) => new(Mathf.Max(X, with), Mathf.Max(Y, with));
    public readonly Vector2 Min(Vector2 with) => new(Mathf.Min(X, with.X), Mathf.Min(Y, with.Y));
    public readonly Vector2 Min(float with) => new(Mathf.Min(X, with), Mathf.Min(Y, with));
    public readonly Axis MaxAxisIndex() => X < Y ? Axis.Y : Axis.X;
    public readonly Axis MinAxisIndex() => X < Y ? Axis.X : Axis.Y;
    public readonly Vector2 MoveToward(Vector2 to, float delta)
    {
        Vector2 v = this;
        Vector2 vd = to - v;
        float len = vd.Length();
        if (len <= delta || len < Mathf.Epsilon) return to;
        return v + vd / len * delta;
    }
    public readonly Vector2 Normalized()
    {
        Vector2 v = this;
        float lengthsq = LengthSquared();
        if (lengthsq == 0)
        {
            v.X = v.Y = 0f;
        }
        else
        {
            float length = MathF.Sqrt(lengthsq);
            v.X /= length;
            v.Y /= length;
        }
        return v;
    }
    public readonly Vector2 Orthogonal() => new(Y, -X);
    public readonly Vector2 PosMod(float mod) => new(Mathf.PosMod(X, mod), Mathf.PosMod(Y, mod));
    public readonly Vector2 PosMod(Vector2 modv) => new(Mathf.PosMod(X, modv.X), Mathf.PosMod(Y, modv.Y));
    public readonly Vector2 Project(Vector2 onNormal) => onNormal * (Dot(onNormal) / onNormal.LengthSquared());
    public readonly Vector2 Reflect(Vector2 line) => 2.0f * line * Dot(line) - this;
    public readonly Vector2 Rotated(float angle)
    {
        (float sin, float cos) = MathF.SinCos(angle);
        return new Vector2(X * cos - Y * sin, X * sin + Y * cos);
    }
    public readonly Vector2 Round() => new(MathF.Round(X), MathF.Round(Y));
    public readonly Vector2 Sign() => new(MathF.Sign(X), MathF.Sign(Y));
    public readonly Vector2 Slide(Vector2 normal) => this - normal * Dot(normal);
    public readonly Vector2 Snapped(Vector2 step) => new(Mathf.Snapped(X, step.X), Mathf.Snapped(Y, step.Y));
    public readonly Vector2 Snapped(float step) => new(Mathf.Snapped(X, step), Mathf.Snapped(Y, step));
    public static Vector2 FromAngle(float angle)
    {
        (float sin, float cos) = MathF.SinCos(angle);
        return new Vector2(cos, sin);
    }
    public readonly bool IsEqualApprox(Vector2 other) => Mathf.IsEqualApprox(X, other.X) && Mathf.IsEqualApprox(Y, other.Y);
    public readonly bool IsZeroApprox() => Mathf.IsZeroApprox(X) && Mathf.IsZeroApprox(Y);

    public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vector2 operator -(Vector2 v) => new(-v.X, -v.Y);
    public static Vector2 operator *(Vector2 a, float b) => new(a.X * b, a.Y * b);
    public static Vector2 operator *(float a, Vector2 b) => new(a * b.X, a * b.Y);
    public static Vector2 operator *(Vector2 a, Vector2 b) => new(a.X * b.X, a.Y * b.Y);
    public static Vector2 operator /(Vector2 a, float b) => new(a.X / b, a.Y / b);
    public static Vector2 operator /(Vector2 a, Vector2 b) => new(a.X / b.X, a.Y / b.Y);
    // Not in GodotSharp; kept for binary compatibility with older stub users.
    public static Vector2 operator /(Vector2 a, int b) => new(a.X / b, a.Y / b);
    public static Vector2 operator %(Vector2 a, float b) => new(a.X % b, a.Y % b);
    public static Vector2 operator %(Vector2 a, Vector2 b) => new(a.X % b.X, a.Y % b.Y);
    public static bool operator ==(Vector2 a, Vector2 b) => a.Equals(b);
    public static bool operator !=(Vector2 a, Vector2 b) => !a.Equals(b);
    public static bool operator <(Vector2 a, Vector2 b) => a.X == b.X ? a.Y < b.Y : a.X < b.X;
    public static bool operator >(Vector2 a, Vector2 b) => a.X == b.X ? a.Y > b.Y : a.X > b.X;
    public static bool operator <=(Vector2 a, Vector2 b) => a.X == b.X ? a.Y <= b.Y : a.X < b.X;
    public static bool operator >=(Vector2 a, Vector2 b) => a.X == b.X ? a.Y >= b.Y : a.X > b.X;

    public override readonly bool Equals(object? obj) => obj is Vector2 other && Equals(other);
    public readonly bool Equals(Vector2 other) => X == other.X && Y == other.Y;
    public override readonly int GetHashCode() => HashCode.Combine(X, Y);
    public override readonly string ToString() => ToString(null);
    public readonly string ToString(string? format) =>
        $"({X.ToString(format, CultureInfo.InvariantCulture)}, {Y.ToString(format, CultureInfo.InvariantCulture)})";
}

public struct Vector2I : IEquatable<Vector2I>
{
    public enum Axis { X, Y }

    public int X;
    public int Y;

    public Vector2I(int x, int y) { X = x; Y = y; }

    public static Vector2I MinValue => new(int.MinValue, int.MinValue);
    public static Vector2I MaxValue => new(int.MaxValue, int.MaxValue);
    public static Vector2I Zero => new(0, 0);
    public static Vector2I One => new(1, 1);
    public static Vector2I Up => new(0, -1);
    public static Vector2I Down => new(0, 1);
    public static Vector2I Left => new(-1, 0);
    public static Vector2I Right => new(1, 0);

    public int this[int index]
    {
        readonly get => index switch { 0 => X, 1 => Y, _ => throw new ArgumentOutOfRangeException(nameof(index)) };
        set { switch (index) { case 0: X = value; break; case 1: Y = value; break; default: throw new ArgumentOutOfRangeException(nameof(index)); } }
    }

    public readonly void Deconstruct(out int x, out int y) { x = X; y = Y; }

    public readonly Vector2I Abs() => new(Math.Abs(X), Math.Abs(Y));
    public readonly float Aspect() => X / (float)Y;
    public readonly Vector2I Clamp(Vector2I min, Vector2I max) => new(Math.Clamp(X, min.X, max.X), Math.Clamp(Y, min.Y, max.Y));
    public readonly Vector2I Clamp(int min, int max) => new(Math.Clamp(X, min, max), Math.Clamp(Y, min, max));
    public readonly int DistanceSquaredTo(Vector2I to) => (to - this).LengthSquared();
    public readonly float DistanceTo(Vector2I to) => (to - this).Length();
    public readonly float Length()
    {
        int x2 = X * X;
        int y2 = Y * Y;
        return MathF.Sqrt(x2 + y2);
    }
    public readonly int LengthSquared()
    {
        int x2 = X * X;
        int y2 = Y * Y;
        return x2 + y2;
    }
    public readonly Vector2I Max(Vector2I with) => new(Math.Max(X, with.X), Math.Max(Y, with.Y));
    public readonly Vector2I Max(int with) => new(Math.Max(X, with), Math.Max(Y, with));
    public readonly Vector2I Min(Vector2I with) => new(Math.Min(X, with.X), Math.Min(Y, with.Y));
    public readonly Vector2I Min(int with) => new(Math.Min(X, with), Math.Min(Y, with));
    public readonly Axis MaxAxisIndex() => X < Y ? Axis.Y : Axis.X;
    public readonly Axis MinAxisIndex() => X < Y ? Axis.X : Axis.Y;
    public readonly Vector2I Sign() => new(Math.Sign(X), Math.Sign(Y));
    public readonly Vector2I Snapped(Vector2I step) => new((int)Mathf.Snapped((double)X, step.X), (int)Mathf.Snapped((double)Y, step.Y));
    public readonly Vector2I Snapped(int step) => new((int)Mathf.Snapped((double)X, step), (int)Mathf.Snapped((double)Y, step));

    public static implicit operator Vector2(Vector2I v) => new(v.X, v.Y);
    public static explicit operator Vector2I(Vector2 v) => new((int)v.X, (int)v.Y);

    // Integer grid math used by game logic (e.g. the Crystal Sphere event grid).
    public static Vector2I operator +(Vector2I a, Vector2I b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2I operator -(Vector2I a, Vector2I b) => new(a.X - b.X, a.Y - b.Y);
    public static Vector2I operator -(Vector2I v) => new(-v.X, -v.Y);
    public static Vector2I operator *(Vector2I a, Vector2I b) => new(a.X * b.X, a.Y * b.Y);
    public static Vector2I operator *(Vector2I a, int s) => new(a.X * s, a.Y * s);
    public static Vector2I operator *(int s, Vector2I a) => new(a.X * s, a.Y * s);
    public static Vector2I operator /(Vector2I a, Vector2I b) => new(a.X / b.X, a.Y / b.Y);
    public static Vector2I operator /(Vector2I a, int s) => new(a.X / s, a.Y / s);
    public static Vector2I operator %(Vector2I a, Vector2I b) => new(a.X % b.X, a.Y % b.Y);
    public static Vector2I operator %(Vector2I a, int s) => new(a.X % s, a.Y % s);
    public static bool operator ==(Vector2I a, Vector2I b) => a.Equals(b);
    public static bool operator !=(Vector2I a, Vector2I b) => !a.Equals(b);
    // Godot orders vectors by X, then Y.
    public static bool operator <(Vector2I a, Vector2I b) => a.X == b.X ? a.Y < b.Y : a.X < b.X;
    public static bool operator >(Vector2I a, Vector2I b) => a.X == b.X ? a.Y > b.Y : a.X > b.X;
    public static bool operator <=(Vector2I a, Vector2I b) => a.X == b.X ? a.Y <= b.Y : a.X < b.X;
    public static bool operator >=(Vector2I a, Vector2I b) => a.X == b.X ? a.Y >= b.Y : a.X > b.X;

    public override readonly bool Equals(object? obj) => obj is Vector2I other && Equals(other);
    public readonly bool Equals(Vector2I other) => X == other.X && Y == other.Y;
    public override readonly int GetHashCode() => HashCode.Combine(X, Y);
    public override readonly string ToString() => $"({X}, {Y})";
}

public struct Vector3 : IEquatable<Vector3>
{
    public float X, Y, Z;

    public static Vector3 Zero => new(0, 0, 0);
    public static Vector3 One => new(1, 1, 1);
    public static Vector3 Up => new(0, 1, 0);
    public static Vector3 Down => new(0, -1, 0);
    public static Vector3 Right => new(1, 0, 0);
    public static Vector3 Left => new(-1, 0, 0);
    public static Vector3 Forward => new(0, 0, -1);
    public static Vector3 Back => new(0, 0, 1);

    public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }

    public readonly Vector3 Cross(Vector3 with) => new(Y * with.Z - Z * with.Y, Z * with.X - X * with.Z, X * with.Y - Y * with.X);
    public readonly float Dot(Vector3 with) => X * with.X + Y * with.Y + Z * with.Z;
    public readonly float Length() => MathF.Sqrt(LengthSquared());
    public readonly float LengthSquared() => X * X + Y * Y + Z * Z;
    public readonly Vector3 Lerp(Vector3 to, float weight) =>
        new(Mathf.Lerp(X, to.X, weight), Mathf.Lerp(Y, to.Y, weight), Mathf.Lerp(Z, to.Z, weight));
    public readonly Vector3 Normalized()
    {
        float lengthsq = LengthSquared();
        if (lengthsq == 0) return Zero;
        float length = MathF.Sqrt(lengthsq);
        return new Vector3(X / length, Y / length, Z / length);
    }

    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3 operator -(Vector3 v) => new(-v.X, -v.Y, -v.Z);
    public static Vector3 operator *(Vector3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vector3 operator *(float s, Vector3 a) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vector3 operator *(Vector3 a, Vector3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    public static Vector3 operator /(Vector3 a, float s) => new(a.X / s, a.Y / s, a.Z / s);
    public static bool operator ==(Vector3 a, Vector3 b) => a.Equals(b);
    public static bool operator !=(Vector3 a, Vector3 b) => !a.Equals(b);

    public override readonly bool Equals(object? obj) => obj is Vector3 other && Equals(other);
    public readonly bool Equals(Vector3 other) => X == other.X && Y == other.Y && Z == other.Z;
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z);
    public override readonly string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X}, {Y}, {Z})");
}

public struct Quaternion : IEquatable<Quaternion>
{
    public float X, Y, Z, W;

    public static Quaternion Identity => new(0, 0, 0, 1);

    public Quaternion(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

    /// <summary>Euler angles in YXZ order, as Godot uses.</summary>
    public static Quaternion FromEuler(Vector3 eulerYXZ)
    {
        float halfA1 = eulerYXZ.Y * 0.5f;
        float halfA2 = eulerYXZ.X * 0.5f;
        float halfA3 = eulerYXZ.Z * 0.5f;

        (float sinA1, float cosA1) = MathF.SinCos(halfA1);
        (float sinA2, float cosA2) = MathF.SinCos(halfA2);
        (float sinA3, float cosA3) = MathF.SinCos(halfA3);

        return new Quaternion(
            sinA1 * cosA2 * sinA3 + cosA1 * sinA2 * cosA3,
            sinA1 * cosA2 * cosA3 - cosA1 * sinA2 * sinA3,
            -sinA1 * sinA2 * cosA3 + cosA1 * cosA2 * sinA3,
            sinA1 * sinA2 * sinA3 + cosA1 * cosA2 * cosA3);
    }

    public readonly float Dot(Quaternion b) => X * b.X + Y * b.Y + Z * b.Z + W * b.W;
    public readonly float Length() => MathF.Sqrt(LengthSquared());
    public readonly float LengthSquared() => Dot(this);
    public readonly Quaternion Normalized() => this / Length();
    public readonly Quaternion Inverse() => new(-X, -Y, -Z, W);

    public static Quaternion operator *(Quaternion left, Quaternion right) => new(
        left.W * right.X + left.X * right.W + left.Y * right.Z - left.Z * right.Y,
        left.W * right.Y + left.Y * right.W + left.Z * right.X - left.X * right.Z,
        left.W * right.Z + left.Z * right.W + left.X * right.Y - left.Y * right.X,
        left.W * right.W - left.X * right.X - left.Y * right.Y - left.Z * right.Z);

    public static Vector3 operator *(Quaternion quaternion, Vector3 vector)
    {
        var u = new Vector3(quaternion.X, quaternion.Y, quaternion.Z);
        Vector3 uv = u.Cross(vector);
        return vector + ((uv * quaternion.W) + u.Cross(uv)) * 2;
    }

    public static Quaternion operator /(Quaternion left, float right) => new(left.X / right, left.Y / right, left.Z / right, left.W / right);
    public static bool operator ==(Quaternion a, Quaternion b) => a.Equals(b);
    public static bool operator !=(Quaternion a, Quaternion b) => !a.Equals(b);

    public override readonly bool Equals(object? obj) => obj is Quaternion other && Equals(other);
    public readonly bool Equals(Quaternion other) => X == other.X && Y == other.Y && Z == other.Z && W == other.W;
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z, W);
    public override readonly string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X}, {Y}, {Z}, {W})");
}

public struct Color : IEquatable<Color>
{
    public float R, G, B, A;

    // Not in GodotSharp (it has Colors.*); kept for existing stub users.
    public static Color White => new(1, 1, 1, 1);
    public static Color Black => new(0, 0, 0, 1);
    public static Color Transparent => new(0, 0, 0, 0);

    public Color(float r, float g, float b, float a = 1f) { R = r; G = g; B = b; A = a; }
    // Godot's Color(Color, float) — recolor with a new alpha. Used by VFX helpers
    // (e.g. Liquid Memories' discard-pile selection); missing overload throws
    // MissingMethodException in headless (issue #72).
    public Color(Color c, float a = 1f) { R = c.R; G = c.G; B = c.B; A = a; }
    /// <summary>From a 32-bit RGBA value (0xRRGGBBAA).</summary>
    public Color(uint rgba)
    {
        A = (rgba & 0xFF) / 255.0f; rgba >>= 8;
        B = (rgba & 0xFF) / 255.0f; rgba >>= 8;
        G = (rgba & 0xFF) / 255.0f; rgba >>= 8;
        R = (rgba & 0xFF) / 255.0f;
    }
    /// <summary>From an HTML code ("#ff8800", "f80c") or a named color ("red", "dark_red").</summary>
    public Color(string code)
    {
        this = HtmlIsValid(code) ? FromHtml(code) : Named(code);
    }
    public Color(string code, float alpha) : this(code) { A = alpha; }

    public int R8 { readonly get => (int)MathF.Round(R * 255.0f); set => R = value / 255.0f; }
    public int G8 { readonly get => (int)MathF.Round(G * 255.0f); set => G = value / 255.0f; }
    public int B8 { readonly get => (int)MathF.Round(B * 255.0f); set => B = value / 255.0f; }
    public int A8 { readonly get => (int)MathF.Round(A * 255.0f); set => A = value / 255.0f; }

    public float H
    {
        readonly get
        {
            float max = Math.Max(R, Math.Max(G, B));
            float min = Math.Min(R, Math.Min(G, B));
            float delta = max - min;
            if (delta == 0) return 0;
            float h;
            if (R == max) h = (G - B) / delta;
            else if (G == max) h = 2 + (B - R) / delta;
            else h = 4 + (R - G) / delta;
            h /= 6.0f;
            if (h < 0) h += 1.0f;
            return h;
        }
        set => this = FromHsv(value, S, V, A);
    }

    public float S
    {
        readonly get
        {
            float max = Math.Max(R, Math.Max(G, B));
            float min = Math.Min(R, Math.Min(G, B));
            float delta = max - min;
            return max == 0 ? 0 : delta / max;
        }
        set => this = FromHsv(H, value, V, A);
    }

    public float V
    {
        readonly get => Math.Max(R, Math.Max(G, B));
        set => this = FromHsv(H, S, value, A);
    }

    public readonly float Luminance => 0.2126f * R + 0.7152f * G + 0.0722f * B;

    public readonly Color Blend(Color over)
    {
        Color res;
        float sa = 1.0f - over.A;
        res.A = A * sa + over.A;
        if (res.A == 0) return new Color(0, 0, 0, 0);
        res.R = (R * A * sa + over.R * over.A) / res.A;
        res.G = (G * A * sa + over.G * over.A) / res.A;
        res.B = (B * A * sa + over.B * over.A) / res.A;
        return res;
    }

    public readonly Color Clamp(Color? min = null, Color? max = null)
    {
        Color minimum = min ?? new Color(0, 0, 0, 0);
        Color maximum = max ?? new Color(1, 1, 1, 1);
        return new Color(
            Mathf.Clamp(R, minimum.R, maximum.R), Mathf.Clamp(G, minimum.G, maximum.G),
            Mathf.Clamp(B, minimum.B, maximum.B), Mathf.Clamp(A, minimum.A, maximum.A));
    }

    public readonly Color Darkened(float amount)
    {
        Color res = this;
        res.R *= 1.0f - amount;
        res.G *= 1.0f - amount;
        res.B *= 1.0f - amount;
        return res;
    }

    public readonly Color Lightened(float amount)
    {
        Color res = this;
        res.R += (1.0f - res.R) * amount;
        res.G += (1.0f - res.G) * amount;
        res.B += (1.0f - res.B) * amount;
        return res;
    }

    public readonly Color Inverted() => new(1.0f - R, 1.0f - G, 1.0f - B, A);

    public readonly Color Lerp(Color to, float weight) => new(
        Mathf.Lerp(R, to.R, weight), Mathf.Lerp(G, to.G, weight),
        Mathf.Lerp(B, to.B, weight), Mathf.Lerp(A, to.A, weight));

    public readonly bool IsEqualApprox(Color other) =>
        Mathf.IsEqualApprox(R, other.R) && Mathf.IsEqualApprox(G, other.G) &&
        Mathf.IsEqualApprox(B, other.B) && Mathf.IsEqualApprox(A, other.A);

    public readonly uint ToRgba32()
    {
        uint c = (byte)Math.Round(R * 255.0f);
        c <<= 8; c |= (byte)Math.Round(G * 255.0f);
        c <<= 8; c |= (byte)Math.Round(B * 255.0f);
        c <<= 8; c |= (byte)Math.Round(A * 255.0f);
        return c;
    }

    public readonly uint ToArgb32()
    {
        uint c = (byte)Math.Round(A * 255.0f);
        c <<= 8; c |= (byte)Math.Round(R * 255.0f);
        c <<= 8; c |= (byte)Math.Round(G * 255.0f);
        c <<= 8; c |= (byte)Math.Round(B * 255.0f);
        return c;
    }

    public readonly string ToHtml(bool includeAlpha = true)
    {
        string txt = ToHex32(R) + ToHex32(G) + ToHex32(B);
        if (includeAlpha) txt += ToHex32(A);
        return txt;
    }

    private static string ToHex32(float val)
    {
        byte b = (byte)Mathf.RoundToInt(Mathf.Clamp(val * 255, 0, 255));
        return b.ToString("x2", CultureInfo.InvariantCulture);
    }

    public static Color Color8(byte r8, byte g8, byte b8, byte a8 = 255) => new(r8 / 255f, g8 / 255f, b8 / 255f, a8 / 255f);

    public static Color FromHsv(float hue, float saturation, float value, float alpha = 1.0f)
    {
        if (saturation == 0) return new Color(value, value, value, alpha);

        hue *= 6.0f;
        hue %= 6.0f;
        int i = (int)hue;
        float f = hue - i;
        float p = value * (1 - saturation);
        float q = value * (1 - saturation * f);
        float t = value * (1 - saturation * (1 - f));

        return i switch
        {
            0 => new Color(value, t, p, alpha),
            1 => new Color(q, value, p, alpha),
            2 => new Color(p, value, t, alpha),
            3 => new Color(p, q, value, alpha),
            4 => new Color(t, p, value, alpha),
            _ => new Color(value, p, q, alpha),
        };
    }

    public static bool HtmlIsValid(ReadOnlySpan<char> color)
    {
        if (color.IsEmpty) return false;
        if (color[0] == '#') color = color.Slice(1);
        int len = color.Length;
        if (!(len == 3 || len == 4 || len == 6 || len == 8)) return false;
        foreach (char c in color)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }

    public static Color FromHtml(ReadOnlySpan<char> rgba)
    {
        Color c;
        if (rgba.Length == 0)
        {
            c.R = 0f; c.G = 0f; c.B = 0f; c.A = 1.0f;
            return c;
        }
        if (rgba[0] == '#') rgba = rgba.Slice(1);

        bool alpha;
        bool isShorthand;
        if (rgba.Length == 8) { alpha = true; isShorthand = false; }
        else if (rgba.Length == 6) { alpha = false; isShorthand = false; }
        else if (rgba.Length == 4) { alpha = true; isShorthand = true; }
        else if (rgba.Length == 3) { alpha = false; isShorthand = true; }
        else throw new ArgumentOutOfRangeException(nameof(rgba), $"Invalid color code. Length is {rgba.Length}, but a length of 6 or 8 is expected.");

        c.A = 1.0f;
        if (isShorthand)
        {
            c.R = ParseCol4(rgba, 0) / 15f;
            c.G = ParseCol4(rgba, 1) / 15f;
            c.B = ParseCol4(rgba, 2) / 15f;
            if (alpha) c.A = ParseCol4(rgba, 3) / 15f;
        }
        else
        {
            c.R = ParseCol8(rgba, 0) / 255f;
            c.G = ParseCol8(rgba, 2) / 255f;
            c.B = ParseCol8(rgba, 4) / 255f;
            if (alpha) c.A = ParseCol8(rgba, 6) / 255f;
        }

        if (c.R < 0 || c.G < 0 || c.B < 0 || c.A < 0)
            throw new ArgumentOutOfRangeException(nameof(rgba), "Invalid color code.");
        return c;
    }

    private static int ParseCol4(ReadOnlySpan<char> str, int index)
    {
        char character = str[index];
        if (character >= '0' && character <= '9') return character - '0';
        if (character >= 'a' && character <= 'f') return character + (10 - 'a');
        if (character >= 'A' && character <= 'F') return character + (10 - 'A');
        return -1;
    }

    private static int ParseCol8(ReadOnlySpan<char> str, int index) =>
        ParseCol4(str, index) * 16 + ParseCol4(str, index + 1);

    public static Color FromString(string str, Color @default)
    {
        if (HtmlIsValid(str)) return FromHtml(str);
        return Colors.NamedColors.TryGetValue(NormalizeName(str), out var c) ? c : @default;
    }

    private static Color Named(string name) =>
        Colors.NamedColors.TryGetValue(NormalizeName(name), out var c)
            ? c
            : throw new ArgumentOutOfRangeException(nameof(name), $"Invalid Color Name: {name}");

    private static string NormalizeName(string name) =>
        name.Replace(" ", "").Replace("-", "").Replace("_", "").Replace("'", "").Replace(".", "").ToUpperInvariant();

    public static Color operator +(Color left, Color right) => new(left.R + right.R, left.G + right.G, left.B + right.B, left.A + right.A);
    public static Color operator -(Color left, Color right) => new(left.R - right.R, left.G - right.G, left.B - right.B, left.A - right.A);
    public static Color operator -(Color color) => Colors.White - color;
    public static Color operator *(Color color, float scale) => new(color.R * scale, color.G * scale, color.B * scale, color.A * scale);
    public static Color operator *(float scale, Color color) => color * scale;
    public static Color operator *(Color left, Color right) => new(left.R * right.R, left.G * right.G, left.B * right.B, left.A * right.A);
    public static Color operator /(Color color, float scale) => new(color.R / scale, color.G / scale, color.B / scale, color.A / scale);
    public static Color operator /(Color left, Color right) => new(left.R / right.R, left.G / right.G, left.B / right.B, left.A / right.A);
    public static bool operator ==(Color a, Color b) => a.Equals(b);
    public static bool operator !=(Color a, Color b) => !a.Equals(b);

    public override readonly bool Equals(object? obj) => obj is Color other && Equals(other);
    public readonly bool Equals(Color other) => R == other.R && G == other.G && B == other.B && A == other.A;
    public override readonly int GetHashCode() => HashCode.Combine(R, G, B, A);
    public override readonly string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({R}, {G}, {B}, {A})");
}

public struct Rect2 : IEquatable<Rect2>
{
    private Vector2 _position;
    private Vector2 _size;

    public Vector2 Position { readonly get => _position; set => _position = value; }
    public Vector2 Size { readonly get => _size; set => _size = value; }
    public Vector2 End { readonly get => _position + _size; set => _size = value - _position; }
    public readonly float Area => _size.X * _size.Y;

    // Not in GodotSharp; kept for existing stub users.
    public float X { readonly get => _position.X; set => _position = new Vector2(value, _position.Y); }
    public float Y { readonly get => _position.Y; set => _position = new Vector2(_position.X, value); }
    public float W { readonly get => _size.X; set => _size = new Vector2(value, _size.Y); }
    public float H { readonly get => _size.Y; set => _size = new Vector2(_size.X, value); }

    public Rect2(Vector2 position, Vector2 size) { _position = position; _size = size; }
    public Rect2(Vector2 position, float width, float height) { _position = position; _size = new Vector2(width, height); }
    public Rect2(float x, float y, Vector2 size) { _position = new Vector2(x, y); _size = size; }
    public Rect2(float x, float y, float width, float height) { _position = new Vector2(x, y); _size = new Vector2(width, height); }

    public readonly Rect2 Abs()
    {
        Vector2 end = End;
        var topLeft = new Vector2(Mathf.Min(_position.X, end.X), Mathf.Min(_position.Y, end.Y));
        return new Rect2(topLeft, _size.Abs());
    }

    public readonly Rect2 Intersection(Rect2 b)
    {
        Rect2 newRect = b;
        if (!Intersects(newRect)) return new Rect2();

        newRect._position.X = Mathf.Max(b._position.X, _position.X);
        newRect._position.Y = Mathf.Max(b._position.Y, _position.Y);

        Vector2 bEnd = b._position + b._size;
        Vector2 end = _position + _size;

        newRect._size.X = Mathf.Min(bEnd.X, end.X) - newRect._position.X;
        newRect._size.Y = Mathf.Min(bEnd.Y, end.Y) - newRect._position.Y;
        return newRect;
    }

    public readonly bool Encloses(Rect2 b) =>
        b._position.X >= _position.X && b._position.Y >= _position.Y &&
        b._position.X + b._size.X <= _position.X + _size.X &&
        b._position.Y + b._size.Y <= _position.Y + _size.Y;

    public readonly Rect2 Expand(Vector2 to)
    {
        Rect2 expanded = this;
        Vector2 begin = expanded._position;
        Vector2 end = expanded._position + expanded._size;
        if (to.X < begin.X) begin.X = to.X;
        if (to.Y < begin.Y) begin.Y = to.Y;
        if (to.X > end.X) end.X = to.X;
        if (to.Y > end.Y) end.Y = to.Y;
        expanded._position = begin;
        expanded._size = end - begin;
        return expanded;
    }

    public readonly Vector2 GetCenter() => _position + _size * 0.5f;

    public readonly Rect2 Grow(float by)
    {
        Rect2 g = this;
        g._position.X -= by;
        g._position.Y -= by;
        g._size.X += by * 2;
        g._size.Y += by * 2;
        return g;
    }

    public readonly Rect2 GrowIndividual(float left, float top, float right, float bottom)
    {
        Rect2 g = this;
        g._position.X -= left;
        g._position.Y -= top;
        g._size.X += left + right;
        g._size.Y += top + bottom;
        return g;
    }

    public readonly bool HasArea() => _size.X > 0.0f && _size.Y > 0.0f;

    public readonly bool HasPoint(Vector2 point)
    {
        if (point.X < _position.X) return false;
        if (point.Y < _position.Y) return false;
        if (point.X >= _position.X + _size.X) return false;
        if (point.Y >= _position.Y + _size.Y) return false;
        return true;
    }

    public readonly bool Intersects(Rect2 b, bool includeBorders = false)
    {
        if (includeBorders)
        {
            if (_position.X > b._position.X + b._size.X) return false;
            if (_position.X + _size.X < b._position.X) return false;
            if (_position.Y > b._position.Y + b._size.Y) return false;
            if (_position.Y + _size.Y < b._position.Y) return false;
        }
        else
        {
            if (_position.X >= b._position.X + b._size.X) return false;
            if (_position.X + _size.X <= b._position.X) return false;
            if (_position.Y >= b._position.Y + b._size.Y) return false;
            if (_position.Y + _size.Y <= b._position.Y) return false;
        }
        return true;
    }

    public readonly Rect2 Merge(Rect2 b)
    {
        Rect2 newRect;
        newRect._position = new Vector2(Mathf.Min(b._position.X, _position.X), Mathf.Min(b._position.Y, _position.Y));
        newRect._size = new Vector2(Mathf.Max(b._position.X + b._size.X, _position.X + _size.X),
                                    Mathf.Max(b._position.Y + b._size.Y, _position.Y + _size.Y));
        newRect._size -= newRect._position;
        return newRect;
    }

    public readonly bool IsEqualApprox(Rect2 other) => _position.IsEqualApprox(other._position) && _size.IsEqualApprox(other._size);

    public static bool operator ==(Rect2 a, Rect2 b) => a.Equals(b);
    public static bool operator !=(Rect2 a, Rect2 b) => !a.Equals(b);
    public override readonly bool Equals(object? obj) => obj is Rect2 other && Equals(other);
    public readonly bool Equals(Rect2 other) => _position.Equals(other._position) && _size.Equals(other._size);
    public override readonly int GetHashCode() => HashCode.Combine(_position, _size);
    public override readonly string ToString() => $"{_position}, {_size}";
}

public struct Transform2D : IEquatable<Transform2D>
{
    /// <summary>Basis X column.</summary>
    public Vector2 X;
    /// <summary>Basis Y column.</summary>
    public Vector2 Y;
    public Vector2 Origin;

    public static Transform2D Identity => new(1, 0, 0, 1, 0, 0);
    public static Transform2D FlipX => new(-1, 0, 0, 1, 0, 0);
    public static Transform2D FlipY => new(1, 0, 0, -1, 0, 0);

    public Transform2D(Vector2 xAxis, Vector2 yAxis, Vector2 originPos) { X = xAxis; Y = yAxis; Origin = originPos; }

    public Transform2D(float xx, float xy, float yx, float yy, float ox, float oy)
    {
        X = new Vector2(xx, xy);
        Y = new Vector2(yx, yy);
        Origin = new Vector2(ox, oy);
    }

    public Transform2D(float rotation, Vector2 origin)
    {
        (float sin, float cos) = MathF.SinCos(rotation);
        X = new Vector2(cos, sin);
        Y = new Vector2(-sin, cos);
        Origin = origin;
    }

    public Transform2D(float rotation, Vector2 scale, float skew, Vector2 origin)
    {
        (float rotationSin, float rotationCos) = MathF.SinCos(rotation);
        (float rotationSkewSin, float rotationSkewCos) = MathF.SinCos(rotation + skew);
        X = new Vector2(rotationCos * scale.X, rotationSin * scale.X);
        Y = new Vector2(-rotationSkewSin * scale.Y, rotationSkewCos * scale.Y);
        Origin = origin;
    }

    public readonly float Rotation => MathF.Atan2(X.Y, X.X);

    public readonly Vector2 Scale
    {
        get
        {
            float detSign = Mathf.Sign(Determinant());
            return new Vector2(X.Length(), detSign * Y.Length());
        }
    }

    public readonly float Skew
    {
        get
        {
            float detSign = Mathf.Sign(Determinant());
            return MathF.Acos(X.Normalized().Dot(detSign * Y.Normalized())) - MathF.PI * 0.5f;
        }
    }

    public Vector2 this[int column]
    {
        readonly get => column switch { 0 => X, 1 => Y, 2 => Origin, _ => throw new ArgumentOutOfRangeException(nameof(column)) };
        set
        {
            switch (column)
            {
                case 0: X = value; return;
                case 1: Y = value; return;
                case 2: Origin = value; return;
                default: throw new ArgumentOutOfRangeException(nameof(column));
            }
        }
    }

    public readonly float Determinant() => X.X * Y.Y - X.Y * Y.X;

    private readonly float Tdotx(Vector2 with) => X.X * with.X + Y.X * with.Y;
    private readonly float Tdoty(Vector2 with) => X.Y * with.X + Y.Y * with.Y;

    public readonly Vector2 BasisXform(Vector2 v) => new(Tdotx(v), Tdoty(v));
    public readonly Vector2 BasisXformInv(Vector2 v) => new(X.Dot(v), Y.Dot(v));

    public readonly Transform2D AffineInverse()
    {
        float det = Determinant();
        if (det == 0) throw new InvalidOperationException("Matrix determinant is zero and cannot be inverted.");

        Transform2D inv = this;
        (inv.X.X, inv.Y.Y) = (inv.Y.Y, inv.X.X);
        inv.X *= new Vector2(1.0f / det, -1.0f / det);
        inv.Y *= new Vector2(-1.0f / det, 1.0f / det);
        inv.Origin = inv.BasisXform(-inv.Origin);
        return inv;
    }

    /// <summary>Inverse assuming an orthonormal basis (rotation + translation only).</summary>
    public readonly Transform2D Inverse()
    {
        Transform2D inv = this;
        inv.X.Y = Y.X;
        inv.Y.X = X.Y;
        inv.Origin = inv.BasisXform(-inv.Origin);
        return inv;
    }

    public readonly Transform2D Orthonormalized()
    {
        Transform2D ortho = this;
        Vector2 orthoX = ortho.X;
        Vector2 orthoY = ortho.Y;
        orthoX = orthoX.Normalized();
        orthoY = orthoY - orthoX * orthoX.Dot(orthoY);
        orthoY = orthoY.Normalized();
        ortho.X = orthoX;
        ortho.Y = orthoY;
        return ortho;
    }

    public readonly Transform2D Rotated(float angle) => new Transform2D(angle, Vector2.Zero) * this;
    public readonly Transform2D RotatedLocal(float angle) => this * new Transform2D(angle, Vector2.Zero);

    public readonly Transform2D Scaled(Vector2 scale)
    {
        Transform2D copy = this;
        copy.X *= scale;
        copy.Y *= scale;
        copy.Origin *= scale;
        return copy;
    }

    public readonly Transform2D ScaledLocal(Vector2 scale) => new(X * scale, Y * scale, Origin);
    public readonly Transform2D Translated(Vector2 offset) => new(X, Y, Origin + offset);
    public readonly Transform2D TranslatedLocal(Vector2 offset) => new(X, Y, Origin + BasisXform(offset));

    // Not in GodotSharp; kept for existing stub users.
    public readonly Transform2D SampleBakedWithRotation() => this;

    public readonly bool IsEqualApprox(Transform2D other) =>
        X.IsEqualApprox(other.X) && Y.IsEqualApprox(other.Y) && Origin.IsEqualApprox(other.Origin);

    public static Transform2D operator *(Transform2D left, Transform2D right)
    {
        left.Origin = left * right.Origin;

        float x0 = left.Tdotx(right.X);
        float x1 = left.Tdoty(right.X);
        float y0 = left.Tdotx(right.Y);
        float y1 = left.Tdoty(right.Y);

        left.X.X = x0;
        left.X.Y = x1;
        left.Y.X = y0;
        left.Y.Y = y1;
        return left;
    }

    public static Vector2 operator *(Transform2D transform, Vector2 vector) =>
        new Vector2(transform.Tdotx(vector), transform.Tdoty(vector)) + transform.Origin;

    /// <summary>Inverse-transforms the vector (assumes an orthonormal basis).</summary>
    public static Vector2 operator *(Vector2 vector, Transform2D transform)
    {
        Vector2 vInv = vector - transform.Origin;
        return new Vector2(transform.X.Dot(vInv), transform.Y.Dot(vInv));
    }

    public static Rect2 operator *(Transform2D transform, Rect2 rect)
    {
        Vector2 pos = transform * rect.Position;
        Vector2 toX = transform.X * rect.Size.X;
        Vector2 toY = transform.Y * rect.Size.Y;
        return new Rect2(pos, new Vector2()).Expand(pos + toX).Expand(pos + toY).Expand(pos + toX + toY);
    }

    public static bool operator ==(Transform2D a, Transform2D b) => a.Equals(b);
    public static bool operator !=(Transform2D a, Transform2D b) => !a.Equals(b);
    public override readonly bool Equals(object? obj) => obj is Transform2D other && Equals(other);
    public readonly bool Equals(Transform2D other) => X.Equals(other.X) && Y.Equals(other.Y) && Origin.Equals(other.Origin);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Origin);
    public override readonly string ToString() => $"[X: {X}, Y: {Y}, O: {Origin}]";
}

public static class Mathf
{
    public const float Tau = MathF.Tau;
    public const float Pi = MathF.PI;
    public const float Inf = float.PositiveInfinity;
    public const float NaN = float.NaN;
    public const float E = MathF.E;
    public const float Sqrt2 = 1.41421356f;
    public const float Epsilon = 1e-06f;

    private const float DegToRadConstF = MathF.PI / 180.0f;
    private const double DegToRadConstD = Math.PI / 180.0;
    private const float RadToDegConstF = 180.0f / MathF.PI;
    private const double RadToDegConstD = 180.0 / Math.PI;

    public static int Abs(int s) => Math.Abs(s);
    public static float Abs(float s) => Math.Abs(s);
    public static double Abs(double s) => Math.Abs(s);

    public static float Acos(float s) => MathF.Acos(s);
    public static double Acos(double s) => Math.Acos(s);
    public static float Asin(float s) => MathF.Asin(s);
    public static double Asin(double s) => Math.Asin(s);
    public static float Atan(float s) => MathF.Atan(s);
    public static double Atan(double s) => Math.Atan(s);
    public static float Atan2(float y, float x) => MathF.Atan2(y, x);
    public static double Atan2(double y, double x) => Math.Atan2(y, x);
    public static float Cos(float s) => MathF.Cos(s);
    public static double Cos(double s) => Math.Cos(s);
    public static float Cosh(float s) => MathF.Cosh(s);
    public static double Cosh(double s) => Math.Cosh(s);
    public static float Sin(float s) => MathF.Sin(s);
    public static double Sin(double s) => Math.Sin(s);
    public static float Sinh(float s) => MathF.Sinh(s);
    public static double Sinh(double s) => Math.Sinh(s);
    public static float Tan(float s) => MathF.Tan(s);
    public static double Tan(double s) => Math.Tan(s);
    public static float Tanh(float s) => MathF.Tanh(s);
    public static double Tanh(double s) => Math.Tanh(s);
    public static float Exp(float s) => MathF.Exp(s);
    public static double Exp(double s) => Math.Exp(s);
    public static float Log(float s) => MathF.Log(s);
    public static double Log(double s) => Math.Log(s);
    public static float Pow(float x, float y) => MathF.Pow(x, y);
    public static double Pow(double x, double y) => Math.Pow(x, y);
    public static float Sqrt(float s) => MathF.Sqrt(s);
    public static double Sqrt(double s) => Math.Sqrt(s);

    public static float Ceil(float s) => MathF.Ceiling(s);
    public static double Ceil(double s) => Math.Ceiling(s);
    public static float Floor(float s) => MathF.Floor(s);
    public static double Floor(double s) => Math.Floor(s);
    public static float Round(float s) => MathF.Round(s);
    public static double Round(double s) => Math.Round(s);
    public static int CeilToInt(float s) => (int)MathF.Ceiling(s);
    public static int CeilToInt(double s) => (int)Math.Ceiling(s);
    public static int FloorToInt(float s) => (int)MathF.Floor(s);
    public static int FloorToInt(double s) => (int)Math.Floor(s);
    public static int RoundToInt(float s) => (int)MathF.Round(s);
    public static int RoundToInt(double s) => (int)Math.Round(s);

    // GodotSharp delegates to System.Math.Clamp, which throws when min > max.
    public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
    public static float Clamp(float value, float min, float max) => Math.Clamp(value, min, max);
    public static double Clamp(double value, double min, double max) => Math.Clamp(value, min, max);

    public static int Max(int a, int b) => a > b ? a : b;
    public static float Max(float a, float b) => a > b ? a : b;
    public static double Max(double a, double b) => a > b ? a : b;
    public static int Min(int a, int b) => a < b ? a : b;
    public static float Min(float a, float b) => a < b ? a : b;
    public static double Min(double a, double b) => a < b ? a : b;

    public static int Sign(int s) => s == 0 ? 0 : s < 0 ? -1 : 1;
    public static int Sign(float s) => s == 0 ? 0 : s < 0 ? -1 : 1;
    public static int Sign(double s) => s == 0 ? 0 : s < 0 ? -1 : 1;

    public static float DegToRad(float deg) => deg * DegToRadConstF;
    public static double DegToRad(double deg) => deg * DegToRadConstD;
    public static float RadToDeg(float rad) => rad * RadToDegConstF;
    public static double RadToDeg(double rad) => rad * RadToDegConstD;

    public static float BezierInterpolate(float start, float control1, float control2, float end, float t)
    {
        float omt = 1.0f - t, omt2 = omt * omt, omt3 = omt2 * omt;
        float t2 = t * t, t3 = t2 * t;
        return start * omt3 + control1 * omt2 * t * 3.0f + control2 * omt * t2 * 3.0f + end * t3;
    }
    public static double BezierInterpolate(double start, double control1, double control2, double end, double t)
    {
        double omt = 1.0 - t, omt2 = omt * omt, omt3 = omt2 * omt;
        double t2 = t * t, t3 = t2 * t;
        return start * omt3 + control1 * omt2 * t * 3.0 + control2 * omt * t2 * 3.0 + end * t3;
    }
    public static float BezierDerivative(float start, float control1, float control2, float end, float t)
    {
        float omt = 1.0f - t, omt2 = omt * omt, t2 = t * t;
        return (control1 - start) * 3.0f * omt2 + (control2 - control1) * 6.0f * omt * t + (end - control2) * 3.0f * t2;
    }
    public static double BezierDerivative(double start, double control1, double control2, double end, double t)
    {
        double omt = 1.0 - t, omt2 = omt * omt, t2 = t * t;
        return (control1 - start) * 3.0 * omt2 + (control2 - control1) * 6.0 * omt * t + (end - control2) * 3.0 * t2;
    }
    public static float CubicInterpolate(float from, float to, float pre, float post, float weight) =>
        0.5f * ((from * 2.0f) + (-pre + to) * weight + (2.0f * pre - 5.0f * from + 4.0f * to - post) * (weight * weight)
                + (-pre + 3.0f * from - 3.0f * to + post) * (weight * weight * weight));
    public static double CubicInterpolate(double from, double to, double pre, double post, double weight) =>
        0.5 * ((from * 2.0) + (-pre + to) * weight + (2.0 * pre - 5.0 * from + 4.0 * to - post) * (weight * weight)
               + (-pre + 3.0 * from - 3.0 * to + post) * (weight * weight * weight));

    public static float Lerp(float from, float to, float weight) => from + (to - from) * weight;
    public static double Lerp(double from, double to, double weight) => from + (to - from) * weight;
    public static float InverseLerp(float from, float to, float weight) => (weight - from) / (to - from);
    public static double InverseLerp(double from, double to, double weight) => (weight - from) / (to - from);
    public static float Remap(float value, float inFrom, float inTo, float outFrom, float outTo) =>
        Lerp(outFrom, outTo, InverseLerp(inFrom, inTo, value));
    public static double Remap(double value, double inFrom, double inTo, double outFrom, double outTo) =>
        Lerp(outFrom, outTo, InverseLerp(inFrom, inTo, value));

    public static float AngleDifference(float from, float to)
    {
        float difference = (to - from) % Tau;
        return (2.0f * difference % Tau) - difference;
    }
    public static double AngleDifference(double from, double to)
    {
        double difference = (to - from) % Math.Tau;
        return (2.0 * difference % Math.Tau) - difference;
    }
    public static float LerpAngle(float from, float to, float weight) => from + AngleDifference(from, to) * weight;
    public static double LerpAngle(double from, double to, double weight) => from + AngleDifference(from, to) * weight;

    public static float MoveToward(float from, float to, float delta) =>
        Math.Abs(to - from) <= delta ? to : from + Sign(to - from) * delta;
    public static double MoveToward(double from, double to, double delta) =>
        Math.Abs(to - from) <= delta ? to : from + Sign(to - from) * delta;

    public static float SmoothStep(float from, float to, float weight)
    {
        if (IsEqualApprox(from, to)) return from;
        float x = Clamp((weight - from) / (to - from), 0.0f, 1.0f);
        return x * x * (3 - 2 * x);
    }
    public static double SmoothStep(double from, double to, double weight)
    {
        if (IsEqualApprox(from, to)) return from;
        double x = Clamp((weight - from) / (to - from), 0.0, 1.0);
        return x * x * (3 - 2 * x);
    }

    public static float Snapped(float s, float step) => step != 0f ? MathF.Floor(s / step + 0.5f) * step : s;
    public static double Snapped(double s, double step) => step != 0d ? Math.Floor(s / step + 0.5) * step : s;

    public static int PosMod(int a, int b)
    {
        int c = a % b;
        if ((c < 0 && b > 0) || (c > 0 && b < 0)) c += b;
        return c;
    }
    public static float PosMod(float a, float b)
    {
        float c = a % b;
        if ((c < 0 && b > 0) || (c > 0 && b < 0)) c += b;
        return c;
    }
    public static double PosMod(double a, double b)
    {
        double c = a % b;
        if ((c < 0 && b > 0) || (c > 0 && b < 0)) c += b;
        return c;
    }

    public static int Wrap(int value, int min, int max)
    {
        int range = max - min;
        if (range == 0) return min;
        return min + ((value - min) % range + range) % range;
    }
    public static float Wrap(float value, float min, float max)
    {
        float range = max - min;
        if (IsZeroApprox(range)) return min;
        return min + ((value - min) % range + range) % range;
    }
    public static double Wrap(double value, double min, double max)
    {
        double range = max - min;
        if (IsZeroApprox(range)) return min;
        return min + ((value - min) % range + range) % range;
    }

    private static float Fract(float value) => value - MathF.Floor(value);
    private static double Fract(double value) => value - Math.Floor(value);
    public static float PingPong(float value, float length) =>
        length != 0.0f ? Math.Abs(Fract((value - length) / (length * 2.0f)) * length * 2.0f - length) : 0.0f;
    public static double PingPong(double value, double length) =>
        length != 0.0 ? Math.Abs(Fract((value - length) / (length * 2.0)) * length * 2.0 - length) : 0.0;

    public static float LinearToDb(float linear) => MathF.Log(linear) * 8.6858896380650365530225783783321f;
    public static double LinearToDb(double linear) => Math.Log(linear) * 8.6858896380650365530225783783321;
    public static float DbToLinear(float db) => MathF.Exp(db * 0.11512925464970228420089957273422f);
    public static double DbToLinear(double db) => Math.Exp(db * 0.11512925464970228420089957273422);

    public static bool IsEqualApprox(float a, float b)
    {
        if (a == b) return true;
        float tolerance = Epsilon * Math.Abs(a);
        if (tolerance < Epsilon) tolerance = Epsilon;
        return Math.Abs(a - b) < tolerance;
    }
    public static bool IsEqualApprox(double a, double b)
    {
        if (a == b) return true;
        double tolerance = 1e-14 * Math.Abs(a);
        if (tolerance < 1e-14) tolerance = 1e-14;
        return Math.Abs(a - b) < tolerance;
    }
    public static bool IsEqualApprox(float a, float b, float tolerance) => a == b || Math.Abs(a - b) < tolerance;
    public static bool IsEqualApprox(double a, double b, double tolerance) => a == b || Math.Abs(a - b) < tolerance;
    public static bool IsZeroApprox(float s) => Math.Abs(s) < Epsilon;
    public static bool IsZeroApprox(double s) => Math.Abs(s) < 1e-14;
    public static bool IsFinite(float s) => float.IsFinite(s);
    public static bool IsFinite(double s) => double.IsFinite(s);
    public static bool IsInf(float s) => float.IsInfinity(s);
    public static bool IsInf(double s) => double.IsInfinity(s);
    public static bool IsNaN(float s) => float.IsNaN(s);
    public static bool IsNaN(double s) => double.IsNaN(s);
}
