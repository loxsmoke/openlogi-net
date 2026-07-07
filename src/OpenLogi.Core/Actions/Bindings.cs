using OpenLogi.Core.Config;
using OpenLogi.Core.Gestures;

namespace OpenLogi.Core.Actions;

/// <summary>Canonical default bindings. Ported from the free functions in Rust <c>binding</c>.</summary>
public static class Bindings
{
    /// <summary>Sensible default action for a fresh button so the panel isn't empty.</summary>
    public static MouseAction DefaultBinding(ButtonId button) => button switch
    {
        ButtonId.LeftClick => MouseAction.LeftClick,
        ButtonId.RightClick => MouseAction.RightClick,
        ButtonId.MiddleClick => MouseAction.MiddleClick,
        ButtonId.Back => MouseAction.BrowserBack,
        ButtonId.Forward => MouseAction.BrowserForward,
        ButtonId.DpiToggle => MouseAction.CycleDpiPresets,
        ButtonId.Thumbwheel => MouseAction.TaskView,
        ButtonId.ThumbwheelScrollUp => MouseAction.HorizontalScrollRight,
        ButtonId.ThumbwheelScrollDown => MouseAction.HorizontalScrollLeft,
        ButtonId.GestureButton => MouseAction.TaskView,
        _ => MouseAction.None,
    };

    /// <summary>Per-direction defaults for the gesture button.</summary>
    public static MouseAction DefaultGestureBinding(GestureDirection direction) => direction switch
    {
        GestureDirection.Up => MouseAction.TaskView,
        GestureDirection.Down => MouseAction.ShowDesktop,
        GestureDirection.Left => MouseAction.PrevTab,
        GestureDirection.Right => MouseAction.NextTab,
        GestureDirection.Click => MouseAction.TaskView,
        _ => MouseAction.None,
    };

    /// <summary>
    /// The canonical default <see cref="Binding"/> for a fresh button:
    /// <see cref="ButtonId.GestureButton"/> defaults to a full gesture map; every
    /// other button to a <see cref="Binding.Single"/> of its <see cref="DefaultBinding"/>.
    /// </summary>
    public static Binding DefaultBindingFor(ButtonId button) => button switch
    {
        ButtonId.GestureButton => new Binding.Gesture(
            GestureDirectionExtensions.All.Select(d =>
                new KeyValuePair<GestureDirection, MouseAction>(d, DefaultGestureBinding(d)))),
        _ => new Binding.Single(DefaultBinding(button)),
    };
}
