namespace OpenLogi.Core.Actions;

/// <summary>
/// Discriminator for <see cref="MouseAction"/>. Member names equal the Rust
/// variant names and form the on-disk TOML vocabulary — they are frozen.
/// </summary>
public enum ActionKind
{
    None,
    LeftClick, RightClick, MiddleClick, MouseBack, MouseForward,
    Copy, Paste, Cut, Undo, Redo, SelectAll, Find, Save,
    BrowserBack, BrowserForward, NewTab, CloseTab, ReopenTab, NextTab, PrevTab, ReloadPage,
    TaskView, PreviousDesktop, NextDesktop, ShowDesktop, StartMenu,
    MaximizeWindow, MinimizeWindow, SnapWindowLeft, SnapWindowRight,
    LockScreen, Screenshot, CaptureRegion,
    PlayPause, NextTrack, PrevTrack, VolumeUp, VolumeDown, MuteVolume,
    CycleDpiPresets, SetDpiPreset, ToggleSmartShift,
    ScrollUp, ScrollDown, HorizontalScrollLeft, HorizontalScrollRight,
    CustomShortcut,
}
