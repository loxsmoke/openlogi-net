using OpenLogi.Core.Config;
using OpenLogi.Core.Localization;

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
        ActionKind.None => Loc.Current["Action_None"],
        ActionKind.LeftClick => Loc.Current["Action_LeftClick"],
        ActionKind.RightClick => Loc.Current["Action_RightClick"],
        ActionKind.MiddleClick => Loc.Current["Action_MiddleClick"],
        ActionKind.MouseBack => Loc.Current["Action_MouseBack"],
        ActionKind.MouseForward => Loc.Current["Action_MouseForward"],
        ActionKind.Copy => Loc.Current["Action_Copy"],
        ActionKind.Paste => Loc.Current["Action_Paste"],
        ActionKind.Cut => Loc.Current["Action_Cut"],
        ActionKind.Undo => Loc.Current["Action_Undo"],
        ActionKind.Redo => Loc.Current["Action_Redo"],
        ActionKind.SelectAll => Loc.Current["Action_SelectAll"],
        ActionKind.Find => Loc.Current["Action_Find"],
        ActionKind.Save => Loc.Current["Action_Save"],
        ActionKind.BrowserBack => Loc.Current["Action_BrowserBack"],
        ActionKind.BrowserForward => Loc.Current["Action_BrowserForward"],
        ActionKind.NewTab => Loc.Current["Action_NewTab"],
        ActionKind.CloseTab => Loc.Current["Action_CloseTab"],
        ActionKind.ReopenTab => Loc.Current["Action_ReopenTab"],
        ActionKind.NextTab => Loc.Current["Action_NextTab"],
        ActionKind.PrevTab => Loc.Current["Action_PrevTab"],
        ActionKind.ReloadPage => Loc.Current["Action_ReloadPage"],
        ActionKind.TaskView => Loc.Current["Action_TaskView"],
        ActionKind.PreviousDesktop => Loc.Current["Action_PreviousDesktop"],
        ActionKind.NextDesktop => Loc.Current["Action_NextDesktop"],
        ActionKind.ShowDesktop => Loc.Current["Action_ShowDesktop"],
        ActionKind.StartMenu => Loc.Current["Action_StartMenu"],
        ActionKind.MaximizeWindow => Loc.Current["Action_MaximizeWindow"],
        ActionKind.MinimizeWindow => Loc.Current["Action_MinimizeWindow"],
        ActionKind.SnapWindowLeft => Loc.Current["Action_SnapWindowLeft"],
        ActionKind.SnapWindowRight => Loc.Current["Action_SnapWindowRight"],
        ActionKind.LockScreen => Loc.Current["Action_LockScreen"],
        ActionKind.Screenshot => Loc.Current["Action_Screenshot"],
        ActionKind.CaptureRegion => Loc.Current["Action_CaptureRegion"],
        ActionKind.PlayPause => Loc.Current["Action_PlayPause"],
        ActionKind.NextTrack => Loc.Current["Action_NextTrack"],
        ActionKind.PrevTrack => Loc.Current["Action_PrevTrack"],
        ActionKind.VolumeUp => Loc.Current["Action_VolumeUp"],
        ActionKind.VolumeDown => Loc.Current["Action_VolumeDown"],
        ActionKind.MuteVolume => Loc.Current["Action_MuteVolume"],
        ActionKind.CycleDpiPresets => Loc.Current["Action_CycleDpiPresets"],
        ActionKind.SetDpiPreset => Loc.Current.Format("Action_SetDpiPreset", DpiPreset + 1),
        ActionKind.ToggleSmartShift => Loc.Current["Action_ToggleSmartShift"],
        ActionKind.ScrollUp => Loc.Current["Action_ScrollUp"],
        ActionKind.ScrollDown => Loc.Current["Action_ScrollDown"],
        ActionKind.HorizontalScrollLeft => Loc.Current["Action_HorizontalScrollLeft"],
        ActionKind.HorizontalScrollRight => Loc.Current["Action_HorizontalScrollRight"],
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
