using OpenLogi.Core.Actions;
using OpenLogi.Core.Gestures;

namespace OpenLogi.Core.Config;

/// <summary>Settings scoped to a single physical device.</summary>
public sealed class DeviceConfig
{
    public GestureOwner? GestureOwner { get; set; }
    public DeviceIdentity? Identity { get; set; }
    public SortedDictionary<ButtonId, Binding> Bindings { get; set; } = new();
    public SortedDictionary<string, SortedDictionary<ButtonId, Actions.MouseAction>> PerAppBindings { get; set; } = new(StringComparer.Ordinal);
    public List<uint> DpiPresets { get; set; } = [];
    public uint? Dpi { get; set; }
    public Lighting? Lighting { get; set; }
    public SmartShift? SmartShift { get; set; }
    public bool InvertScroll { get; set; }
    /// <summary>Hi-res smooth scrolling (HiResWheel 0x2121 diverted + re-injected while the agent runs).</summary>
    public bool SmoothScroll { get; set; }

    /// <summary>
    /// Per-webcam UVC image controls (snake_case control name → value; auto toggles
    /// as 0/1). Null until the user adjusts one, so it stays out of the file otherwise.
    /// </summary>
    public SortedDictionary<string, int>? CameraControls { get; set; }
    /// <summary>User-saved camera profiles (name → control snapshot). Built-ins live in the GUI.</summary>
    public SortedDictionary<string, SortedDictionary<string, int>> CameraProfiles { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The camera profile last applied from the GUI, highlighted on reopen.</summary>
    public string? CameraProfile { get; set; }
    /// <summary>
    /// The resolution and frame rate the preview uses with exclusive access (the
    /// Rust app's preview mode, same key). A shared preview cannot choose one.
    /// </summary>
    public CameraStreamMode? CameraStreamMode { get; set; }
}

/// <summary>
/// The frame size and rate a webcam's live preview asks for when it opens. Not a
/// UVC control: nothing is written to the camera, and other apps negotiate their
/// own, so it lives beside <see cref="DeviceConfig.CameraControls"/>, not in it.
/// </summary>
public sealed record CameraStreamMode(uint Width, uint Height, uint Fps);
