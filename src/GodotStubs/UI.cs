namespace Godot;

// CanvasItem
public class CanvasItem : Node
{
    public new class SignalName : Node.SignalName
    {
        public static readonly StringName Draw = "draw";
        public static readonly StringName VisibilityChanged = "visibility_changed";
        public static readonly StringName Hidden = "hidden";
        public static readonly StringName ItemRectChanged = "item_rect_changed";
    }

    public Color Modulate { get; set; } = Color.White;
    public Color SelfModulate { get; set; } = Color.White;
    public bool Visible { get; set; } = true;
    public virtual void Show() => Visible = true;
    public virtual void Hide() => Visible = false;
    public bool IsVisibleInTree() => Visible;
    public Material? Material { get; set; }
    public virtual Transform2D GetGlobalTransform() => Transform2D.Identity;
    public Transform2D GetGlobalTransformWithCanvas() => GetGlobalTransform();
    // Method forms of the properties above, called by monster visual hooks (e.g. Crusher, TestSubject).
    public void SetVisible(bool visible) => Visible = visible;
    public bool IsVisible() => Visible;
    public void SetSelfModulate(Color color) => SelfModulate = color;
    public int ZIndex { get; set; }
    public bool UseParentMaterial { get; set; }
    public void MoveToFront() { }
    public Tween CreateTween() => new Tween();
    public Rect2 GetViewportRect() => new Rect2(Vector2.Zero, new Vector2(1920, 1080));
}

// Control
public class Control : CanvasItem
{
    public enum FocusModeEnum : long
    {
        None = 0,
        Click = 1,
        All = 2,
        Accessibility = 3,
    }
    public enum MouseFilterEnum : long
    {
        Stop = 0,
        Pass = 1,
        Ignore = 2,
    }
    public enum LayoutPreset : long
    {
        TopLeft = 0,
        TopRight = 1,
        BottomLeft = 2,
        BottomRight = 3,
        CenterLeft = 4,
        CenterTop = 5,
        CenterRight = 6,
        CenterBottom = 7,
        Center = 8,
        LeftWide = 9,
        TopWide = 10,
        RightWide = 11,
        BottomWide = 12,
        VcenterWide = 13,
        HcenterWide = 14,
        FullRect = 15,
    }

    public new class MethodName : Node.MethodName { }
    public new class PropertyName : Node.PropertyName { }
    public new class SignalName : CanvasItem.SignalName
    {
        public static readonly StringName FocusEntered = "FocusEntered";
        public static readonly StringName FocusExited = "FocusExited";
        public static readonly StringName MouseEntered = "MouseEntered";
        public static readonly StringName MouseExited = "MouseExited";
        public static readonly StringName Resized = "Resized";
    }

    public Vector2 Position { get; set; }
    public Vector2 GlobalPosition { get; set; }
    public Vector2 Size { get; set; }
    public Vector2 CustomMinimumSize { get; set; }
    public float Rotation { get; set; }
    public Vector2 Scale { get; set; } = Vector2.One;
    public Vector2 PivotOffset { get; set; }
    public FocusModeEnum FocusMode { get; set; }
    public MouseFilterEnum MouseFilter { get; set; }
    public string TooltipText { get; set; } = "";

    public Rect2 GetViewportRect() => new Rect2(0, 0, 1920, 1080);
    public void GrabFocus() { }
    public void ReleaseFocus() { }
    public bool HasFocus() => false;
    public Viewport? GetViewport() => null;

    public virtual void _GuiInput(InputEvent @event) { }

    public NodePath FocusNeighborBottom { get; set; } = new();
    public NodePath FocusNeighborLeft { get; set; } = new();
    public NodePath FocusNeighborRight { get; set; } = new();
    public NodePath FocusNeighborTop { get; set; } = new();
    public void SetFocusMode(FocusModeEnum mode) => FocusMode = mode;
    public void SetAnchorsPreset(LayoutPreset preset, bool keepOffsets = false) { }
    public void AddThemeFontOverride(StringName name, Font font) { }
    public override Transform2D GetGlobalTransform() => new(Rotation, Scale, 0, GlobalPosition);
}

// Node2D
public class Node2D : CanvasItem
{
    public new class MethodName : Node.MethodName { }
    public new class PropertyName : Node.PropertyName { }
    public new class SignalName : CanvasItem.SignalName { }

    public Vector2 Position { get; set; }
    public Vector2 GlobalPosition { get; set; }
    public float Rotation { get; set; }
    public float RotationDegrees { get; set; }
    public Vector2 Scale { get; set; } = Vector2.One;
    public Transform2D GlobalTransform { get; set; } = Transform2D.Identity;
    public Transform2D Transform { get; set; } = Transform2D.Identity;
    public override Transform2D GetGlobalTransform() => GlobalTransform;
}

// Resource
public class Resource : GodotObject
{
    public string ResourcePath { get; set; } = "";
    public new class MethodName : GodotObject.MethodName { }
    public new class PropertyName : GodotObject.PropertyName { }
    public new class SignalName : GodotObject.SignalName { }

    // Shallow copy; nested resources are shared even when deep (no sub-resources headless).
    public Resource Duplicate(bool deep = false) => (Resource)MemberwiseClone();
}

// PackedScene
public class PackedScene : Resource
{
    public enum GenEditState : long
    {
        Disabled = 0,
        Instance = 1,
        Main = 2,
        MainInherited = 3,
    }
    public T Instantiate<T>(GenEditState editState = GenEditState.Disabled) where T : Node, new() => new T();
    public Node Instantiate(GenEditState editState = GenEditState.Disabled) => new Node();
}

// Texture types
public class Texture2D : Resource { }
public class CompressedTexture2D : Texture2D { }
public class AtlasTexture : Texture2D
{
    public Rect2 Region { get; set; }
    public Rect2 Margin { get; set; }
    public Texture2D? Atlas { get; set; }
}
public class ImageTexture : Texture2D { }

// Material types
public class Material : Resource { }
public class ShaderMaterial : Material
{
    public void SetShaderParameter(StringName param, Variant value) { }
    public Variant GetShaderParameter(StringName param) => default;
}
public class Shader : Resource { }

// Curve
public class Curve : Resource
{
    public float Sample(float offset) => 0f;
}

// Tween
public class Tween : GodotObject
{
    public enum EaseType : long
    {
        In = 0,
        Out = 1,
        InOut = 2,
        OutIn = 3,
    }
    public enum TransitionType : long
    {
        Linear = 0,
        Sine = 1,
        Quint = 2,
        Quart = 3,
        Quad = 4,
        Expo = 5,
        Elastic = 6,
        Cubic = 7,
        Circ = 8,
        Bounce = 9,
        Back = 10,
        Spring = 11,
    }

    public new class SignalName : GodotObject.SignalName
    {
        public static readonly StringName Finished = "finished";
    }

    public event Action? Finished;

    public PropertyTweener TweenProperty(GodotObject obj, NodePath property, Variant finalVal, double duration) => new();
    public CallbackTweener TweenCallback(Callable callback) => new();
    public MethodTweener TweenMethod(Callable method, Variant from, Variant to, double duration) => new();
    public IntervalTweener TweenInterval(double time) => new();
    public Tween Parallel() => this;
    public Tween SetParallel(bool parallel = true) => this;
    public Tween SetLoops(int loops = 0) => this;
    public Tween SetEase(EaseType ease) => this;
    public Tween SetTrans(TransitionType trans) => this;
    public Tween Chain() => this;
    public bool CustomStep(double delta) { Finished?.Invoke(); return true; }
    public void Play() { Finished?.Invoke(); }
    public void Stop() { }
    public void Pause() { }
    public void Kill() { }
    public bool IsRunning() => false;
    public bool IsValid() => true;
    public Tween BindNode(Node node) => this;
    public Tween SetSpeedScale(float scale) => this;
    public Tween SetProcessMode(TweenProcessMode mode) => this;
    public enum TweenProcessMode : long
    {
        Physics = 0,
        Idle = 1,
    }
}

public class PropertyTweener
{
    public PropertyTweener From(Variant value) => this;
    public PropertyTweener SetEase(Tween.EaseType ease) => this;
    public PropertyTweener SetTrans(Tween.TransitionType trans) => this;
    public PropertyTweener SetDelay(double delay) => this;
    public PropertyTweener AsRelative() => this;
}

public class CallbackTweener
{
    public CallbackTweener SetDelay(double delay) => this;
}

public class MethodTweener
{
    public MethodTweener SetEase(Tween.EaseType ease) => this;
    public MethodTweener SetTrans(Tween.TransitionType trans) => this;
    public MethodTweener SetDelay(double delay) => this;
}

public class IntervalTweener { }

// UI Controls
public class TextureRect : Control
{
    public enum ExpandModeEnum : long
    {
        KeepSize = 0,
        IgnoreSize = 1,
        FitWidth = 2,
        FitWidthProportional = 3,
        FitHeight = 4,
        FitHeightProportional = 5,
    }

    public new class MethodName : Control.MethodName { }
    public new class PropertyName : Control.PropertyName { }
    public new class SignalName : Control.SignalName { }
    public Texture2D? Texture { get; set; }
    public ExpandModeEnum ExpandMode { get; set; }
}

public class ColorRect : Control
{
    public Color Color { get; set; }
}

public class Panel : Control { }
public class PanelContainer : Control { }

public class Container : Control { }
public class BoxContainer : Container { }
public class VBoxContainer : BoxContainer { }
public class HBoxContainer : BoxContainer { }
public class FlowContainer : Container { }
public class HFlowContainer : FlowContainer { }
public class GridContainer : Container
{
    public int Columns { get; set; }
}
public class MarginContainer : Container { }
public class CenterContainer : Container { }
public class ScrollContainer : Container { }
public class SubViewportContainer : Container { }
public class SubViewport : Viewport { }

public class Label : Control
{
    public new class MethodName : Control.MethodName { }
    public new class PropertyName : Control.PropertyName { }
    public new class SignalName : Control.SignalName { }

    public string Text { get; set; } = "";
}

public class RichTextLabel : Control
{
    public new class MethodName : Control.MethodName { }
    public new class PropertyName : Control.PropertyName { }
    public new class SignalName : Control.SignalName { }

    public string Text { get; set; } = "";
    public void Clear() { Text = ""; }
    public void AppendText(string text) { Text += text; }
    public void AddText(string text) { Text += text; }
}

public class Button : Control
{
    public new class SignalName : Control.SignalName
    {
        public static readonly StringName Pressed = "Pressed";
    }
    public string Text { get; set; } = "";
    public event Action? Pressed;
}

public class BaseButton : Control
{
    public new class SignalName : Control.SignalName
    {
        public static readonly StringName Pressed = "Pressed";
    }
}

public class CheckBox : Button { }
public class CheckButton : Button { }

public class OptionButton : Button
{
    public int Selected { get; set; }
    public void AddItem(string label, int id = -1) { }
    public void Select(int idx) { Selected = idx; }
}

public class LineEdit : Control
{
    public string Text { get; set; } = "";
    public string PlaceholderText { get; set; } = "";
    public new class SignalName : Control.SignalName
    {
        public static readonly StringName TextChanged = "TextChanged";
        public static readonly StringName TextSubmitted = "TextSubmitted";
    }
}

public class TextEdit : Control
{
    public string Text { get; set; } = "";
}

public class SpinBox : Control
{
    public double Value { get; set; }
}

public class Slider : Control
{
    public double Value { get; set; }
}
public class HSlider : Slider { }
public class VSlider : Slider { }

public class ScrollBar : Control
{
    public double Value { get; set; }
}
public class HScrollBar : ScrollBar { }
public class VScrollBar : ScrollBar { }

public class Separator : Control { }
public class HSeparator : Separator { }
public class VSeparator : Separator { }

public class TabContainer : Container { }

// Timer
public class Timer : Node
{
    public double WaitTime { get; set; } = 1.0;
    public bool OneShot { get; set; }
    public bool Autostart { get; set; }
    public event Action? Timeout;
    public void Start(double timeSec = -1) { Timeout?.Invoke(); }
    public void Stop() { }
}

// Audio
public class AudioStream : Resource { }
public class AudioStreamPlayer : Node
{
    public new class SignalName : Node.SignalName
    {
        public static readonly StringName Finished = "Finished";
    }
    public AudioStream? Stream { get; set; }
    public float VolumeDb { get; set; }
    public void Play(float fromPosition = 0) { }
    public void Stop() { }
}

// Input
public class InputEvent : Resource
{
    public virtual bool IsActionPressed(StringName action, bool allowEcho = false) => false;
    public virtual bool IsActionReleased(StringName action) => false;
    public bool IsPressed() => false;
    public bool IsReleased() => true;
}
public class InputEventKey : InputEvent { }
public class InputEventMouse : InputEventWithModifiers
{
    public Vector2 Position { get; set; }
    public Vector2 GlobalPosition { get; set; }
}
public class InputEventMouseButton : InputEventMouse
{
    public MouseButton ButtonIndex { get; set; }
    public float Factor { get; set; } = 1f;
    public bool DoubleClick { get; set; }
}
public class InputEventMouseMotion : InputEventMouse
{
    public Vector2 Relative { get; set; }
    public Vector2 Velocity { get; set; }
}

// FileAccess
// Open never succeeds headless (no user:// or res:// filesystem), and GetOpenError says
// FileNotFound so callers such as Saves.GodotFileIo take their "no such file" path.
// Static queries on real paths go to System.IO, like FileExists.
public class FileAccess : GodotObject
{
    public enum ModeFlags : long
    {
        Read = 1,
        Write = 2,
        ReadWrite = 3,
        WriteRead = 7,
    }
    public static FileAccess? Open(string path, ModeFlags flags) => null;
    public static Error GetOpenError() => Error.FileNotFound;
    public static bool FileExists(string path) => File.Exists(path);
    public static ulong GetModifiedTime(string file) =>
        File.Exists(file) ? (ulong)new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds() : 0;
    public static long GetSize(string file) => File.Exists(file) ? new FileInfo(file).Length : -1;

    public bool IsOpen() => false;
    public Error GetError() => Error.Unavailable;
    public ulong GetLength() => 0;
    public ulong GetPosition() => 0;
    public void Seek(ulong position) { }
    public byte[] GetBuffer(long length) => Array.Empty<byte>();
    public string GetAsText(bool skipCr = false) => "";
    public bool StoreBuffer(byte[] buffer) => false;
    public bool StoreString(string str) => false;
    public void Flush() { }
    public void Close() { }
}

// DirAccess
public class DirAccess : GodotObject
{
    private static Error Try(Action io)
    {
        try { io(); return Error.Ok; }
        catch (FileNotFoundException) { return Error.FileNotFound; }
        catch (DirectoryNotFoundException) { return Error.FileNotFound; }
        catch (UnauthorizedAccessException) { return Error.FileNoPermission; }
        catch { return Error.Failed; }
    }

    public static bool DirExistsAbsolute(string path) => Directory.Exists(path);
    public static Error MakeDirAbsolute(string path) => Try(() => Directory.CreateDirectory(path));
    public static Error MakeDirRecursiveAbsolute(string path) => Try(() => Directory.CreateDirectory(path));
    public static string[] GetFilesAt(string path) =>
        Directory.Exists(path) ? Directory.GetFiles(path).Select(p => Path.GetFileName(p)).ToArray() : Array.Empty<string>();
    public static string[] GetDirectoriesAt(string path) =>
        Directory.Exists(path) ? Directory.GetDirectories(path).Select(p => Path.GetFileName(p)).ToArray() : Array.Empty<string>();
    public static Error RemoveAbsolute(string path)
    {
        if (File.Exists(path)) return Try(() => File.Delete(path));
        if (Directory.Exists(path)) return Try(() => Directory.Delete(path));
        return Error.FileNotFound;
    }
    public static Error RenameAbsolute(string from, string to)
    {
        if (Directory.Exists(from)) return Try(() => Directory.Move(from, to));
        return File.Exists(from) ? Try(() => File.Move(from, to, overwrite: true)) : Error.FileNotFound;
    }
    public static Error CopyAbsolute(string from, string to, int chmodFlags = -1) =>
        File.Exists(from) ? Try(() => File.Copy(from, to, overwrite: true)) : Error.FileNotFound;
    public static DirAccess? Open(string path) => Directory.Exists(path) ? new DirAccess(path) : null;

    private readonly string _path;
    private string[]? _listing;
    private int _listIndex;
    private bool _currentIsDir;

    private DirAccess(string path) { _path = path; }
    public DirAccess() { _path = ""; }

    public bool IncludeHidden { get; set; }
    public bool IncludeNavigational { get; set; }

    public Error MakeDirRecursive(string path) => Try(() => Directory.CreateDirectory(Path.Combine(_path, path)));
    public string[] GetFiles() => GetFilesAt(_path);
    public string[] GetDirectories() => GetDirectoriesAt(_path);
    public Error Remove(string path) => RemoveAbsolute(Path.Combine(_path, path));

    public Error ListDirBegin()
    {
        if (!Directory.Exists(_path)) return Error.CantOpen;
        _listing = Directory.GetFileSystemEntries(_path)
            .Select(p => Path.GetFileName(p))
            .Where(n => IncludeHidden || !n.StartsWith('.'))
            .ToArray();
        _listIndex = 0;
        return Error.Ok;
    }

    public string GetNext()
    {
        if (_listing == null || _listIndex >= _listing.Length)
        {
            _currentIsDir = false;
            return "";
        }
        string name = _listing[_listIndex++];
        _currentIsDir = Directory.Exists(Path.Combine(_path, name));
        return name;
    }

    public bool CurrentIsDir() => _currentIsDir;
    public void ListDirEnd() => _listing = null;
}

// Animation
public class AnimationPlayer : Node
{
    public new class SignalName : Node.SignalName
    {
        public static readonly StringName AnimationFinished = "AnimationFinished";
    }
    public void Play(StringName name = default, double customBlend = -1, float customSpeed = 1f, bool fromEnd = false) { }
    public void Stop(bool keepState = false) { }
}

// Particles
public class GpuParticles2D : Node2D
{
    public bool Emitting { get; set; }
    public Material? ProcessMaterial { get; set; }
    // build 23372702 sets Amount on enemy/VFX particles (e.g. BygoneEffigy); without
    // this no-op stub the JIT throws "Method not found: set_Amount" mid enemy turn,
    // derailing the async turn continuation and forcing the EndTurn nuclear fallback.
    public int Amount { get; set; } = 1;
    public double Lifetime { get; set; } = 1.0;
    public bool OneShot { get; set; }
    public float Explosiveness { get; set; }
    public Texture2D? Texture { get; set; }
    public void Restart() { }
}

// Sprite
public class Sprite2D : Node2D
{
    public Texture2D? Texture { get; set; }
}

// CharFXTransform for RichTextEffects
public class CharFXTransform : GodotObject
{
    public Color Color { get; set; } = Color.White;
    public Vector2 Offset { get; set; }
    public Transform2D Transform { get; set; }
    public bool Visible { get; set; } = true;
    public double ElapsedTime { get; set; }
    public int RelativeIndex { get; set; }
    public Godot.Collections.Dictionary Env { get; set; } = new();
    public Rid Font { get; set; }
    public uint GlyphIndex { get; set; }
}

// RichTextEffect
public class RichTextEffect : Resource
{
    public new class SignalName { }
    public virtual bool _ProcessCustomFX(CharFXTransform charFx) => false;
}

// ResourceFormatLoader
public class ResourceFormatLoader : GodotObject
{
    public new class MethodName : GodotObject.MethodName { }
    public new class PropertyName : GodotObject.PropertyName { }
    public new class SignalName : GodotObject.SignalName { }

    public virtual Variant _Load(string path, string originalPath, bool useSubThreads, int cacheMode) => default;
    public virtual string[] _GetRecognizedExtensions() => Array.Empty<string>();
    public virtual bool _HandlesType(StringName type) => false;
    public virtual string _GetResourceType(string path) => "";
    public virtual bool _RecognizePath(string path, StringName type) => false;
    public virtual string[] _GetDependencies(string path, bool addTypes) => Array.Empty<string>();
    public virtual bool _Exists(string path) => false;
}

// Image
public class Image : Resource
{
    public enum Format : long
    {
        L8 = 0,
        La8 = 1,
        R8 = 2,
        Rg8 = 3,
        Rgb8 = 4,
        Rgba8 = 5,
        Rgba4444 = 6,
        Rgb565 = 7,
        Rf = 8,
        Rgf = 9,
        Rgbf = 10,
        Rgbaf = 11,
        Rh = 12,
        Rgh = 13,
        Rgbh = 14,
        Rgbah = 15,
        Rgbe9995 = 16,
        Dxt1 = 17,
        Dxt3 = 18,
        Dxt5 = 19,
        RgtcR = 20,
        RgtcRg = 21,
        BptcRgba = 22,
        BptcRgbf = 23,
        BptcRgbfu = 24,
        Etc = 25,
        Etc2R11 = 26,
        Etc2R11S = 27,
        Etc2Rg11 = 28,
        Etc2Rg11S = 29,
        Etc2Rgb8 = 30,
        Etc2Rgba8 = 31,
        Etc2Rgb8A1 = 32,
        Etc2RaAsRg = 33,
        Dxt5RaAsRg = 34,
        Astc4X4 = 35,
        Astc4X4Hdr = 36,
        Astc8X8 = 37,
        Astc8X8Hdr = 38,
        Max = 39,
    }
    public static Image CreateEmpty(int width, int height, bool useMipmaps, Format format) => new();
    public void SetPixel(int x, int y, Color color) { }
}
