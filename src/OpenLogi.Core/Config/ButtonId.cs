namespace OpenLogi.Core.Config;

/// <summary>
/// One of the user-rebindable hotspots on a Logi mouse. Order matches the
/// physical layout front-to-side. Variant <em>names</em> are TOML-stable (they
/// are config keys). Ported from Rust <c>binding::ButtonId</c>.
/// </summary>
public enum ButtonId
{
    LeftClick,
    RightClick,
    MiddleClick,
    Back,
    Forward,
    /// <summary>The "ModeShift" button under the wheel. Named DpiToggle for historical reasons.</summary>
    DpiToggle,
    /// <summary>The horizontal thumb wheel's click.</summary>
    Thumbwheel,
    /// <summary>Rotating the thumb wheel "up" (positive rotation).</summary>
    ThumbwheelScrollUp,
    /// <summary>Rotating the thumb wheel "down" (negative rotation).</summary>
    ThumbwheelScrollDown,
    /// <summary>The HID++ gesture button on MX-line devices.</summary>
    GestureButton,
}
