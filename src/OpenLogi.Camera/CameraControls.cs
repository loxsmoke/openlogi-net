namespace OpenLogi.Camera;

/// <summary>
/// One adjustable camera control, mapped to a UVC selector by the backend.
/// Ported from Rust <c>openlogi_camera::CameraControl</c>; the order is the order
/// the UI lists them (lens first, then image).
/// </summary>
public enum CameraControl
{
    Zoom,
    Pan,
    Tilt,
    Focus,
    Exposure,
    PowerLineFrequency,
    LowLightCompensation,
    Brightness,
    Contrast,
    Saturation,
    Sharpness,
    Gain,
    BacklightCompensation,
    WhiteBalance,
    Tint,
}

/// <summary>An auto-mode toggle paired with a manual control (focus / exposure / white balance).</summary>
public enum AutoToggle
{
    Focus,
    Exposure,
    WhiteBalance,
}

public static class CameraControlNames
{
    /// <summary>Every control, in the order the UI lists them.</summary>
    public static readonly CameraControl[] AllControls = Enum.GetValues<CameraControl>();

    /// <summary>Every auto toggle.</summary>
    public static readonly AutoToggle[] AllToggles = Enum.GetValues<AutoToggle>();

    /// <summary>Stable snake_case identifier used for config persistence and the CLI.</summary>
    public static string Name(this CameraControl control) => control switch
    {
        CameraControl.Zoom => "zoom",
        CameraControl.Pan => "pan",
        CameraControl.Tilt => "tilt",
        CameraControl.Focus => "focus",
        CameraControl.Exposure => "exposure",
        CameraControl.PowerLineFrequency => "power_line_frequency",
        CameraControl.LowLightCompensation => "low_light_compensation",
        CameraControl.Brightness => "brightness",
        CameraControl.Contrast => "contrast",
        CameraControl.Saturation => "saturation",
        CameraControl.Sharpness => "sharpness",
        CameraControl.Gain => "gain",
        CameraControl.BacklightCompensation => "backlight_compensation",
        CameraControl.WhiteBalance => "white_balance",
        CameraControl.Tint => "tint",
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    /// <summary>Stable snake_case identifier used for config persistence and the CLI.</summary>
    public static string Name(this AutoToggle toggle) => toggle switch
    {
        AutoToggle.Focus => "focus_auto",
        AutoToggle.Exposure => "exposure_auto",
        AutoToggle.WhiteBalance => "white_balance_auto",
        _ => throw new ArgumentOutOfRangeException(nameof(toggle)),
    };

    /// <summary>The auto-mode toggle that gates this control, if the device has one.</summary>
    public static AutoToggle? AutoToggleFor(this CameraControl control) => control switch
    {
        CameraControl.Focus => AutoToggle.Focus,
        CameraControl.Exposure => AutoToggle.Exposure,
        CameraControl.WhiteBalance => AutoToggle.WhiteBalance,
        _ => null,
    };

    /// <summary>Whether the control belongs to the lens (camera terminal) rather than the image (processing unit).</summary>
    public static bool IsLens(this CameraControl control) =>
        control is CameraControl.Zoom or CameraControl.Pan or CameraControl.Tilt
            or CameraControl.Focus or CameraControl.Exposure;

    public static CameraControl? ParseControl(string name)
    {
        foreach (var c in AllControls)
            if (c.Name() == name)
                return c;
        return null;
    }

    public static AutoToggle? ParseToggle(string name)
    {
        foreach (var t in AllToggles)
            if (t.Name() == name)
                return t;
        return null;
    }
}

/// <summary>The device's reported range and current value for a control.</summary>
/// <param name="ValueMask">
/// Bit <c>n</c> is set when discrete value <c>n</c> is supported. <c>null</c> means
/// every value in the range is available.
/// </param>
public readonly record struct ControlRange(int Min, int Max, int Default, int Current, uint? ValueMask = null)
{
    /// <summary>Whether the device reports <paramref name="value"/> as supported.</summary>
    public bool Supports(int value)
    {
        if (value < Min || value > Max) return false;
        if (ValueMask is not { } mask) return true;
        return value is >= 0 and < 32 && (mask & (1u << value)) != 0;
    }

    /// <summary><paramref name="value"/> pulled into the range.</summary>
    public int Clamp(int value) => Math.Clamp(value, Min, Math.Max(Min, Max));
}

/// <summary>One auto toggle's live and default state, read from the device.</summary>
public readonly record struct AutoState(bool Current, bool Default);

/// <summary>
/// Everything the controls UI needs, read in a single device open: each supported
/// control's range and each supported auto toggle's state.
/// </summary>
public sealed record CameraState(
    IReadOnlyList<(CameraControl Control, ControlRange Range)> Controls,
    IReadOnlyList<(AutoToggle Toggle, AutoState State)> Autos)
{
    public static CameraState Empty { get; } = new([], []);
}

/// <summary>Why a UVC control operation failed.</summary>
public enum ControlError
{
    /// <summary>No matching camera device (or it exposes no controllable unit).</summary>
    NotFound,
    /// <summary>The camera can't be uniquely identified, so a write could hit the wrong device.</summary>
    Ambiguous,
    /// <summary>The camera rejected or doesn't support the control — or the platform has no backend.</summary>
    Unsupported,
    /// <summary>A platform API call failed.</summary>
    Io,
}

public sealed class CameraControlException(ControlError error, string? detail = null)
    : Exception(detail is null ? Describe(error) : $"{Describe(error)}: {detail}")
{
    public ControlError Error { get; } = error;

    private static string Describe(ControlError error) => error switch
    {
        ControlError.NotFound => "no matching UVC device",
        ControlError.Ambiguous => "camera could not be uniquely identified",
        ControlError.Unsupported => "camera does not support that control",
        _ => "platform error",
    };
}
