using OpenLogi.Core.Actions;
using OpenLogi.Core.Config;
using OpenLogi.Core.Cursor;
using OpenLogi.Input;

namespace OpenLogi.Tests.Input;

/// <summary>Ported from the Rust <c>openlogi-hook::windows</c> translate_event tests.</summary>
public class MouseHookTranslateTests
{
    [Fact]
    public void IgnoresInjectedMouseInput()
    {
        var data = new Native.MSLLHOOKSTRUCT { flags = Native.LLMHF_INJECTED };
        Assert.Null(MouseHook.TranslateEvent(Native.WM_LBUTTONDOWN, data));
    }

    [Fact]
    public void WheelForwardScrollsUpLikeOtherPlatforms()
    {
        var forward = new Native.MSLLHOOKSTRUCT { mouseData = 120u << 16 };
        var ev = Assert.IsType<MouseEvent.Scroll>(MouseHook.TranslateEvent(Native.WM_MOUSEWHEEL, forward));
        Assert.True(Math.Abs(ev.DeltaX) < float.Epsilon);
        Assert.True(ev.DeltaY > 0.0f, $"wheel-forward should scroll up, got {ev.DeltaY}");
    }

    [Fact]
    public void WheelBackwardScrollsDown()
    {
        var backward = new Native.MSLLHOOKSTRUCT { mouseData = unchecked((uint)(-120 << 16)) };
        var ev = Assert.IsType<MouseEvent.Scroll>(MouseHook.TranslateEvent(Native.WM_MOUSEWHEEL, backward));
        Assert.True(ev.DeltaY < 0.0f);
    }

    [Theory]
    [InlineData(Native.WM_LBUTTONDOWN, ButtonId.LeftClick, true)]
    [InlineData(Native.WM_RBUTTONUP, ButtonId.RightClick, false)]
    [InlineData(Native.WM_MBUTTONDOWN, ButtonId.MiddleClick, true)]
    public void TranslatesButtonEvents(uint wParam, ButtonId expectedId, bool expectedPressed)
    {
        var ev = Assert.IsType<MouseEvent.Button>(MouseHook.TranslateEvent(wParam, new Native.MSLLHOOKSTRUCT()));
        Assert.Equal(expectedId, ev.Id);
        Assert.Equal(expectedPressed, ev.Pressed);
    }

    [Fact]
    public void TranslatesMovesToScreenPositions()
    {
        var moved = new Native.MSLLHOOKSTRUCT { pt = new Native.POINT { X = 1280, Y = 720 } };
        var ev = Assert.IsType<MouseEvent.Move>(MouseHook.TranslateEvent(Native.WM_MOUSEMOVE, moved));
        Assert.Equal(1280, ev.X);
        Assert.Equal(720, ev.Y);
    }

    [Fact]
    public void TranslatesXButtonsToBackForward()
    {
        var back = new Native.MSLLHOOKSTRUCT { mouseData = (uint)Native.XBUTTON1 << 16 };
        var forward = new Native.MSLLHOOKSTRUCT { mouseData = (uint)Native.XBUTTON2 << 16 };
        Assert.Equal(ButtonId.Back, Assert.IsType<MouseEvent.Button>(MouseHook.TranslateEvent(Native.WM_XBUTTONDOWN, back)).Id);
        Assert.Equal(ButtonId.Forward, Assert.IsType<MouseEvent.Button>(MouseHook.TranslateEvent(Native.WM_XBUTTONDOWN, forward)).Id);
    }
}

/// <summary>The pure part of shake-to-locate's pointer resize (the rest needs a desktop).</summary>
public class CursorSizeTests
{
    [Fact]
    public void ScalesTheUsersOwnPointerSize()
    {
        Assert.Equal(96, CursorSize.TargetSize(CursorSize.DefaultBaseSize, 3));
        Assert.Equal(192, CursorSize.TargetSize(64, 3)); // a user who already runs a big pointer
    }

    [Fact]
    public void FractionalFramesLandOnTheSizeStep()
    {
        // Mid-animation scales resolve to a step of the ladder, so consecutive frames
        // often round to the size already on screen and cost nothing to "apply".
        Assert.Equal(48, CursorSize.TargetSize(CursorSize.DefaultBaseSize, 1.5));
        Assert.Equal(68, CursorSize.TargetSize(CursorSize.DefaultBaseSize, 2.18)); // 69.8 → nearest step
        Assert.Equal(0, CursorSize.TargetSize(CursorSize.DefaultBaseSize, 2.6) % CursorSize.SizeStep);
        Assert.Equal(CursorSize.DefaultBaseSize, CursorSize.TargetSize(CursorSize.DefaultBaseSize, 0.5)); // never below 1×
    }

    [Fact]
    public void FullScaleStaysOnANativeCursorSize()
    {
        // The stock cursor files carry native 32/48/64/96/128 images; 3× of the default
        // is 96, so the fully grown pointer is drawn from one of them rather than scaled.
        Assert.Equal(96, CursorSize.TargetSize(CursorSize.DefaultBaseSize, ShakeZoom.MaxScale));
    }

    [Fact]
    public void NeverExceedsWhatWindowsAccepts()
    {
        Assert.Equal(CursorSize.MaxBaseSize, CursorSize.TargetSize(64, 8));
        Assert.Equal(CursorSize.MaxBaseSize, CursorSize.TargetSize(CursorSize.MaxBaseSize, 2));
    }
}

/// <summary>The pure translation step of the low-level keyboard hook.</summary>
public class KeyboardHookTranslateTests
{
    [Fact]
    public void KeyDownAndUpTranslate_SysKeysIncluded()
    {
        var data = new Native.KBDLLHOOKSTRUCT { vkCode = 0x09 };
        Assert.Equal(new KeyboardHookEvent(0x09, Pressed: true), KeyboardHook.TranslateEvent(Native.WM_KEYDOWN, data));
        Assert.Equal(new KeyboardHookEvent(0x09, Pressed: true), KeyboardHook.TranslateEvent(Native.WM_SYSKEYDOWN, data)); // Alt+Tab
        Assert.Equal(new KeyboardHookEvent(0x09, Pressed: false), KeyboardHook.TranslateEvent(Native.WM_KEYUP, data));
        Assert.Equal(new KeyboardHookEvent(0x09, Pressed: false), KeyboardHook.TranslateEvent(Native.WM_SYSKEYUP, data));
    }

    [Fact]
    public void InjectedKeysAreDropped()
    {
        var injected = new Native.KBDLLHOOKSTRUCT { vkCode = 0x50, flags = Native.LLKHF_INJECTED };
        Assert.Null(KeyboardHook.TranslateEvent(Native.WM_KEYDOWN, injected)); // our own SendInput
        Assert.Null(KeyboardHook.TranslateEvent(0x0200, new Native.KBDLLHOOKSTRUCT { vkCode = 0x50 })); // not a key message
    }
}

/// <summary>The pure parts of shortcut injection: which keys are held, and which are "extended".</summary>
public class ActionInjectorKeyTests
{
    [Fact]
    public void ModifierKeysAreHeldInCtrlShiftAltWinOrder()
    {
        var all = ShortcutModifiers.Ctrl | ShortcutModifiers.Alt | ShortcutModifiers.Shift | ShortcutModifiers.Win;
        Assert.Equal(new ushort[] { 0x11, 0x10, 0x12, 0x5B }, ActionInjector.ModifierKeys(all));
        Assert.Equal(new ushort[] { 0x12 }, ActionInjector.ModifierKeys(ShortcutModifiers.Alt));
        Assert.Empty(ActionInjector.ModifierKeys(ShortcutModifiers.None));
    }

    [Theory]
    [InlineData(0x25, true)]  // VK_LEFT
    [InlineData(0x2E, true)]  // VK_DELETE
    [InlineData(0xA5, true)]  // VK_RMENU
    [InlineData(0x5B, true)]  // VK_LWIN
    [InlineData(0x41, false)] // VK_A
    [InlineData(0x10, false)] // VK_SHIFT
    [InlineData(0x64, false)] // VK_NUMPAD4
    public void ExtendedKeysAreTheNavigationClusterAndRightHandModifiers(int vk, bool extended)
    {
        Assert.Equal(extended, ActionInjector.IsExtendedKey((ushort)vk));
    }
}
