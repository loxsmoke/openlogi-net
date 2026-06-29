using OpenLogi.Agent;
using OpenLogi.Core;
using OpenLogi.Input;

namespace OpenLogi.Tests.Agent;

/// <summary>Ported from the Rust <c>openlogi-agent-core::bindings</c> tests.</summary>
public class BindingMapsTests
{
    [Fact]
    public void ClickLessGestureKeepsDefaultClickInProjection()
    {
        var cfg = new Config();
        var map = new SortedDictionary<GestureDirection, Action> { [GestureDirection.Up] = Action.Copy };
        cfg.SetBinding("2b042", ButtonId.GestureButton, new Binding.Gesture(map));

        var projected = BindingMaps.BindingsFor(cfg, "2b042", null);
        Assert.Equal(Core.Bindings.DefaultBinding(ButtonId.GestureButton), projected[ButtonId.GestureButton]);
    }

    [Fact]
    public void ExplicitGestureClickOverridesDefaultInProjection()
    {
        var cfg = new Config();
        var map = new SortedDictionary<GestureDirection, Action> { [GestureDirection.Click] = Action.Paste };
        cfg.SetBinding("2b042", ButtonId.GestureButton, new Binding.Gesture(map));

        Assert.Equal(Action.Paste, BindingMaps.BindingsFor(cfg, "2b042", null)[ButtonId.GestureButton]);
    }

    [Fact]
    public void OsHookGesturesCollectsOnlyOsHookGestureButtons()
    {
        var cfg = new Config();
        cfg.SetGestureOwner("2b042", ButtonId.Back); // makes Back an OS-hook gesture owner
        cfg.SetBinding("2b042", ButtonId.MiddleClick, new Binding.Single(Action.MiddleClick));

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

        cfg.SetPerAppBinding("2b042", "com.apple.Safari", ButtonId.Back, Action.NextTab);
        Assert.Empty(BindingMaps.OsHookGesturesFor(cfg, "2b042", "com.apple.Safari"));
        Assert.True(BindingMaps.OsHookGesturesFor(cfg, "2b042", "com.other.App").ContainsKey(ButtonId.Back));
    }

    [Fact]
    public void GestureBindingsSilentWhenHidppButtonIsNotOwner()
    {
        var cfg = new Config();
        Assert.Equal(
            Core.Bindings.DefaultGestureBinding(GestureDirection.Up),
            BindingMaps.GestureBindingsFor(cfg, "2b042")[GestureDirection.Up]);

        cfg.SetGestureOwner("2b042", ButtonId.Back);
        Assert.Empty(BindingMaps.GestureBindingsFor(cfg, "2b042"));
    }
}

public class ButtonDispatchTests
{
    private static SortedDictionary<ButtonId, Action> Bindings(params (ButtonId, Action)[] entries)
    {
        var d = new SortedDictionary<ButtonId, Action>();
        foreach (var (b, a) in entries) d[b] = a;
        return d;
    }

    [Fact]
    public void NonOsHookButtonsPassThrough()
    {
        var (disp, inject) = ButtonDispatch.Resolve(ButtonId.LeftClick, true, Bindings((ButtonId.LeftClick, Action.Copy)));
        Assert.Equal(EventDisposition.PassThrough, disp);
        Assert.Null(inject);
    }

    [Fact]
    public void NativeIdentityPassesThrough()
    {
        var (disp, inject) = ButtonDispatch.Resolve(ButtonId.Back, true, Bindings((ButtonId.Back, Action.MouseBack)));
        Assert.Equal(EventDisposition.PassThrough, disp);
        Assert.Null(inject);
    }

    [Fact]
    public void RemappedPressSuppressesAndInjects()
    {
        var (disp, inject) = ButtonDispatch.Resolve(ButtonId.Back, true, Bindings((ButtonId.Back, Action.Copy)));
        Assert.Equal(EventDisposition.Suppress, disp);
        Assert.Equal(Action.Copy, inject);
    }

    [Fact]
    public void RemappedReleaseSuppressesWithoutInjecting()
    {
        var (disp, inject) = ButtonDispatch.Resolve(ButtonId.Back, false, Bindings((ButtonId.Back, Action.Copy)));
        Assert.Equal(EventDisposition.Suppress, disp);
        Assert.Null(inject);
    }
}
