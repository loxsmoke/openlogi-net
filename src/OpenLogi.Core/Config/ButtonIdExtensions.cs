namespace OpenLogi.Core.Config;

public static class ButtonIdExtensions
{
    public static readonly ButtonId[] All =
    [
        ButtonId.LeftClick, ButtonId.RightClick, ButtonId.MiddleClick, ButtonId.Back,
        ButtonId.Forward, ButtonId.DpiToggle, ButtonId.Thumbwheel, ButtonId.ThumbwheelScrollUp,
        ButtonId.ThumbwheelScrollDown, ButtonId.GestureButton,
    ];

    /// <summary>
    /// Whether this button is one the OS hook remaps: Middle, Back, or Forward.
    /// The primary clicks pass through; DPI/thumb/gesture controls are captured
    /// over HID++, not visible to the OS hook.
    /// </summary>
    public static bool IsOsHookButton(this ButtonId id) =>
        id is ButtonId.MiddleClick or ButtonId.Back or ButtonId.Forward;

    /// <summary>Human-readable label for popovers and tooltips.</summary>
    public static string Label(this ButtonId id) => id switch
    {
        ButtonId.LeftClick => "Left Click",
        ButtonId.RightClick => "Right Click",
        ButtonId.MiddleClick => "Middle Click",
        ButtonId.Back => "Back",
        ButtonId.Forward => "Forward",
        ButtonId.DpiToggle => "DPI Toggle",
        ButtonId.Thumbwheel => "Thumb Wheel",
        ButtonId.ThumbwheelScrollUp => "Thumb Wheel Up",
        ButtonId.ThumbwheelScrollDown => "Thumb Wheel Down",
        ButtonId.GestureButton => "Gesture Button",
        _ => id.ToString(),
    };
}
