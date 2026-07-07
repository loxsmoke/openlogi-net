using OpenLogi.Agent;
using OpenLogi.Core.Actions;
using OpenLogi.Core.Config;
using OpenLogi.Core.Gestures;
using OpenLogi.Input;

namespace OpenLogi.Tests.Agent;

/// <summary>Ported from the Rust <c>openlogi-agent-core::bindings</c> tests.</summary>
public class BindingMapsTests
{
    [Fact]
    public void ClickLessGestureKeepsDefaultClickInProjection()
    {
        var cfg = new Config();
        var map = new SortedDictionary<GestureDirection, MouseAction> { [GestureDirection.Up] = MouseAction.Copy };
        cfg.SetBinding("2b042", ButtonId.GestureButton, new Binding.Gesture(map));

        var projected = BindingMaps.BindingsFor(cfg, "2b042", null);
        Assert.Equal(Bindings.DefaultBinding(ButtonId.GestureButton), projected[ButtonId.GestureButton]);
    }

    [Fact]
    public void ExplicitGestureClickOverridesDefaultInProjection()
    {
        var cfg = new Config();
        var map = new SortedDictionary<GestureDirection, MouseAction> { [GestureDirection.Click] = MouseAction.Paste };
        cfg.SetBinding("2b042", ButtonId.GestureButton, new Binding.Gesture(map));

        Assert.Equal(MouseAction.Paste, BindingMaps.BindingsFor(cfg, "2b042", null)[ButtonId.GestureButton]);
    }

    [Fact]
    public void OsHookGesturesCollectsOnlyOsHookGestureButtons()
    {
        var cfg = new Config();
        cfg.SetGestureOwner("2b042", ButtonId.Back); // makes Back an OS-hook gesture owner
        cfg.SetBinding("2b042", ButtonId.MiddleClick, new Binding.Single(MouseAction.MiddleClick));

        var oshook = BindingMaps.OsHookGesturesFor(cfg, "2b042", null);
        Assert.Single(oshook);
        Assert.True(oshook.ContainsKey(ButtonId.Back));
        Assert.False(oshook.ContainsKey(ButtonId.MiddleClick));
        Assert.False(oshook.ContainsKey(ButtonId.GestureButton));
    }

    [Fact]
    public void PerAppOverrideDropsOwnerFromOsHookGestureSet()
    {
        var cfg = new Config();
        cfg.SetGestureOwner("2b042", ButtonId.Back);
        Assert.True(BindingMaps.OsHookGesturesFor(cfg, "2b042", null).ContainsKey(ButtonId.Back));

        cfg.SetPerAppBinding("2b042", "com.apple.Safari", ButtonId.Back, MouseAction.NextTab);
        Assert.Empty(BindingMaps.OsHookGesturesFor(cfg, "2b042", "com.apple.Safari"));
        Assert.True(BindingMaps.OsHookGesturesFor(cfg, "2b042", "com.other.App").ContainsKey(ButtonId.Back));
    }

    [Fact]
    public void GestureBindingsArePerButton()
    {
        var cfg = new Config();
        // The dedicated gesture button gestures by default; an unconfigured
        // physical button does not.
        Assert.Equal(
            Bindings.DefaultGestureBinding(GestureDirection.Up),
            BindingMaps.GestureBindingsFor(cfg, "2b042", ButtonId.GestureButton)[GestureDirection.Up]);
        Assert.Empty(BindingMaps.GestureBindingsFor(cfg, "2b042", ButtonId.Back));

        // Configuring Back gives it its own live map — without clearing the
        // dedicated button's (several buttons may gesture at once).
        cfg.SetGestureOwner("2b042", ButtonId.Back); // fills Back's five defaults
        cfg.SetGestureDirection("2b042", ButtonId.Back, GestureDirection.Up, MouseAction.Copy);
        var map = BindingMaps.GestureBindingsFor(cfg, "2b042", ButtonId.Back);
        Assert.Equal(MouseAction.Copy, map[GestureDirection.Up]);
        Assert.Equal(
            Bindings.DefaultGestureBinding(GestureDirection.Down),
            map[GestureDirection.Down]);
        Assert.NotEmpty(BindingMaps.GestureBindingsFor(cfg, "2b042", ButtonId.GestureButton));

        // Selecting another button in the editor must not clear Back's map.
        cfg.SetGestureSelection("2b042", ButtonId.MiddleClick);
        Assert.Equal(MouseAction.Copy,
            BindingMaps.GestureBindingsFor(cfg, "2b042", ButtonId.Back)[GestureDirection.Up]);

        // Only "All off" silences everything — and re-selecting restores it.
        cfg.DisableGestures("2b042");
        Assert.Empty(BindingMaps.GestureBindingsFor(cfg, "2b042", ButtonId.Back));
        Assert.Empty(BindingMaps.GestureBindingsFor(cfg, "2b042", ButtonId.GestureButton));
        cfg.SetGestureSelection("2b042", ButtonId.Back);
        Assert.Equal(MouseAction.Copy,
            BindingMaps.GestureBindingsFor(cfg, "2b042", ButtonId.Back)[GestureDirection.Up]);
    }
}

public class ButtonDispatchTests
{
    private static SortedDictionary<ButtonId, MouseAction> Bindings(params (ButtonId, MouseAction)[] entries)
    {
        var d = new SortedDictionary<ButtonId, MouseAction>();
        foreach (var (b, a) in entries) d[b] = a;
        return d;
    }

    [Fact]
    public void NonOsHookButtonsPassThrough()
    {
        var (disp, inject) = ButtonDispatch.Resolve(ButtonId.LeftClick, true, Bindings((ButtonId.LeftClick, MouseAction.Copy)));
        Assert.Equal(EventDisposition.PassThrough, disp);
        Assert.Null(inject);
    }

    [Fact]
    public void NativeIdentityPassesThrough()
    {
        var (disp, inject) = ButtonDispatch.Resolve(ButtonId.Back, true, Bindings((ButtonId.Back, MouseAction.MouseBack)));
        Assert.Equal(EventDisposition.PassThrough, disp);
        Assert.Null(inject);
    }

    [Fact]
    public void RemappedPressSuppressesAndInjects()
    {
        var (disp, inject) = ButtonDispatch.Resolve(ButtonId.Back, true, Bindings((ButtonId.Back, MouseAction.Copy)));
        Assert.Equal(EventDisposition.Suppress, disp);
        Assert.Equal(MouseAction.Copy, inject);
    }

    [Fact]
    public void RemappedReleaseSuppressesWithoutInjecting()
    {
        var (disp, inject) = ButtonDispatch.Resolve(ButtonId.Back, false, Bindings((ButtonId.Back, MouseAction.Copy)));
        Assert.Equal(EventDisposition.Suppress, disp);
        Assert.Null(inject);
    }
}
