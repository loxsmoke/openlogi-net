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

/// <summary>Ported from the Rust <c>inject</c> mac_virtual_key_to_windows tests.</summary>
public class ActionInjectorMappingTests
{
    [Fact]
    public void CustomShortcutKeycodesMapAcrossCategories()
    {
        Assert.Equal((ushort)0x41, ActionInjector.MacVkToWindows(0x00)); // A → VK_A
        Assert.Equal((ushort)0x31, ActionInjector.MacVkToWindows(0x12)); // 1 → VK_1
        Assert.Equal((ushort)0x70, ActionInjector.MacVkToWindows(0x7A)); // F1 → VK_F1
        Assert.Equal((ushort)0x25, ActionInjector.MacVkToWindows(0x7B)); // LeftArrow → VK_LEFT
        Assert.Equal((ushort)0x20, ActionInjector.MacVkToWindows(0x31)); // Space → VK_SPACE
        Assert.Equal((ushort)0xBA, ActionInjector.MacVkToWindows(0x29)); // ; → VK_OEM_1
        Assert.Null(ActionInjector.MacVkToWindows(0x37));                // Command is a modifier
    }
}
