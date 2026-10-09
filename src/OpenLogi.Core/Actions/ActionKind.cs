namespace OpenLogi.Core.Actions;

/// <summary>
/// Discriminator for <see cref="MouseAction"/>. Member names equal the Rust
/// variant names and form the on-disk TOML vocabulary — they are frozen.
/// Each member declares its picker group, label key and payload in its
/// <see cref="ActionAttribute"/>; only <see cref="SetDpiPreset"/> and
/// <see cref="CustomShortcut"/> carry a payload.
/// </summary>
public enum ActionKind
{
    [Action(Category.System, "Action_None")] None,

    [Action(Category.Mouse, "Action_LeftClick")] LeftClick,
    [Action(Category.Mouse, "Action_RightClick")] RightClick,
    [Action(Category.Mouse, "Action_MiddleClick")] MiddleClick,
    [Action(Category.Mouse, "Action_MouseBack")] MouseBack,
    [Action(Category.Mouse, "Action_MouseForward")] MouseForward,

    [Action(Category.Editing, "Action_Copy")] Copy,
    [Action(Category.Editing, "Action_Paste")] Paste,
    [Action(Category.Editing, "Action_Cut")] Cut,
    [Action(Category.Editing, "Action_Undo")] Undo,
    [Action(Category.Editing, "Action_Redo")] Redo,
    [Action(Category.Editing, "Action_SelectAll")] SelectAll,
    [Action(Category.Editing, "Action_Find")] Find,
    [Action(Category.Editing, "Action_Save")] Save,

    [Action(Category.Browser, "Action_BrowserBack")] BrowserBack,
    [Action(Category.Browser, "Action_BrowserForward")] BrowserForward,
    [Action(Category.Browser, "Action_NewTab")] NewTab,
    [Action(Category.Browser, "Action_CloseTab")] CloseTab,
    [Action(Category.Browser, "Action_ReopenTab")] ReopenTab,
    [Action(Category.Browser, "Action_NextTab")] NextTab,
    [Action(Category.Browser, "Action_PrevTab")] PrevTab,
    [Action(Category.Browser, "Action_ReloadPage")] ReloadPage,

    [Action(Category.Navigation, "Action_TaskView")] TaskView,
    [Action(Category.Navigation, "Action_PreviousDesktop")] PreviousDesktop,
    [Action(Category.Navigation, "Action_NextDesktop")] NextDesktop,
    [Action(Category.Navigation, "Action_ShowDesktop")] ShowDesktop,
    [Action(Category.Navigation, "Action_StartMenu")] StartMenu,
    [Action(Category.Navigation, "Action_MaximizeWindow")] MaximizeWindow,
    [Action(Category.Navigation, "Action_MinimizeWindow")] MinimizeWindow,
    [Action(Category.Navigation, "Action_SnapWindowLeft")] SnapWindowLeft,
    [Action(Category.Navigation, "Action_SnapWindowRight")] SnapWindowRight,

    [Action(Category.System, "Action_LockScreen")] LockScreen,
    [Action(Category.System, "Action_Screenshot")] Screenshot,
    [Action(Category.System, "Action_CaptureRegion")] CaptureRegion,

    [Action(Category.Media, "Action_PlayPause")] PlayPause,
    [Action(Category.Media, "Action_NextTrack")] NextTrack,
    [Action(Category.Media, "Action_PrevTrack")] PrevTrack,
    [Action(Category.Media, "Action_VolumeUp")] VolumeUp,
    [Action(Category.Media, "Action_VolumeDown")] VolumeDown,
    [Action(Category.Media, "Action_MuteVolume")] MuteVolume,

    [Action(Category.Dpi, "Action_CycleDpiPresets")] CycleDpiPresets,
    [Action(Category.Dpi, "Action_SetDpiPreset", ActionPayload.DpiPreset)] SetDpiPreset,
    [Action(Category.Dpi, "Action_ToggleSmartShift")] ToggleSmartShift,

    [Action(Category.Scroll, "Action_ScrollUp")] ScrollUp,
    [Action(Category.Scroll, "Action_ScrollDown")] ScrollDown,
    [Action(Category.Scroll, "Action_HorizontalScrollLeft")] HorizontalScrollLeft,
    [Action(Category.Scroll, "Action_HorizontalScrollRight")] HorizontalScrollRight,

    [Action(Category.Keyboard, "Action_CustomShortcut", ActionPayload.Shortcut)] CustomShortcut,
}
