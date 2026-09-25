// Members sts2.dll references that the stubs lacked, found with scripts/godot_stub_audit
// (checked against the real GodotSharp 4.5.1 signatures). CoreCLR resolves member tokens when
// it JIT-compiles a method, so one missing member fails the whole calling method the first time
// it runs, even when the reference sits in a branch that never executes headless.
//
// Value types and string helpers follow Godot's semantics; anything visual, audio, input or
// engine-service related is a harmless no-op / neutral default.

using System.Runtime.InteropServices;
using Godot.Collections;

namespace Godot
{
    // ─── Core objects ───

    public partial class GodotObject : IDisposable
    {
        public void Dispose() { }
        public Variant CallDeferred(StringName method, params Variant[] args) => default;
        public Error Connect(StringName signal, Callable callable, uint flags = 0u) => Error.Ok;
        public void Disconnect(StringName signal, Callable callable) { }
        public bool IsConnected(StringName signal, Callable callable) => false;
    }

    public partial class Node
    {
        public string SceneFilePath { get; set; } = "";
        public T GetParent<T>() where T : class => (GetParent() as T)!;
        public Node? GetNodeOrNull(NodePath path) => null;
        public NodePath GetPath() => new(GetParent() is { } p ? p.GetPath() + "/" + Name : "/" + Name);
        // Headless nodes never enter a scene tree, so _Ready never runs.
        public bool IsNodeReady() => false;

        public void AddSibling(Node sibling, bool forceReadableName = false) => GetParent()?.AddChild(sibling, forceReadableName);

        public void MoveChild(Node childNode, int toIndex)
        {
            if (!_children.Remove(childNode)) return;
            if (toIndex < 0) toIndex += _children.Count + 1;
            _children.Insert(Math.Clamp(toIndex, 0, _children.Count), childNode);
        }
    }

    public partial class SceneTree
    {
        public Node? GetEditedSceneRoot() => null;
    }

    public static partial class Engine
    {
        public static Dictionary GetVersionInfo() => new()
        {
            [new Variant("major")] = new Variant(4L),
            [new Variant("minor")] = new Variant(5L),
            [new Variant("patch")] = new Variant(1L),
            [new Variant("status")] = new Variant("stable"),
            [new Variant("string")] = new Variant("4.5.1.stable"),
        };

        public static string GetArchitectureName() => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86_32",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };
    }

    public static partial class GD
    {
        public static float Randf() => Random.Shared.NextSingle();
    }

    public static partial class OS
    {
        public static bool IsInLowProcessorUsageMode() => false;
        public static int GetProcessorCount() => Environment.ProcessorCount;
        public static string GetProcessorName() => "";
        public static string GetEnvironment(string variable) => Environment.GetEnvironmentVariable(variable) ?? "";
        public static string GetDistributionName() => "";
        public static string[] GetCmdlineUserArgs() => System.Array.Empty<string>();
        public static string GetLocaleLanguage() => "en";
        public static string GetModelName() => "GenericDevice";
        public static bool IsUserfsPersistent() => true;
        public static bool IsStdOutVerbose() => false;
        public static ulong GetStaticMemoryUsage() => (ulong)GC.GetTotalMemory(false);
        public static ulong GetStaticMemoryPeakUsage() => (ulong)GC.GetTotalMemory(false);
        public static Dictionary GetMemoryInfo() => new();
        public static bool IsSandboxed() => false;
        public static string[] GetGrantedPermissions() => System.Array.Empty<string>();
    }

    public static partial class ResourceLoader
    {
        public enum ThreadLoadStatus : long { InvalidResource, InProgress, Failed, Loaded }

        public static Resource? Load(string path, string typeHint = "", CacheMode cacheMode = CacheMode.Reuse) => null;
        public static Error LoadThreadedRequest(string path, string typeHint = "", bool useSubThreads = false, CacheMode cacheMode = CacheMode.Reuse) => Error.Ok;
        public static ThreadLoadStatus LoadThreadedGetStatus(string path, Godot.Collections.Array? progress = null) => ThreadLoadStatus.InvalidResource;
        public static Resource? LoadThreadedGet(string path) => null;
        public static void AddResourceFormatLoader(ResourceFormatLoader formatLoader, bool atFront = false) { }
    }

    public partial class Resource
    {
        public Resource Duplicate(bool deep = false) => (Resource)MemberwiseClone();
    }

    // ─── Variant / Callable / StringName ───

    public partial struct Variant
    {
        public bool AsBool() => Obj switch
        {
            bool b => b,
            null => false,
            string s => s.Length > 0,
            IConvertible c => Convert.ToDouble(c) != 0,
            _ => true,
        };

        public long AsInt64() => Obj switch
        {
            bool b => b ? 1 : 0,
            IConvertible c and not string => Convert.ToInt64(c),
            string s when long.TryParse(s, out var l) => l,
            _ => 0,
        };

        public double AsDouble() => Obj switch
        {
            bool b => b ? 1 : 0,
            IConvertible c and not string => Convert.ToDouble(c),
            string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };

        public float AsSingle() => (float)AsDouble();
        public string AsString() => Obj?.ToString() ?? "";
        public override string ToString() => AsString();
    }

    public partial struct Callable
    {
        public static Callable From<T0, T1, T2>(Action<T0, T1, T2> action) => new(action);
        public static Callable From<T0, T1, T2, T3>(Action<T0, T1, T2, T3> action) => new(action);
    }

    public sealed partial class StringName
    {
        // Native string names never exist headless; only an empty managed name can match one.
        public static bool operator ==(StringName? left, in NativeInterop.godot_string_name right) => string.IsNullOrEmpty(left);
        public static bool operator !=(StringName? left, in NativeInterop.godot_string_name right) => !(left == right);
        public static bool operator ==(in NativeInterop.godot_string_name left, StringName? right) => right == left;
        public static bool operator !=(in NativeInterop.godot_string_name left, StringName? right) => !(right == left);
    }

    public static partial class StringExtensions
    {
        public static string GetFile(this string instance)
        {
            int sep = Math.Max(instance.LastIndexOf('/'), instance.LastIndexOf('\\'));
            return sep < 0 ? instance : instance[(sep + 1)..];
        }

        public static string GetBaseDir(this string instance)
        {
            // Keep "res://", "user://" and "/" roots intact, like Godot.
            int schemeEnd = instance.IndexOf("://", StringComparison.Ordinal);
            string root = "", rest = instance;
            if (schemeEnd >= 0) { root = instance[..(schemeEnd + 3)]; rest = instance[(schemeEnd + 3)..]; }
            else if (instance.StartsWith('/')) { root = "/"; rest = instance[1..]; }
            int sep = Math.Max(rest.LastIndexOf('/'), rest.LastIndexOf('\\'));
            return sep < 0 ? root : root + rest[..sep];
        }

        // Godot's String::to_snake_case: split camel-case humps, upper-case runs and digit runs.
        public static string ToSnakeCase(this string instance)
        {
            var sb = new System.Text.StringBuilder(instance.Length + 8);
            for (int i = 0; i < instance.Length; i++)
            {
                char c = instance[i];
                if (i > 0)
                {
                    char prev = instance[i - 1];
                    bool isUpper = char.IsUpper(c), wasUpper = char.IsUpper(prev);
                    bool isDigit = char.IsDigit(c), wasDigit = char.IsDigit(prev);
                    bool nextLower = i + 1 < instance.Length && char.IsLower(instance[i + 1]);
                    bool split = (isUpper && !wasUpper && !wasDigit)
                                 || (wasUpper && isUpper && nextLower)
                                 || (isDigit && !wasDigit)
                                 || (!isDigit && wasDigit);
                    if (split && prev != '_' && c != '_') sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        // Godot's String::capitalize: "snake_case" / "CamelCase" -> "Snake Case" / "Camel Case".
        public static string Capitalize(this string instance)
        {
            var words = instance.ToSnakeCase().Replace(' ', '_')
                .Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
            return string.Join(" ", words);
        }
    }

    // ─── Math ───

    public partial struct Transform2D
    {
        public Transform2D(Vector2 xAxis, Vector2 yAxis, Vector2 originPos)
        {
            X = xAxis; Y = yAxis; Origin = originPos;
        }

        public readonly Vector2 BasisXform(Vector2 v) => X * v.X + Y * v.Y;

        public static Vector2 operator *(Transform2D transform, Vector2 vector) => transform.BasisXform(vector) + transform.Origin;

        // this * Transform2D(angle, 0): rotate the basis in local space.
        public readonly Transform2D RotatedLocal(float angle)
        {
            float c = MathF.Cos(angle), s = MathF.Sin(angle);
            return new Transform2D(X * c + Y * s, X * -s + Y * c, Origin);
        }

        public readonly Transform2D TranslatedLocal(Vector2 offset) => new(X, Y, Origin + BasisXform(offset));
    }

    public static partial class Mathf
    {
        private const double DbPerNeper = 8.6858896380650365530225783783321;
        public static double Sin(double s) => Math.Sin(s);
        public static float Atan2(float y, float x) => MathF.Atan2(y, x);
        public static double Atan2(double y, double x) => Math.Atan2(y, x);
        public static float LinearToDb(float linear) => (float)(Math.Log(linear) * DbPerNeper);
        public static double LinearToDb(double linear) => Math.Log(linear) * DbPerNeper;
        public static float Log(float s) => MathF.Log(s);
        public static double Log(double s) => Math.Log(s);
    }

    public static partial class Colors
    {
        public static Color DarkRed => new(0.545098f, 0f, 0f);
    }

    /// <summary>Opaque server resource handle.</summary>
    public readonly struct Rid : IEquatable<Rid>
    {
        public ulong Id { get; }
        public Rid(ulong id) => Id = id;
        public bool IsValid => Id != 0;
        public bool Equals(Rid other) => Id == other.Id;
        public override bool Equals(object? obj) => obj is Rid r && Equals(r);
        public override int GetHashCode() => Id.GetHashCode();
        public static bool operator ==(Rid left, Rid right) => left.Id == right.Id;
        public static bool operator !=(Rid left, Rid right) => left.Id != right.Id;
    }

    // ─── Files ───

    public partial class FileAccess
    {
        public static Error GetOpenError() => Error.FileNotFound;
        public Error GetError() => Error.Ok;
        public bool IsOpen() => false;
        public void Flush() { }
        public void Seek(ulong position) { }
        public ulong GetPosition() => 0;
        public ulong GetLength() => 0;
        public byte[] GetBuffer(long length) => System.Array.Empty<byte>();
        public bool StoreBuffer(byte[] buffer) => false;
        public static long GetSize(string file) => File.Exists(file) ? new FileInfo(file).Length : -1;
        public static ulong GetModifiedTime(string file) =>
            File.Exists(file) ? (ulong)new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds() : 0;
    }

    public partial class DirAccess
    {
        private string[]? _listing;
        private int _listingIndex;

        public bool IncludeHidden { get; set; }

        public Error ListDirBegin()
        {
            try
            {
                _listing = Directory.GetDirectories(_path).Select(d => Path.GetFileName(d) + "/")
                    .Concat(Directory.GetFiles(_path).Select(Path.GetFileName)).ToArray()!;
                _listingIndex = 0;
                return Error.Ok;
            }
            catch { _listing = null; return Error.Failed; }
        }

        public string GetNext() =>
            _listing != null && _listingIndex < _listing.Length ? _listing[_listingIndex++].TrimEnd('/') : "";

        public bool CurrentIsDir() => _listing != null && _listingIndex > 0 && _listing[_listingIndex - 1].EndsWith('/');

        public Error Remove(string path) => RemoveAbsolute(Path.Combine(_path, path));

        public static string[] GetFilesAt(string path)
        {
            try { return Directory.GetFiles(path).Select(Path.GetFileName).ToArray()!; }
            catch { return System.Array.Empty<string>(); }
        }

        public static string[] GetDirectoriesAt(string path)
        {
            try { return Directory.GetDirectories(path).Select(Path.GetFileName).ToArray()!; }
            catch { return System.Array.Empty<string>(); }
        }

        public static Error CopyAbsolute(string from, string to, int chmodFlags = -1)
        {
            try { File.Copy(from, to, overwrite: true); return Error.Ok; }
            catch { return Error.Failed; }
        }

        public static Error RenameAbsolute(string from, string to)
        {
            try
            {
                if (Directory.Exists(from)) Directory.Move(from, to);
                else File.Move(from, to, overwrite: true);
                return Error.Ok;
            }
            catch { return Error.Failed; }
        }

        public static Error RemoveAbsolute(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path);
                else if (File.Exists(path)) File.Delete(path);
                else return Error.FileNotFound;
                return Error.Ok;
            }
            catch { return Error.Failed; }
        }
    }

    // ─── Visuals, audio, input: no-ops ───

    public partial class CanvasItem
    {
        public Material? Material { get; set; }
        public void SetMaterial(Material material) => Material = material;
        public Material? GetMaterial() => Material;
        public Transform2D GetGlobalTransformWithCanvas() => Transform2D.Identity;
    }

    public partial class Control
    {
        public NodePath FocusNeighborBottom { get; set; } = new();
        public NodePath FocusNeighborLeft { get; set; } = new();
        public NodePath FocusNeighborRight { get; set; } = new();
        public NodePath FocusNeighborTop { get; set; } = new();
        public void SetAnchorsPreset(LayoutPreset preset, bool keepOffsets = false) { }
        public void SetFocusMode(FocusModeEnum mode) => FocusMode = mode;
        public void AddThemeFontOverride(StringName name, Font font) { }
    }

    public partial class TextureRect
    {
        public enum ExpandModeEnum : long { KeepSize, IgnoreSize, FitWidth, FitWidthProportional, FitHeight, FitHeightProportional }
        public ExpandModeEnum ExpandMode { get; set; }
    }

    public partial class AtlasTexture
    {
        public Rect2 Margin { get; set; }
    }

    public partial class ImageTexture
    {
        public static ImageTexture CreateFromImage(Image image) => new();
    }

    public partial class Image
    {
        public static Image? LoadFromFile(string path) => null;
    }

    public partial class GpuParticles2D
    {
        public void Restart() { }
        public void Restart(bool keepSeed) { }
    }

    public partial class CharFXTransform
    {
        public Vector2I Range { get; set; }
        public uint GlyphIndex { get; set; }
        public Rid Font { get; set; }
        public void SetGlyphIndex(uint glyphIndex) => GlyphIndex = glyphIndex;
    }

    public partial class FastNoiseLite
    {
        public enum NoiseTypeEnum : long { Value = 5L, ValueCubic = 4L, Perlin = 3L, Cellular = 2L, Simplex = 0L, SimplexSmooth = 1L }
        public NoiseTypeEnum NoiseType { get; set; } = NoiseTypeEnum.SimplexSmooth;
        public int Seed { get; set; }
        public int FractalOctaves { get; set; } = 5;
        public float FractalGain { get; set; } = 0.5f;
    }

    public partial class AudioStreamPlayer
    {
        public StringName Bus { get; set; } = "Master";
        public float PitchScale { get; set; } = 1f;
        public float VolumeLinear { get; set; } = 1f;
        public bool IsPlaying() => false;
    }

    public partial class RenderingServer
    {
        public enum RenderingInfo : long
        {
            TotalObjectsInFrame, TotalPrimitivesInFrame, TotalDrawCallsInFrame, TextureMemUsed, BufferMemUsed,
            VideoMemUsed, PipelineCompilationsCanvas, PipelineCompilationsMesh, PipelineCompilationsSurface,
            PipelineCompilationsDraw, PipelineCompilationsSpecialization,
        }

        public static ulong GetRenderingInfo(RenderingInfo info) => 0;
        public static RenderingDevice? GetRenderingDevice() => null;
    }

    public class RenderingDevice : GodotObject
    {
        public string GetDeviceName() => "headless";
    }

    public static class DisplayServer
    {
        public enum ScreenOrientation : long { Landscape, Portrait, ReverseLandscape, ReversePortrait, SensorLandscape, SensorPortrait, Sensor }

        public static string GetName() => "headless";
        public static int GetScreenCount() => 1;
        public static int GetPrimaryScreen() => 0;
        public static Vector2I ScreenGetSize(int screen = -1) => new(1920, 1080);
        public static int ScreenGetDpi(int screen = -1) => 96;
        public static float ScreenGetScale(int screen = -1) => 1f;
        public static float ScreenGetRefreshRate(int screen = -1) => 60f;
        public static ScreenOrientation ScreenGetOrientation(int screen = -1) => ScreenOrientation.Landscape;
    }

    public class TextServer : GodotObject
    {
        public long FontGetGlyphIndex(Rid fontRid, long size, long @char, long variationSelector) => @char;
        public long FontGetCharFromGlyphIndex(Rid fontRid, long size, long glyphIndex) => glyphIndex;
    }

    public class TextServerManagerInstance : GodotObject
    {
        public TextServer GetPrimaryInterface() => new();
    }

    public class AudioServerInstance : GodotObject
    {
        public int GetBusIndex(StringName busName) => -1;
        public void SetBusVolumeDb(int busIdx, float volumeDb) { }
    }

    public enum JoyAxis : long
    {
        Invalid = -1L, LeftX = 0L, LeftY = 1L, RightX = 2L, RightY = 3L, TriggerLeft = 4L, TriggerRight = 5L, SdlMax = 6L, Max = 10L,
    }

    public partial class InputEvent
    {
        public int Device { get; set; }
    }

    public partial class InputEventKey
    {
        public Key Keycode { get; set; }
        public bool Pressed { get; set; }
    }

    public partial class InputEventMouseButton
    {
        public MouseButton ButtonIndex { get; set; }
    }

    public partial class InputEventAction
    {
        public bool Pressed { get; set; }
    }

    public class InputEventPanGesture : InputEvent
    {
        public Vector2 Delta { get; set; }
    }

    // ─── Networking (multiplayer transport; unused in single-player headless runs) ───

    // Hosting/joining fails cleanly: no host is ever created and no events arrive.
    public class ENetConnection : GodotObject
    {
        public enum EventType : long { Error = -1L, None, Connect, Disconnect, Receive }

        public Error CreateHost(int maxPeers = 32, int maxChannels = 0, int inBandwidth = 0, int outBandwidth = 0) => Error.CantCreate;
        public Error CreateHostBound(string bindAddress, int bindPort, int maxPeers = 32, int maxChannels = 0, int inBandwidth = 0, int outBandwidth = 0) => Error.CantCreate;
        public ENetPacketPeer? ConnectToHost(string address, int port, int channels = 0, int data = 0) => null;
        public Godot.Collections.Array Service(int timeout = 0) => new() { new Variant((long)EventType.None), default, default, default };
        public void Flush() { }
        public void Destroy() { }
    }

    public class ENetPacketPeer : GodotObject
    {
        public enum PeerState : long
        {
            Disconnected, Connecting, AcknowledgingConnect, ConnectionPending, ConnectionSucceeded,
            Connected, DisconnectLater, Disconnecting, AcknowledgingDisconnect, Zombie,
        }

        public PeerState GetState() => PeerState.Disconnected;
        public bool IsActive() => false;
        public Error Send(int channel, byte[] packet, int flags) => Error.Unavailable;
        public void SetTimeout(int timeout, int timeoutMin, int timeoutMax) { }
        public void PeerDisconnect(int data = 0) { }
        public void PeerDisconnectLater(int data = 0) { }
        public void PeerDisconnectNow(int data = 0) { }
        public void Reset() { }
    }
}

namespace Godot.NativeInterop
{
    public static partial class VariantUtils
    {
        public static T ConvertTo<T>(in godot_variant variant) => default!;
        public static godot_variant CreateFrom<T>(scoped in T from) => default;
    }
}
