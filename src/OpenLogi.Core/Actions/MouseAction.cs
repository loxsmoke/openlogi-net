using OpenLogi.Core.Config;
using OpenLogi.Core.Localization;

namespace OpenLogi.Core.Actions;

/// <summary>
/// What pressing a <see cref="ButtonId"/> should do. A tagged union: most
/// variants are unit (a bare <see cref="ActionKind"/>), <see cref="ActionKind.SetDpiPreset"/>
/// carries a preset index, and <see cref="ActionKind.CustomShortcut"/> carries a
/// <see cref="KeyCombo"/>. Pure config data — OS event synthesis lives in
/// OpenLogi.Input. Ported from Rust <c>binding::Action</c>. Per-kind facts
/// (group, label key, payload) come from the kind's <see cref="ActionAttribute"/>.
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

    /// <summary>The kind's declared metadata.</summary>
    public static ActionAttribute Info(ActionKind kind) => EnumMetadata.Get<ActionKind, ActionAttribute>(kind);

    private ActionAttribute Info_ => Info(Kind);

    /// <summary>A unit (payload-free) action. Throws for the payload-carrying kinds.</summary>
    public static MouseAction Unit(ActionKind kind) =>
        Info(kind).Payload == ActionPayload.None
            ? new MouseAction(kind)
            : throw new ArgumentException($"{kind} carries a payload; use the dedicated factory", nameof(kind));

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

    /// <summary>
    /// Display label for the picker row: the kind's resource string, formatted with the
    /// preset number for <see cref="ActionKind.SetDpiPreset"/>; the combo text itself for
    /// <see cref="ActionKind.CustomShortcut"/>.
    /// </summary>
    public string Label() => Info_.Payload switch
    {
        ActionPayload.DpiPreset => Loc.Current.Format(Info_.LabelKey, DpiPreset + 1),
        ActionPayload.Shortcut => Combo!.Label(),
        _ => Loc.Current[Info_.LabelKey],
    };

    /// <summary>Which <see cref="Category"/> this action belongs to (picker grouping).</summary>
    public Category Category() => Info_.Category;

    private static readonly IReadOnlyList<MouseAction> CatalogList =
        [.. Enum.GetValues<ActionKind>().Where(k => Info(k).Payload == ActionPayload.None).Select(Unit)];

    /// <summary>
    /// All pickable actions in declaration order: every payload-free kind. The two
    /// payload kinds are excluded — <see cref="ActionKind.CustomShortcut"/> is offered
    /// by the picker through its own recorded row, <see cref="ActionKind.SetDpiPreset"/>
    /// has no picker UI.
    /// </summary>
    public static IReadOnlyList<MouseAction> Catalog() => CatalogList;
}
