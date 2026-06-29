using OpenLogi.Core;
using Action = OpenLogi.Core.Action;

namespace OpenLogi.Input;

/// <summary>
/// Synthesises OS input for a bound <see cref="Action"/> via <c>SendInput</c>.
/// Ported from Rust <c>openlogi-inject</c> (Windows path). Device actions
/// (DPI/SmartShift) are no-ops here — they're handled by the HID layer.
///
/// HARDWARE-UNVERIFIED: actual injection needs an interactive desktop to observe.
/// <see cref="MacVkToWindows"/> is pure and unit-tested.
/// </summary>
public static class ActionInjector
{
    private const int WheelDelta = 120;

    // Windows virtual-key codes.
    private const ushort VK_A = 0x41, VK_C = 0x43, VK_D = 0x44, VK_F = 0x46, VK_L = 0x4C,
        VK_R = 0x52, VK_S = 0x53, VK_T = 0x54, VK_V = 0x56, VK_W = 0x57, VK_X = 0x58, VK_Y = 0x59, VK_Z = 0x5A,
        VK_TAB = 0x09, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12,
        VK_LWIN = 0x5B, VK_BROWSER_BACK = 0xA6, VK_BROWSER_FORWARD = 0xA7, VK_VOLUME_MUTE = 0xAD,
        VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF, VK_MEDIA_NEXT_TRACK = 0xB0, VK_MEDIA_PREV_TRACK = 0xB1,
        VK_MEDIA_PLAY_PAUSE = 0xB3;

    private enum MouseButton { Left, Right, Middle, Back, Forward }

    /// <summary>Execute the OS effect of <paramref name="action"/>.</summary>
    public static void Execute(Action action)
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
            case ActionKind.MissionControl:
            case ActionKind.AppExpose: PostKey(VK_TAB, VK_LWIN); break;
            case ActionKind.PreviousDesktop: PostKey(VK_LEFT, VK_LWIN, VK_CONTROL); break;
            case ActionKind.NextDesktop: PostKey(VK_RIGHT, VK_LWIN, VK_CONTROL); break;
            case ActionKind.ShowDesktop: PostKey(VK_D, VK_LWIN); break;
            case ActionKind.LaunchpadShow: PostKey(VK_LWIN); break;
            case ActionKind.LockScreen: PostKey(VK_L, VK_LWIN); break;
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

    private static void PostCustomShortcut(KeyCombo combo)
    {
        if (combo.KeyCode == 0) return;
        if (MacVkToWindows(combo.KeyCode) is not { } vk) return;

        var modifiers = new List<ushort>();
        if ((combo.Modifiers & KeyCombo.ModCmd) != 0) modifiers.Add(VK_CONTROL);
        if ((combo.Modifiers & KeyCombo.ModShift) != 0) modifiers.Add(VK_SHIFT);
        if ((combo.Modifiers & KeyCombo.ModCtrl) != 0 && !modifiers.Contains(VK_CONTROL)) modifiers.Add(VK_CONTROL);
        if ((combo.Modifiers & KeyCombo.ModOption) != 0) modifiers.Add(VK_MENU);
        PostKey(vk, [.. modifiers]);
    }

    private static void SendInputs(Native.INPUT[] inputs) =>
        Native.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());

    private static Native.INPUT KeyInput(ushort vk, bool keyUp) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = keyUp ? Native.KEYEVENTF_KEYUP : 0 } },
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

    /// <summary>
    /// Map a macOS virtual key code (the form stored in <see cref="KeyCombo.KeyCode"/>)
    /// to a Windows virtual-key code, or <c>null</c> if unmapped. Ported from Rust
    /// <c>mac_virtual_key_to_windows</c>.
    /// </summary>
    public static ushort? MacVkToWindows(ushort keyCode) => keyCode switch
    {
        0x00 => 0x41, 0x0B => 0x42, 0x08 => 0x43, 0x02 => 0x44, 0x0E => 0x45, 0x03 => 0x46,
        0x05 => 0x47, 0x04 => 0x48, 0x22 => 0x49, 0x26 => 0x4A, 0x28 => 0x4B, 0x25 => 0x4C,
        0x2E => 0x4D, 0x2D => 0x4E, 0x1F => 0x4F, 0x23 => 0x50, 0x0C => 0x51, 0x0F => 0x52,
        0x01 => 0x53, 0x11 => 0x54, 0x20 => 0x55, 0x09 => 0x56, 0x0D => 0x57, 0x07 => 0x58,
        0x10 => 0x59, 0x06 => 0x5A,
        0x1D => 0x30, 0x12 => 0x31, 0x13 => 0x32, 0x14 => 0x33, 0x15 => 0x34, 0x17 => 0x35,
        0x16 => 0x36, 0x1A => 0x37, 0x1C => 0x38, 0x19 => 0x39,
        0x1B => 0xBD, 0x18 => 0xBB, 0x21 => 0xDB, 0x1E => 0xDD, 0x2A => 0xDC, 0x29 => 0xBA,
        0x27 => 0xDE, 0x2B => 0xBC, 0x2F => 0xBE, 0x2C => 0xBF, 0x32 => 0xC0,
        0x24 => 0x0D, 0x30 => 0x09, 0x31 => 0x20, 0x33 => 0x08, 0x35 => 0x1B,
        0x73 => 0x24, 0x77 => 0x23, 0x74 => 0x21, 0x79 => 0x22, 0x75 => 0x2E,
        0x7B => 0x25, 0x7C => 0x27, 0x7D => 0x28, 0x7E => 0x26,
        0x7A => 0x70, 0x78 => 0x71, 0x63 => 0x72, 0x76 => 0x73, 0x60 => 0x74, 0x61 => 0x75,
        0x62 => 0x76, 0x64 => 0x77, 0x65 => 0x78, 0x6D => 0x79, 0x67 => 0x7A, 0x6F => 0x7B,
        0x69 => 0x7C, 0x6B => 0x7D, 0x71 => 0x7E, 0x6A => 0x7F, 0x40 => 0x80, 0x4F => 0x81,
        0x50 => 0x82, 0x5A => 0x83,
        _ => null,
    };
}
