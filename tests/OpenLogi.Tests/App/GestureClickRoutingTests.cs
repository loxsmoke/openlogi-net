using OpenLogi.Agent;
using OpenLogi.App.ViewModels;
using OpenLogi.Core.Actions;
using OpenLogi.Core.Config;
using OpenLogi.Core.Gestures;

namespace OpenLogi.Tests.App;

/// <summary>
/// A button with a gesture map is diverted at the device, so its plain click is dispatched
/// from the map's Click entry — not its single binding. These guard the click editors (the
/// diagram picker and the panel's Click row share one path) routing their edit into the
/// gesture map (issue: picking an action on the left-side button showed "assigned" but the
/// click stayed dead / the panel showed "Do nothing"), while a button without a map keeps
/// getting an ordinary single binding — being selected in the panel doesn't make it gesture.
/// </summary>
public class GestureClickRoutingTests
{
    private static Config WithBackSwipes()
    {
        var cfg = new Config();
        cfg.SetGestureDirection("dev", ButtonId.Back, GestureDirection.Left, MouseAction.BrowserBack);
        cfg.SetGestureDirection("dev", ButtonId.Back, GestureDirection.Right, MouseAction.BrowserForward);
        return cfg;
    }

    [Fact]
    public void RoutesToGesture_WhenButtonAlreadyDrivesGestures()
    {
        var cfg = WithBackSwipes();
        Assert.True(MainWindowViewModel.ClickEditRoutesToGesture(cfg, "dev", ButtonId.Back));
    }

    [Fact]
    public void RoutesToGesture_WhileGesturesAreGloballyOff_SoTheKeptMapIsNotDropped()
    {
        var cfg = WithBackSwipes();
        cfg.DisableGestures("dev");
        // Off keeps every map for when gestures come back; a click edit must not clobber it.
        Assert.True(MainWindowViewModel.ClickEditRoutesToGesture(cfg, "dev", ButtonId.Back));

        cfg.SetGestureDirection("dev", ButtonId.Back, GestureDirection.Click, MouseAction.Copy);
        cfg.EnableGestures("dev");
        var stored = cfg.GestureBindingsFor("dev", ButtonId.Back);
        Assert.Equal(MouseAction.BrowserBack, stored[GestureDirection.Left]);
        Assert.Equal(MouseAction.Copy, stored[GestureDirection.Click]);
    }

    [Fact]
    public void DoesNotRouteToGesture_ForAButtonWithNoMapYet()
    {
        // Selecting a button in the panel alone never makes it a gesture button: its
        // click stays an ordinary single binding until its first swipe is configured.
        var cfg = new Config();
        Assert.False(MainWindowViewModel.ClickEditRoutesToGesture(cfg, "dev", ButtonId.Back));
    }

    [Fact]
    public void DoesNotRouteToGesture_ForAPlainButton()
    {
        var cfg = WithBackSwipes();
        // Forward has no gesture map — a normal single binding.
        Assert.False(MainWindowViewModel.ClickEditRoutesToGesture(cfg, "dev", ButtonId.Forward));
    }

    [Fact]
    public void RoutingClickEdit_PreservesSwipesAndDispatchesTheClick()
    {
        var cfg = WithBackSwipes();

        // What Persist does for a gesture-owner button instead of writing a Single.
        cfg.SetGestureDirection("dev", ButtonId.Back, GestureDirection.Click, MouseAction.Copy);

        // Still a gesture button (map not clobbered), and the swipes survive the click edit.
        Assert.Contains(ButtonId.Back, cfg.GestureButtons("dev"));
        var stored = cfg.GestureBindingsFor("dev", ButtonId.Back);
        Assert.Equal(MouseAction.BrowserBack, stored[GestureDirection.Left]);
        Assert.Equal(MouseAction.BrowserForward, stored[GestureDirection.Right]);

        // The click the device actually dispatches is the one just assigned.
        var dispatched = BindingMaps.GestureBindingsFor(cfg, "dev", ButtonId.Back);
        Assert.Equal(MouseAction.Copy, dispatched[GestureDirection.Click]);
    }

    [Fact]
    public void WritingASingleInstead_WouldDropTheGesturesAndKillTheClick()
    {
        // Regression guard: the pre-fix diagram edit wrote a Single, which is exactly why
        // the click broke — it replaces the gesture map, so the button is no longer
        // diverted-dispatchable and its swipes are gone.
        var cfg = WithBackSwipes();
        cfg.SetBinding("dev", ButtonId.Back, new Binding.Single(MouseAction.Copy));

        Assert.DoesNotContain(ButtonId.Back, cfg.GestureButtons("dev"));
        Assert.Empty(BindingMaps.GestureBindingsFor(cfg, "dev", ButtonId.Back));
    }
}
