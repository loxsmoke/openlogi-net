using OpenLogi.Core.Config;

namespace OpenLogi.Core.Actions;

/// <summary>
/// What pressing a <see cref="ButtonId"/> should do. A tagged union: most
/// variants are unit (a bare <see cref="ActionKind"/>), <see cref="ActionKind.SetDpiPreset"/>
/// carries a preset index, and <see cref="ActionKind.CustomShortcut"/> carries a
/// <see cref="KeyCombo"/>. Pure config data — OS event synthesis lives in
/// OpenLogi.Input. Ported from Rust <c>binding::Action</c>.
/// </summary>
public sealed record MouseAction
{
    public ActionKind Kind { get; }
    /// <summary>Preset index for <see cref="ActionKind.SetDpiPreset"/>; otherwise 0.</summary>
    public byte DpiPreset { get; }
    /// <summary>Chord for <see cref="ActionKind.CustomShortcut"/>; otherwise <c>null</c>.</summary>
    public KeyCombo? Combo { get; }

    private MouseAction(ActionKind kind, byte dpiPreset = 0, KeyCombo? combo = null)
    {
        Kind = kind;
        DpiPreset = dpiPreset;
        Combo = combo;
    }

    /// <summary>A unit (payload-free) action. Throws for the payload-carrying kinds.</summary>
    public static MouseAction Unit(ActionKind kind) => kind switch
    {
        ActionKind.SetDpiPreset or ActionKind.CustomShortcut =>
            throw new ArgumentException($"{kind} carries a payload; use the dedicated factory", nameof(kind)),
        _ => new MouseAction(kind),
    };

    public static MouseAction SetDpiPreset(byte index) => new(ActionKind.SetDpiPreset, dpiPreset: index);
    public static MouseAction CustomShortcut(KeyCombo combo) => new(ActionKind.CustomShortcut, combo: combo);

    // Convenience accessors for the unit variants, so call sites read like the Rust enum.
    public static MouseAction None => Unit(ActionKind.None);
    public static MouseAction LeftClick => Unit(ActionKind.LeftClick);
    public static MouseAction RightClick => Unit(ActionKind.RightClick);
    public static MouseAction MiddleClick => Unit(ActionKind.MiddleClick);
    public static MouseAction MouseBack => Unit(ActionKind.MouseBack);
    public static MouseAction MouseForward => Unit(ActionKind.MouseForward);
    public static MouseAction Copy => Unit(ActionKind.Copy);
    public static MouseAction Paste => Unit(ActionKind.Paste);
    public static MouseAction Cut => Unit(ActionKind.Cut);
    public static MouseAction Undo => Unit(ActionKind.Undo);
    public static MouseAction Redo => Unit(ActionKind.Redo);
    public static MouseAction SelectAll => Unit(ActionKind.SelectAll);
    public static MouseAction Find => Unit(ActionKind.Find);
    public static MouseAction Save => Unit(ActionKind.Save);
    public static MouseAction BrowserBack => Unit(ActionKind.BrowserBack);
    public static MouseAction BrowserForward => Unit(ActionKind.BrowserForward);
    public static MouseAction NewTab => Unit(ActionKind.NewTab);
    public static MouseAction CloseTab => Unit(ActionKind.CloseTab);
    public static MouseAction ReopenTab => Unit(ActionKind.ReopenTab);
    public static MouseAction NextTab => Unit(ActionKind.NextTab);
    public static MouseAction PrevTab => Unit(ActionKind.PrevTab);
    public static MouseAction ReloadPage => Unit(ActionKind.ReloadPage);
    public static MouseAction TaskView => Unit(ActionKind.TaskView);
    public static MouseAction PreviousDesktop => Unit(ActionKind.PreviousDesktop);
    public static MouseAction NextDesktop => Unit(ActionKind.NextDesktop);
    public static MouseAction ShowDesktop => Unit(ActionKind.ShowDesktop);
    public static MouseAction StartMenu => Unit(ActionKind.StartMenu);
    public static MouseAction MaximizeWindow => Unit(ActionKind.MaximizeWindow);
    public static MouseAction MinimizeWindow => Unit(ActionKind.MinimizeWindow);
    public static MouseAction SnapWindowLeft => Unit(ActionKind.SnapWindowLeft);
    public static MouseAction SnapWindowRight => Unit(ActionKind.SnapWindowRight);
    public static MouseAction LockScreen => Unit(ActionKind.LockScreen);
    public static MouseAction Screenshot => Unit(ActionKind.Screenshot);
    public static MouseAction CaptureRegion => Unit(ActionKind.CaptureRegion);
    public static MouseAction PlayPause => Unit(ActionKind.PlayPause);
    public static MouseAction NextTrack => Unit(ActionKind.NextTrack);
    public static MouseAction PrevTrack => Unit(ActionKind.PrevTrack);
    public static MouseAction VolumeUp => Unit(ActionKind.VolumeUp);
    public static MouseAction VolumeDown => Unit(ActionKind.VolumeDown);
    public static MouseAction MuteVolume => Unit(ActionKind.MuteVolume);
    public static MouseAction CycleDpiPresets => Unit(ActionKind.CycleDpiPresets);
    public static MouseAction ToggleSmartShift => Unit(ActionKind.ToggleSmartShift);
    public static MouseAction ScrollUp => Unit(ActionKind.ScrollUp);
    public static MouseAction ScrollDown => Unit(ActionKind.ScrollDown);
    public static MouseAction HorizontalScrollLeft => Unit(ActionKind.HorizontalScrollLeft);
    public static MouseAction HorizontalScrollRight => Unit(ActionKind.HorizontalScrollRight);

    /// <summary>Display label for the popover row.</summary>
    public string Label() => Kind switch
    {
        ActionKind.None => "Do Nothing",
        ActionKind.LeftClick => "Left Click",
        ActionKind.RightClick => "Right Click",
        ActionKind.MiddleClick => "Middle Click",
        ActionKind.MouseBack => "Back (Button 4)",
        ActionKind.MouseForward => "Forward (Button 5)",
        ActionKind.Copy => "Copy",
        ActionKind.Paste => "Paste",
        ActionKind.Cut => "Cut",
        ActionKind.Undo => "Undo",
        ActionKind.Redo => "Redo",
        ActionKind.SelectAll => "Select All",
        ActionKind.Find => "Find",
        ActionKind.Save => "Save",
        ActionKind.BrowserBack => "Browser Back",
        ActionKind.BrowserForward => "Browser Forward",
        ActionKind.NewTab => "New Tab",
        ActionKind.CloseTab => "Close Tab",
        ActionKind.ReopenTab => "Reopen Tab",
        ActionKind.NextTab => "Next Tab",
        ActionKind.PrevTab => "Previous Tab",
        ActionKind.ReloadPage => "Reload Page",
        ActionKind.TaskView => "Task View",
        ActionKind.PreviousDesktop => "Previous Desktop",
        ActionKind.NextDesktop => "Next Desktop",
        ActionKind.ShowDesktop => "Show Desktop",
        ActionKind.StartMenu => "Start Menu",
        ActionKind.MaximizeWindow => "Maximize Window",
        ActionKind.MinimizeWindow => "Minimize Window",
        ActionKind.SnapWindowLeft => "Snap Window Left",
        ActionKind.SnapWindowRight => "Snap Window Right",
        ActionKind.LockScreen => "Lock Screen",
        ActionKind.Screenshot => "Screenshot",
        ActionKind.CaptureRegion => "Capture Region",
        ActionKind.PlayPause => "Play / Pause",
        ActionKind.NextTrack => "Next Track",
        ActionKind.PrevTrack => "Previous Track",
        ActionKind.VolumeUp => "Volume Up",
        ActionKind.VolumeDown => "Volume Down",
        ActionKind.MuteVolume => "Mute",
        ActionKind.CycleDpiPresets => "Cycle DPI Presets",
        ActionKind.SetDpiPreset => $"DPI Preset {DpiPreset + 1}",
        ActionKind.ToggleSmartShift => "Toggle SmartShift",
        ActionKind.ScrollUp => "Scroll Up",
        ActionKind.ScrollDown => "Scroll Down",
        ActionKind.HorizontalScrollLeft => "Scroll Left",
        ActionKind.HorizontalScrollRight => "Scroll Right",
        ActionKind.CustomShortcut => Combo!.RenderedLabel(),
        _ => Kind.ToString(),
    };

    /// <summary>Which <see cref="Category"/> this action belongs to (popover grouping).</summary>
    public Category Category() => Kind switch
    {
        ActionKind.LeftClick or ActionKind.RightClick or ActionKind.MiddleClick
            or ActionKind.MouseBack or ActionKind.MouseForward => Actions.Category.Mouse,
        ActionKind.Copy or ActionKind.Paste or ActionKind.Cut or ActionKind.Undo or ActionKind.Redo
            or ActionKind.SelectAll or ActionKind.Find or ActionKind.Save
            or ActionKind.CustomShortcut => Actions.Category.Editing,
        ActionKind.BrowserBack or ActionKind.BrowserForward or ActionKind.NewTab or ActionKind.CloseTab
            or ActionKind.ReopenTab or ActionKind.NextTab or ActionKind.PrevTab
            or ActionKind.ReloadPage => Actions.Category.Browser,
        ActionKind.TaskView or ActionKind.PreviousDesktop
            or ActionKind.NextDesktop or ActionKind.ShowDesktop
            or ActionKind.StartMenu or ActionKind.MaximizeWindow or ActionKind.MinimizeWindow
            or ActionKind.SnapWindowLeft or ActionKind.SnapWindowRight => Actions.Category.Navigation,
        ActionKind.None or ActionKind.LockScreen or ActionKind.Screenshot
            or ActionKind.CaptureRegion => Actions.Category.System,
        ActionKind.PlayPause or ActionKind.NextTrack or ActionKind.PrevTrack or ActionKind.VolumeUp
            or ActionKind.VolumeDown or ActionKind.MuteVolume => Actions.Category.Media,
        ActionKind.CycleDpiPresets or ActionKind.SetDpiPreset
            or ActionKind.ToggleSmartShift => Actions.Category.Dpi,
        ActionKind.ScrollUp or ActionKind.ScrollDown or ActionKind.HorizontalScrollLeft
            or ActionKind.HorizontalScrollRight => Actions.Category.Scroll,
        _ => Actions.Category.System,
    };

    /// <summary>
    /// All pickable actions in a deterministic order. <see cref="ActionKind.CustomShortcut"/>
    /// is excluded — it is opened via "Record shortcut…", not selected from the catalog.
    /// </summary>
    public static IReadOnlyList<MouseAction> Catalog() =>
    [
        LeftClick, RightClick, MiddleClick, MouseBack, MouseForward,
        Copy, Paste, Cut, Undo, Redo, SelectAll, Find, Save,
        BrowserBack, BrowserForward, NewTab, CloseTab, ReopenTab, NextTab, PrevTab, ReloadPage,
        TaskView, PreviousDesktop, NextDesktop, ShowDesktop, StartMenu,
        MaximizeWindow, MinimizeWindow, SnapWindowLeft, SnapWindowRight,
        None, LockScreen, Screenshot, CaptureRegion,
        PlayPause, NextTrack, PrevTrack, VolumeUp, VolumeDown, MuteVolume,
        CycleDpiPresets, ToggleSmartShift,
        ScrollUp, ScrollDown, HorizontalScrollLeft, HorizontalScrollRight,
    ];
}
