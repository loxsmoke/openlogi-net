namespace OpenLogi.Core.Gestures;

public static class GestureDirectionExtensions
{
    public static readonly GestureDirection[] All =
        [GestureDirection.Up, GestureDirection.Down, GestureDirection.Left, GestureDirection.Right, GestureDirection.Click];

    public static string Label(this GestureDirection d) => d switch
    {
        GestureDirection.Up => "Up",
        GestureDirection.Down => "Down",
        GestureDirection.Left => "Left",
        GestureDirection.Right => "Right",
        GestureDirection.Click => "Click",
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
