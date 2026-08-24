using OpenLogi.Core.Localization;

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
        ButtonId.LeftClick => Loc.Current["Button_LeftClick"],
        ButtonId.RightClick => Loc.Current["Button_RightClick"],
        ButtonId.MiddleClick => Loc.Current["Button_MiddleClick"],
        ButtonId.Back => Loc.Current["Button_Back"],
        ButtonId.Forward => Loc.Current["Button_Forward"],
        ButtonId.DpiToggle => Loc.Current["Button_DpiToggle"],
        ButtonId.Thumbwheel => Loc.Current["Button_Thumbwheel"],
        ButtonId.ThumbwheelScrollUp => Loc.Current["Button_ThumbwheelScrollUp"],
        ButtonId.ThumbwheelScrollDown => Loc.Current["Button_ThumbwheelScrollDown"],
        ButtonId.GestureButton => Loc.Current["Button_GestureButton"],
        _ => id.ToString(),
    };
}
