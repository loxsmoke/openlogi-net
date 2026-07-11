using OpenLogi.Agent;
using OpenLogi.App.ViewModels;
using OpenLogi.Core.Actions;
using OpenLogi.Core.Config;
using OpenLogi.Core.Gestures;

namespace OpenLogi.Tests.App;

/// <summary>
/// A gesture-owner button is diverted at the device, so its plain click is dispatched
/// from the gesture map's Click entry — not its single binding. These guard the diagram
/// picker routing its click edit into the gesture map (issue: picking an action on the
/// left-side button showed "assigned" but the click stayed dead / the panel showed
/// "Do nothing").
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
        Assert.True(MainWindowViewModel.ClickEditRoutesToGesture(cfg, "dev", ButtonId.Back, selectedGestureOwner: null));
    }

    [Fact]
    public void RoutesToGesture_WhenButtonIsSelectedOwnerEvenWithNoMapYet()
    {
        var cfg = new Config();
        Assert.True(MainWindowViewModel.ClickEditRoutesToGesture(cfg, "dev", ButtonId.Back, selectedGestureOwner: ButtonId.Back));
    }

    [Fact]
    public void DoesNotRouteToGesture_ForAPlainButton()
    {
        var cfg = WithBackSwipes();
        // Forward has no gesture map and is not the owner — a normal single binding.
        Assert.False(MainWindowViewModel.ClickEditRoutesToGesture(cfg, "dev", ButtonId.Forward, selectedGestureOwner: ButtonId.Back));
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
