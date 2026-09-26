namespace Godot;

// Missing Godot node types
public class Line2D : Node2D
{
    public Vector2[] Points { get; set; } = Array.Empty<Vector2>();
    public float Width { get; set; } = 1f;
    public Color DefaultColor { get; set; } = Color.White;
    // Godot appends when index is negative or past the end.
    public void AddPoint(Vector2 position, int index = -1)
    {
        var points = new List<Vector2>(Points);
        if (index < 0 || index > points.Count) points.Add(position);
        else points.Insert(index, position);
        Points = points.ToArray();
    }
    public void ClearPoints() => Points = Array.Empty<Vector2>();
}

public class CpuParticles2D : Node2D
{
    public bool Emitting { get; set; }
    public float EmissionSphereRadius { get; set; } = 1f;
    public Texture2D? Texture { get; set; }
}

public class Marker2D : Node2D { }
public class PathFollow2D : Node2D
{
    public float Progress { get; set; }
    public float ProgressRatio { get; set; }
}
public class Path2D : Node2D
{
    // Scenes always give a Path2D its curve; headless nodes are built without scene data, so
    // start with an empty one (baked length 0) rather than null.
    public Curve2D Curve { get; set; } = new();
}

// Points and baking follow Godot's Curve2D (scene/resources/curve.cpp): each segment is a cubic
// Bezier through the neighbouring points' out/in handles, tessellated to roughly even lengths of
// BakeInterval, and the baked length is the polyline length through the tessellated points.
public class Curve2D : Resource
{
    private readonly List<(Vector2 Position, Vector2 In, Vector2 Out)> _points = new();
    private float _bakeInterval = 5f;
    private Vector2[] _bakedPoints = Array.Empty<Vector2>();
    private float[] _bakedDist = Array.Empty<float>();
    private Vector2[] _bakedForward = Array.Empty<Vector2>();
    private bool _dirty = true;

    public float BakeInterval
    {
        get => _bakeInterval;
        set { _bakeInterval = value; _dirty = true; }
    }
    // Resizing truncates or appends points at the origin, as Godot's set_point_count does.
    public int PointCount
    {
        get => _points.Count;
        set
        {
            if (value < 0) return;
            if (value < _points.Count) _points.RemoveRange(value, _points.Count - value);
            while (_points.Count < value) _points.Add((Vector2.Zero, Vector2.Zero, Vector2.Zero));
            _dirty = true;
        }
    }
    public int GetPointCount() => _points.Count;

    public void AddPoint(Vector2 position, Vector2? @in = null, Vector2? @out = null, int index = -1)
    {
        var point = (position, @in ?? Vector2.Zero, @out ?? Vector2.Zero);
        if (index >= 0 && index < _points.Count) _points.Insert(index, point);
        else _points.Add(point);
        _dirty = true;
    }
    public Vector2 GetPointPosition(int idx) => idx >= 0 && idx < _points.Count ? _points[idx].Position : Vector2.Zero;
    public void ClearPoints()
    {
        _points.Clear();
        _dirty = true;
    }

    public float GetBakedLength()
    {
        Bake();
        return _bakedDist.Length == 0 ? 0f : _bakedDist[^1];
    }

    public Vector2[] GetBakedPoints()
    {
        Bake();
        return (Vector2[])_bakedPoints.Clone();
    }

    public Vector2 SampleBaked(float offset = 0, bool cubic = false)
    {
        Bake();
        int pc = _bakedPoints.Length;
        if (pc == 0) return Vector2.Zero;
        if (pc == 1) return _bakedPoints[0];
        var (idx, frac) = FindInterval(offset);
        return SampleBaked(idx, frac, cubic);
    }

    // Godot: position from SampleBaked, rotation from the Bezier tangents at the interval's ends
    // (slerped), side = forward rotated +90°.
    public Transform2D SampleBakedWithRotation(float offset = 0, bool cubic = false)
    {
        Bake();
        int pc = _bakedPoints.Length;
        if (pc == 0) return Transform2D.Identity;
        if (pc == 1) return new Transform2D(Vector2.Right, Vector2.Down, _bakedPoints[0]);
        var (idx, frac) = FindInterval(offset);
        Vector2 forward = _bakedForward[idx].Slerp(_bakedForward[idx + 1], frac).Normalized();
        return new Transform2D(forward, new Vector2(-forward.Y, forward.X), SampleBaked(idx, frac, cubic));
    }

    private (int Idx, float Frac) FindInterval(float offset)
    {
        offset = Mathf.Clamp(offset, 0f, _bakedDist[^1]);
        // Binary search for the baked interval holding offset.
        int start = 0, end = _bakedDist.Length, idx = (end + start) / 2;
        while (start < idx)
        {
            if (offset <= _bakedDist[idx]) end = idx;
            else start = idx;
            idx = (end + start) / 2;
        }
        float span = _bakedDist[idx + 1] - _bakedDist[idx];
        return (idx, span < float.Epsilon ? 0.5f : (offset - _bakedDist[idx]) / span);
    }

    private Vector2 SampleBaked(int idx, float frac, bool cubic)
    {
        int pc = _bakedPoints.Length;
        if (!cubic) return _bakedPoints[idx].Lerp(_bakedPoints[idx + 1], frac);
        Vector2 pre = idx > 0 ? _bakedPoints[idx - 1] : _bakedPoints[idx];
        Vector2 post = idx < pc - 2 ? _bakedPoints[idx + 2] : _bakedPoints[idx + 1];
        return _bakedPoints[idx].CubicInterpolate(_bakedPoints[idx + 1], pre, post, frac);
    }

    private void Bake()
    {
        if (!_dirty) return;
        _dirty = false;
        if (_points.Count == 0)
        {
            _bakedPoints = Array.Empty<Vector2>();
            _bakedDist = Array.Empty<float>();
            _bakedForward = Array.Empty<Vector2>();
            return;
        }
        if (_points.Count == 1)
        {
            _bakedPoints = new[] { _points[0].Position };
            _bakedDist = new[] { 0f };
            _bakedForward = new[] { new Vector2(0f, 0.1f) };
            return;
        }
        var points = new List<Vector2> { _points[0].Position };
        var forward = new List<Vector2> { Tangent(0, 0f) };
        for (int i = 0; i < _points.Count - 1; i++)
        {
            var mid = new SortedDictionary<float, Vector2>();
            BakeSegmentEvenLength(mid, 0f, 1f, _points[i].Position, _points[i].Out, _points[i + 1].Position, _points[i + 1].In, 0, 10, _bakeInterval);
            foreach (var (t, p) in mid)
            {
                points.Add(p);
                forward.Add(Tangent(i, t));
            }
            points.Add(_points[i + 1].Position);
            forward.Add(Tangent(i, 1f));
        }
        _bakedPoints = points.ToArray();
        _bakedForward = forward.ToArray();
        _bakedDist = new float[_bakedPoints.Length];
        for (int i = 1; i < _bakedPoints.Length; i++)
            _bakedDist[i] = _bakedDist[i - 1] + _bakedPoints[i].DistanceTo(_bakedPoints[i - 1]);
    }

    // Godot's _calculate_tangent for segment i at t, with its corner cases for zero-length handles.
    private Vector2 Tangent(int i, float t)
    {
        Vector2 begin = _points[i].Position, end = _points[i + 1].Position;
        Vector2 control1 = begin + _points[i].Out, control2 = end + _points[i + 1].In;
        const float cmpEpsilon = 0.00001f;
        if (MathF.Abs(t) < cmpEpsilon && ApproxEqual(control1, begin)) return (end - begin).Normalized();
        if (MathF.Abs(t - 1f) < cmpEpsilon && ApproxEqual(control2, end)) return (end - begin).Normalized();
        return begin.BezierDerivative(control1, control2, end, t).Normalized();

        static bool ApproxEqual(Vector2 a, Vector2 b) => Approx(a.X, b.X) && Approx(a.Y, b.Y);
        static bool Approx(float a, float b)
        {
            if (a == b) return true;
            float tolerance = Math.Max(cmpEpsilon * MathF.Abs(a), cmpEpsilon);
            return MathF.Abs(a - b) < tolerance;
        }
    }

    private static void BakeSegmentEvenLength(SortedDictionary<float, Vector2> bake, float begin, float end, Vector2 a, Vector2 @out, Vector2 b, Vector2 @in, int depth, int maxDepth, float length)
    {
        Vector2 beg = a.BezierInterpolate(a + @out, b + @in, b, begin);
        Vector2 fin = a.BezierInterpolate(a + @out, b + @in, b, end);
        if (beg.DistanceTo(fin) > length && depth < maxDepth)
        {
            float mp = (begin + end) * 0.5f;
            bake[mp] = a.BezierInterpolate(a + @out, b + @in, b, mp);
            BakeSegmentEvenLength(bake, begin, mp, a, @out, b, @in, depth + 1, maxDepth, length);
            BakeSegmentEvenLength(bake, mp, end, a, @out, b, @in, depth + 1, maxDepth, length);
        }
    }
}

public class BackBufferCopy : Node2D { }
public class CanvasGroup : Node2D { }
public class CanvasItemMaterial : Material { }

public class NinePatchRect : Control
{
    public Texture2D? Texture { get; set; }
}

public class AspectRatioContainer : Container { }
public class VFlowContainer : FlowContainer { }

public class WorldEnvironment : Node { }
public class FastNoiseLite : Resource { }

public class Font : Resource
{
    public float GetStringSize(string text, int alignment = 0, float width = -1, int fontSize = 16) => text.Length * fontSize * 0.6f;
}

public class TextParagraph
{
    public void Clear() { }
    public void AddString(string text, Font font, int fontSize) { }
    public Vector2 GetSize() => Vector2.Zero;
    public float GetWidth() => 0;
}

public class StyleBoxEmpty : Resource { }

public class GradientTexture2D : Texture2D { }
public class Gradient : Resource { }

public class ParticleProcessMaterial : Material
{
    public Vector3 EmissionBoxExtents { get; set; }
    public Color Color { get; set; } = Colors.White;
}

public class RenderingServer
{
    public enum ViewportMsaa : long
    {
        Disabled = 0,
        Msaa2X = 1,
        Msaa4X = 2,
        Msaa8X = 3,
        Max = 4,
    }
    public static void GlobalShaderParameterSet(StringName name, Variant value) { }
}

// Input types
public static class Input
{
    public enum MouseModeEnum : long
    {
        Visible = 0,
        Hidden = 1,
        Captured = 2,
        Confined = 3,
        ConfinedHidden = 4,
        Max = 5,
    }

    public static MouseModeEnum MouseMode { get; set; }
    public static void SetMouseMode(MouseModeEnum mode) => MouseMode = mode;
    public static MouseModeEnum GetMouseMode() => MouseMode;
}

public enum Key : long
{
    None = 0,
    Special = 4194304,
    Escape = 4194305,
    Tab = 4194306,
    Backtab = 4194307,
    Backspace = 4194308,
    Enter = 4194309,
    KpEnter = 4194310,
    Insert = 4194311,
    Delete = 4194312,
    Pause = 4194313,
    Print = 4194314,
    Sysreq = 4194315,
    Clear = 4194316,
    Home = 4194317,
    End = 4194318,
    Left = 4194319,
    Up = 4194320,
    Right = 4194321,
    Down = 4194322,
    Pageup = 4194323,
    Pagedown = 4194324,
    Shift = 4194325,
    Ctrl = 4194326,
    Meta = 4194327,
    Alt = 4194328,
    Capslock = 4194329,
    Numlock = 4194330,
    Scrolllock = 4194331,
    F1 = 4194332,
    F2 = 4194333,
    F3 = 4194334,
    F4 = 4194335,
    F5 = 4194336,
    F6 = 4194337,
    F7 = 4194338,
    F8 = 4194339,
    F9 = 4194340,
    F10 = 4194341,
    F11 = 4194342,
    F12 = 4194343,
    F13 = 4194344,
    F14 = 4194345,
    F15 = 4194346,
    F16 = 4194347,
    F17 = 4194348,
    F18 = 4194349,
    F19 = 4194350,
    F20 = 4194351,
    F21 = 4194352,
    F22 = 4194353,
    F23 = 4194354,
    F24 = 4194355,
    F25 = 4194356,
    F26 = 4194357,
    F27 = 4194358,
    F28 = 4194359,
    F29 = 4194360,
    F30 = 4194361,
    F31 = 4194362,
    F32 = 4194363,
    F33 = 4194364,
    F34 = 4194365,
    F35 = 4194366,
    KpMultiply = 4194433,
    KpDivide = 4194434,
    KpSubtract = 4194435,
    KpPeriod = 4194436,
    KpAdd = 4194437,
    Kp0 = 4194438,
    Kp1 = 4194439,
    Kp2 = 4194440,
    Kp3 = 4194441,
    Kp4 = 4194442,
    Kp5 = 4194443,
    Kp6 = 4194444,
    Kp7 = 4194445,
    Kp8 = 4194446,
    Kp9 = 4194447,
    Menu = 4194370,
    Hyper = 4194371,
    Help = 4194373,
    Back = 4194376,
    Forward = 4194377,
    Stop = 4194378,
    Refresh = 4194379,
    Volumedown = 4194380,
    Volumemute = 4194381,
    Volumeup = 4194382,
    Mediaplay = 4194388,
    Mediastop = 4194389,
    Mediaprevious = 4194390,
    Medianext = 4194391,
    Mediarecord = 4194392,
    Homepage = 4194393,
    Favorites = 4194394,
    Search = 4194395,
    Standby = 4194396,
    Openurl = 4194397,
    Launchmail = 4194398,
    Launchmedia = 4194399,
    Launch0 = 4194400,
    Launch1 = 4194401,
    Launch2 = 4194402,
    Launch3 = 4194403,
    Launch4 = 4194404,
    Launch5 = 4194405,
    Launch6 = 4194406,
    Launch7 = 4194407,
    Launch8 = 4194408,
    Launch9 = 4194409,
    Launcha = 4194410,
    Launchb = 4194411,
    Launchc = 4194412,
    Launchd = 4194413,
    Launche = 4194414,
    Launchf = 4194415,
    Globe = 4194416,
    Keyboard = 4194417,
    JisEisu = 4194418,
    JisKana = 4194419,
    Unknown = 8388607,
    Space = 32,
    Exclam = 33,
    Quotedbl = 34,
    Numbersign = 35,
    Dollar = 36,
    Percent = 37,
    Ampersand = 38,
    Apostrophe = 39,
    Parenleft = 40,
    Parenright = 41,
    Asterisk = 42,
    Plus = 43,
    Comma = 44,
    Minus = 45,
    Period = 46,
    Slash = 47,
    Key0 = 48,
    Key1 = 49,
    Key2 = 50,
    Key3 = 51,
    Key4 = 52,
    Key5 = 53,
    Key6 = 54,
    Key7 = 55,
    Key8 = 56,
    Key9 = 57,
    Colon = 58,
    Semicolon = 59,
    Less = 60,
    Equal = 61,
    Greater = 62,
    Question = 63,
    At = 64,
    A = 65,
    B = 66,
    C = 67,
    D = 68,
    E = 69,
    F = 70,
    G = 71,
    H = 72,
    I = 73,
    J = 74,
    K = 75,
    L = 76,
    M = 77,
    N = 78,
    O = 79,
    P = 80,
    Q = 81,
    R = 82,
    S = 83,
    T = 84,
    U = 85,
    V = 86,
    W = 87,
    X = 88,
    Y = 89,
    Z = 90,
    Bracketleft = 91,
    Backslash = 92,
    Bracketright = 93,
    Asciicircum = 94,
    Underscore = 95,
    Quoteleft = 96,
    Braceleft = 123,
    Bar = 124,
    Braceright = 125,
    Asciitilde = 126,
    Yen = 165,
    Section = 167,
}
public enum MouseButton : long
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
    WheelUp = 4,
    WheelDown = 5,
    WheelLeft = 6,
    WheelRight = 7,
    Xbutton1 = 8,
    Xbutton2 = 9,
}
public enum JoyAxis : long
{
    Invalid = -1,
    LeftX = 0,
    LeftY = 1,
    RightX = 2,
    RightY = 3,
    TriggerLeft = 4,
    TriggerRight = 5,
    SdlMax = 6,
    Max = 10,
}
public class InputEventJoypadMotion : InputEvent
{
    public JoyAxis Axis { get; set; }
    public float AxisValue { get; set; }
}
public class InputEventAction : InputEvent
{
    public StringName Action { get; set; } = "";
}

// Error enum
public enum Error : long
{
    Ok = 0,
    Failed = 1,
    Unavailable = 2,
    Unconfigured = 3,
    Unauthorized = 4,
    ParameterRangeError = 5,
    OutOfMemory = 6,
    FileNotFound = 7,
    FileBadDrive = 8,
    FileBadPath = 9,
    FileNoPermission = 10,
    FileAlreadyInUse = 11,
    FileCantOpen = 12,
    FileCantWrite = 13,
    FileCantRead = 14,
    FileUnrecognized = 15,
    FileCorrupt = 16,
    FileMissingDependencies = 17,
    FileEof = 18,
    CantOpen = 19,
    CantCreate = 20,
    QueryFailed = 21,
    AlreadyInUse = 22,
    Locked = 23,
    Timeout = 24,
    CantConnect = 25,
    CantResolve = 26,
    ConnectionError = 27,
    CantAcquireResource = 28,
    CantFork = 29,
    InvalidData = 30,
    InvalidParameter = 31,
    AlreadyExists = 32,
    DoesNotExist = 33,
    DatabaseCantRead = 34,
    DatabaseCantWrite = 35,
    CompilationFailed = 36,
    MethodNotFound = 37,
    LinkFailed = 38,
    ScriptFailed = 39,
    CyclicLink = 40,
    InvalidDeclaration = 41,
    DuplicateSymbol = 42,
    ParseError = 43,
    Busy = 44,
    Skip = 45,
    Help = 46,
    Bug = 47,
    PrinterOnFire = 48,
}

// Tool attribute
[AttributeUsage(AttributeTargets.Class)]
public class ToolAttribute : Attribute { }

// ExportToolButton attribute
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class ExportToolButtonAttribute : Attribute
{
    public ExportToolButtonAttribute(string text, string icon = "") { }
}

// AssemblyHasScripts attribute
[AttributeUsage(AttributeTargets.Assembly)]
public class AssemblyHasScriptsAttribute : Attribute
{
    public AssemblyHasScriptsAttribute() { }
    public AssemblyHasScriptsAttribute(string[] scripts) { }
    public AssemblyHasScriptsAttribute(Type[] scriptTypes) { }
}

// Signal class (not attribute) - note: this conflicts with Signal attribute,
// but decompiled code uses both patterns
// public class Signal { public Signal(GodotObject owner, StringName name) { } }

// Range control
public class Range : Control
{
    public new class SignalName : Control.SignalName
    {
        public static readonly StringName ValueChanged = "ValueChanged";
    }
    public double Value { get; set; }
    public double MinValue { get; set; }
    public double MaxValue { get; set; } = 100;
    public double Step { get; set; } = 1;
    public double Page { get; set; }
    public float Ratio { get; set; }
}

// _ProcessCustomFX for RichTextEffect needs this signature
// Already defined in UI.cs - just ensure it's virtual

// Input event hierarchy for gestures (as in Godot: InputEventPanGesture → InputEventGesture
// → InputEventWithModifiers → InputEventFromWindow → InputEvent).
public class InputEventFromWindow : InputEvent
{
    public long WindowId { get; set; }
}
public class InputEventWithModifiers : InputEventFromWindow
{
    public bool ShiftPressed { get; set; }
    public bool CtrlPressed { get; set; }
    public bool AltPressed { get; set; }
    public bool MetaPressed { get; set; }
}
public class InputEventGesture : InputEventWithModifiers
{
    public Vector2 Position { get; set; }
}
public class InputEventPanGesture : InputEventGesture
{
    public Vector2 Delta { get; set; }
}

// One virtual screen headless.
public static class DisplayServer
{
    public static int GetScreenCount() => 1;
}

public static class TranslationServer
{
    private static string _locale = "en";
    public static void SetLocale(string locale) => _locale = locale;
    public static string GetLocale() => _locale;
}

// No text shaping headless: fonts are invalid Rids, so glyph lookups find nothing.
public class TextServer : GodotObject
{
    public long FontGetGlyphIndex(Rid fontRid, long size, long @char, long variationSelector) => 0;
    public long FontGetCharFromGlyphIndex(Rid fontRid, long size, long glyphIndex) => 0;
}

public class TextServerManagerInstance : GodotObject
{
    private readonly TextServer _primary = new();
    public TextServer GetPrimaryInterface() => _primary;
}

public static class TextServerManager
{
    public static TextServerManagerInstance Singleton { get; } = new();
}

// Networking (multiplayer transport). Headless runs single-player: hosts cannot be
// created, connections fail, and polling reports no events.
public class PacketPeer : GodotObject
{
    public virtual byte[] GetPacket() => Array.Empty<byte>();
    public virtual Error GetPacketError() => Error.Unavailable;
    public virtual int GetAvailablePacketCount() => 0;
    public virtual Error PutPacket(byte[] buffer) => Error.Unavailable;
}

public class ENetPacketPeer : PacketPeer
{
    public enum PeerState : long
    {
        Disconnected = 0,
        Connecting = 1,
        AcknowledgingConnect = 2,
        ConnectionPending = 3,
        ConnectionSucceeded = 4,
        Connected = 5,
        DisconnectLater = 6,
        Disconnecting = 7,
        AcknowledgingDisconnect = 8,
        Zombie = 9,
    }

    public PeerState GetState() => PeerState.Disconnected;
    public bool IsActive() => false;
    public void PeerDisconnect(int data = 0) { }
    public void PeerDisconnectLater(int data = 0) { }
    public void PeerDisconnectNow(int data = 0) { }
    public void Reset() { }
    public Error Send(int channel, byte[] packet, int flags) => Error.Unavailable;
    public void SetTimeout(int timeout, int timeoutMin, int timeoutMax) { }
}

public class ENetConnection : GodotObject
{
    public enum EventType : long
    {
        Error = -1,
        None = 0,
        Connect = 1,
        Disconnect = 2,
        Receive = 3,
    }

    public Error CreateHost(int maxPeers = 32, int maxChannels = 0, int inBandwidth = 0, int outBandwidth = 0) => Error.CantCreate;
    public Error CreateHostBound(string bindAddress, int bindPort, int maxPeers = 32, int maxChannels = 0, int inBandwidth = 0, int outBandwidth = 0) => Error.CantCreate;
    public ENetPacketPeer? ConnectToHost(string address, int port, int channels = 0, int data = 0) => null;
    /// <summary>[EventType, ENetPacketPeer, data, channel], as Godot returns.</summary>
    public Godot.Collections.Array Service(int timeout = 0) => new() { (long)EventType.None, default(Variant), 0L, 0L };
    public void Flush() { }
    public void Destroy() { }
}
