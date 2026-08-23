using OpenLogi.Core.Localization;

namespace OpenLogi.Core.Gestures;

public static class GestureDirectionExtensions
{
    public static readonly GestureDirection[] All =
        [GestureDirection.Up, GestureDirection.Down, GestureDirection.Left, GestureDirection.Right, GestureDirection.Click];

    public static string Label(this GestureDirection d) => d switch
    {
        GestureDirection.Up => Loc.Current["Gesture_Up"],
        GestureDirection.Down => Loc.Current["Gesture_Down"],
        GestureDirection.Left => Loc.Current["Gesture_Left"],
        GestureDirection.Right => Loc.Current["Gesture_Right"],
        GestureDirection.Click => Loc.Current["Gesture_Click"],
        _ => d.ToString(),
    };

    public static string Glyph(this GestureDirection d) => d switch
    {
        GestureDirection.Up => "↑",
        GestureDirection.Down => "↓",
        GestureDirection.Left => "←",
        GestureDirection.Right => "→",
        GestureDirection.Click => "", // plain text, no glyph
        _ => "",
    };
}
