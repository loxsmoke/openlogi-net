using OpenLogi.Core.Actions;
using MouseAction = OpenLogi.Core.Actions.MouseAction;

namespace OpenLogi.Input;

/// <summary>
/// Synthesises OS input for a bound <see cref="MouseAction"/> via <c>SendInput</c>.
/// Ported from Rust <c>openlogi-inject</c> (Windows path). Device actions
/// (DPI/SmartShift) are no-ops here — they're handled by the HID layer.
///
/// HARDWARE-UNVERIFIED: actual injection needs an interactive desktop to observe.
/// <see cref="ModifierKeys"/> and <see cref="IsExtendedKey"/> are pure and unit-tested.
/// </summary>
public static class ActionInjector
{
    private const int WheelDelta = 120;

    // Windows virtual-key codes.
    private const ushort VK_A = 0x41, VK_C = 0x43, VK_D = 0x44, VK_F = 0x46,
        VK_R = 0x52, VK_S = 0x53, VK_T = 0x54, VK_V = 0x56, VK_W = 0x57, VK_X = 0x58, VK_Y = 0x59, VK_Z = 0x5A,
        VK_TAB = 0x09, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28,
        VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12,
        VK_LWIN = 0x5B, VK_BROWSER_BACK = 0xA6, VK_BROWSER_FORWARD = 0xA7, VK_VOLUME_MUTE = 0xAD,
        VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF, VK_MEDIA_NEXT_TRACK = 0xB0, VK_MEDIA_PREV_TRACK = 0xB1,
        VK_MEDIA_PLAY_PAUSE = 0xB3;

    private enum MouseButton { Left, Right, Middle, Back, Forward }

    /// <summary>Execute the OS effect of <paramref name="action"/>.</summary>
    public static void Execute(MouseAction action)
    {
        switch (action.Kind)
        {
            case ActionKind.LeftClick: PostClick(MouseButton.Left); break;
            case ActionKind.RightClick: PostClick(MouseButton.Right); break;
            case ActionKind.MiddleClick: PostClick(MouseButton.Middle); break;
            case ActionKind.MouseBack: PostClick(MouseButton.Back); break;
            case ActionKind.MouseForward: PostClick(MouseButton.Forward); break;
            case ActionKind.Copy: PostKey(VK_C, VK_CONTROL); break;
            case ActionKind.Paste: PostKey(VK_V, VK_CONTROL); break;
            case ActionKind.Cut: PostKey(VK_X, VK_CONTROL); break;
            case ActionKind.Undo: PostKey(VK_Z, VK_CONTROL); break;
            case ActionKind.Redo: PostKey(VK_Y, VK_CONTROL); break;
            case ActionKind.SelectAll: PostKey(VK_A, VK_CONTROL); break;
            case ActionKind.Find: PostKey(VK_F, VK_CONTROL); break;
            case ActionKind.Save: PostKey(VK_S, VK_CONTROL); break;
            case ActionKind.BrowserBack: PostKey(VK_BROWSER_BACK); break;
            case ActionKind.BrowserForward: PostKey(VK_BROWSER_FORWARD); break;
            case ActionKind.NewTab: PostKey(VK_T, VK_CONTROL); break;
            case ActionKind.CloseTab: PostKey(VK_W, VK_CONTROL); break;
            case ActionKind.ReopenTab: PostKey(VK_T, VK_CONTROL, VK_SHIFT); break;
            case ActionKind.NextTab: PostKey(VK_TAB, VK_CONTROL); break;
            case ActionKind.PrevTab: PostKey(VK_TAB, VK_CONTROL, VK_SHIFT); break;
            case ActionKind.ReloadPage: PostKey(VK_R, VK_CONTROL); break;
            case ActionKind.TaskView: PostKey(VK_TAB, VK_LWIN); break;
            case ActionKind.PreviousDesktop: PostKey(VK_LEFT, VK_LWIN, VK_CONTROL); break;
            case ActionKind.NextDesktop: PostKey(VK_RIGHT, VK_LWIN, VK_CONTROL); break;
            case ActionKind.ShowDesktop: PostKey(VK_D, VK_LWIN); break;
            case ActionKind.StartMenu: PostKey(VK_LWIN); break;
            case ActionKind.MaximizeWindow: PostKey(VK_UP, VK_LWIN); break;
            case ActionKind.MinimizeWindow: PostKey(VK_DOWN, VK_LWIN); break;
            case ActionKind.SnapWindowLeft: PostKey(VK_LEFT, VK_LWIN); break;
            case ActionKind.SnapWindowRight: PostKey(VK_RIGHT, VK_LWIN); break;
            case ActionKind.LockScreen: Native.LockWorkStation(); break;
            case ActionKind.Screenshot:
            case ActionKind.CaptureRegion: PostKey(VK_S, VK_LWIN, VK_SHIFT); break;
            case ActionKind.PlayPause: PostKey(VK_MEDIA_PLAY_PAUSE); break;
            case ActionKind.NextTrack: PostKey(VK_MEDIA_NEXT_TRACK); break;
            case ActionKind.PrevTrack: PostKey(VK_MEDIA_PREV_TRACK); break;
            case ActionKind.VolumeUp: PostKey(VK_VOLUME_UP); break;
            case ActionKind.VolumeDown: PostKey(VK_VOLUME_DOWN); break;
            case ActionKind.MuteVolume: PostKey(VK_VOLUME_MUTE); break;
            case ActionKind.ScrollUp: PostScroll(Native.MOUSEEVENTF_WHEEL, WheelDelta); break;
            case ActionKind.ScrollDown: PostScroll(Native.MOUSEEVENTF_WHEEL, -WheelDelta); break;
            case ActionKind.HorizontalScrollLeft: PostScroll(Native.MOUSEEVENTF_HWHEEL, -WheelDelta); break;
            case ActionKind.HorizontalScrollRight: PostScroll(Native.MOUSEEVENTF_HWHEEL, WheelDelta); break;
            case ActionKind.CustomShortcut: PostCustomShortcut(action.Combo!); break;
            // None + device actions (CycleDpiPresets/SetDpiPreset/ToggleSmartShift) are no-ops here.
            default: break;
        }
    }

    /// <summary>Synthesise a vertical scroll of raw wheel data (hi-res wheel re-injection; ±120 = one notch).</summary>
    public static void PostVerticalScroll(int wheelData)
    {
        if (wheelData == 0) return;
        PostScroll(Native.MOUSEEVENTF_WHEEL, wheelData);
    }

    /// <summary>Synthesise a horizontal scroll of <paramref name="delta"/> wheel lines (thumbwheel re-injection).</summary>
    public static void PostHorizontalScroll(int delta)
    {
        if (delta == 0) return;
        PostScroll(Native.MOUSEEVENTF_HWHEEL, SaturatingMul(delta, WheelDelta));
    }

    private static void PostClick(MouseButton button)
    {
        var (down, up, data) = button switch
        {
            MouseButton.Left => (Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP, 0),
            MouseButton.Right => (Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP, 0),
            MouseButton.Middle => (Native.MOUSEEVENTF_MIDDLEDOWN, Native.MOUSEEVENTF_MIDDLEUP, 0),
            MouseButton.Back => (Native.MOUSEEVENTF_XDOWN, Native.MOUSEEVENTF_XUP, Native.XBUTTON1),
            MouseButton.Forward => (Native.MOUSEEVENTF_XDOWN, Native.MOUSEEVENTF_XUP, Native.XBUTTON2),
            _ => (0u, 0u, 0),
        };
        SendInputs([MouseInput(down, data), MouseInput(up, data)]);
    }

    private static void PostKey(ushort vk, params ushort[] modifiers)
    {
        var inputs = new List<Native.INPUT>(modifiers.Length * 2 + 2);
        foreach (var m in modifiers) inputs.Add(KeyInput(m, keyUp: false));
        inputs.Add(KeyInput(vk, keyUp: false));
        inputs.Add(KeyInput(vk, keyUp: true));
        for (var i = modifiers.Length - 1; i >= 0; i--) inputs.Add(KeyInput(modifiers[i], keyUp: true));
        SendInputs([.. inputs]);
    }

    private static void PostScroll(uint flags, int data) => SendInputs([MouseInput(flags, data)]);

    /// <summary>A recorded chord: its modifiers held, the key tapped. A cleared (empty) chord injects nothing.</summary>
    private static void PostCustomShortcut(KeyCombo combo)
    {
        if (combo.IsEmpty) return;
        PostKey(combo.KeyCode, ModifierKeys(combo.Modifiers));
    }

    /// <summary>The virtual keys to hold for <paramref name="modifiers"/>, in press order (released in reverse).</summary>
    public static ushort[] ModifierKeys(ShortcutModifiers modifiers)
    {
        var keys = new List<ushort>(4);
        if (modifiers.HasFlag(ShortcutModifiers.Ctrl)) keys.Add(VK_CONTROL);
        if (modifiers.HasFlag(ShortcutModifiers.Shift)) keys.Add(VK_SHIFT);
        if (modifiers.HasFlag(ShortcutModifiers.Alt)) keys.Add(VK_MENU);
        if (modifiers.HasFlag(ShortcutModifiers.Win)) keys.Add(VK_LWIN);
        return [.. keys];
    }

    private static void SendInputs(Native.INPUT[] inputs) =>
        Native.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());

    private static Native.INPUT KeyInput(ushort vk, bool keyUp) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.INPUTUNION
        {
            ki = new Native.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = (keyUp ? Native.KEYEVENTF_KEYUP : 0) | (IsExtendedKey(vk) ? Native.KEYEVENTF_EXTENDEDKEY : 0),
            },
        },
    };

    /// <summary>
    /// Whether <paramref name="vk"/> is an "extended" key (navigation cluster, arrows,
    /// right-hand modifiers, Win/Apps, numpad Enter/Divide). Without the extended flag
    /// Windows derives the numpad scan code for these, so an injected ← reads as numpad 4
    /// to anything that inspects the scan code.
    /// </summary>
    public static bool IsExtendedKey(ushort vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 => true,        // PgUp, PgDn, End, Home
        0x25 or 0x26 or 0x27 or 0x28 => true,        // arrows
        0x2C or 0x2D or 0x2E => true,                // PrintScreen, Insert, Delete
        0x5B or 0x5C or 0x5D => true,                // LWin, RWin, Apps
        0x6F or 0x90 => true,                        // numpad Divide, NumLock
        0xA3 or 0xA5 => true,                        // RControl, RMenu
        >= 0xA6 and <= 0xB7 => true,                 // browser / volume / media / launch keys
        _ => false,
    };

    private static Native.INPUT MouseInput(uint flags, int data) => new()
    {
        type = Native.INPUT_MOUSE,
        u = new Native.INPUTUNION { mi = new Native.MOUSEINPUT { mouseData = unchecked((uint)data), dwFlags = flags } },
    };

    private static int SaturatingMul(int a, int b)
    {
        long p = (long)a * b;
        return p > int.MaxValue ? int.MaxValue : p < int.MinValue ? int.MinValue : (int)p;
    }
}
