namespace OpenLogi.Core;

/// <summary>
/// One of the user-rebindable hotspots on a Logi mouse. Order matches the
/// physical layout front-to-side. Variant <em>names</em> are TOML-stable (they
/// are config keys). Ported from Rust <c>binding::ButtonId</c>.
/// </summary>
public enum ButtonId
{
    LeftClick,
    RightClick,
    MiddleClick,
    Back,
    Forward,
    /// <summary>The "ModeShift" button under the wheel. Named DpiToggle for historical reasons.</summary>
    DpiToggle,
    /// <summary>The horizontal thumb wheel's click.</summary>
    Thumbwheel,
    /// <summary>Rotating the thumb wheel "up" (positive rotation).</summary>
    ThumbwheelScrollUp,
    /// <summary>Rotating the thumb wheel "down" (negative rotation).</summary>
    ThumbwheelScrollDown,
    /// <summary>The HID++ gesture button on MX-line devices.</summary>
    GestureButton,
}

public static class ButtonIdExtensions
{
    public static readonly ButtonId[] All =
    [
        ButtonId.LeftClick, ButtonId.RightClick, ButtonId.MiddleClick, ButtonId.Back,
        ButtonId.Forward, ButtonId.DpiToggle, ButtonId.Thumbwheel, ButtonId.ThumbwheelScrollUp,
        ButtonId.ThumbwheelScrollDown, ButtonId.GestureButton,
    ];

    /// <summary>
    /// Whether this button is one the OS hook remaps: Middle, Back, or Forward.
    /// The primary clicks pass through; DPI/thumb/gesture controls are captured
    /// over HID++, not visible to the OS hook.
    /// </summary>
    public static bool IsOsHookButton(this ButtonId id) =>
        id is ButtonId.MiddleClick or ButtonId.Back or ButtonId.Forward;

    /// <summary>Human-readable label for popovers and tooltips.</summary>
    public static string Label(this ButtonId id) => id switch
    {
        ButtonId.LeftClick => "Left Click",
        ButtonId.RightClick => "Right Click",
        ButtonId.MiddleClick => "Middle Click",
        ButtonId.Back => "Back",
        ButtonId.Forward => "Forward",
        ButtonId.DpiToggle => "DPI Toggle",
        ButtonId.Thumbwheel => "Thumb Wheel",
        ButtonId.ThumbwheelScrollUp => "Thumb Wheel Up",
        ButtonId.ThumbwheelScrollDown => "Thumb Wheel Down",
        ButtonId.GestureButton => "Gesture Button",
        _ => id.ToString(),
    };
}

/// <summary>
/// One of the five gesture-button sub-bindings: hold + swipe up/down/left/right,
/// or a plain click. Variant names are TOML-stable. Ported from Rust
/// <c>binding::GestureDirection</c>.
/// </summary>
public enum GestureDirection { Up, Down, Left, Right, Click }

public static class GestureDirectionExtensions
{
    public static readonly GestureDirection[] All =
        [GestureDirection.Up, GestureDirection.Down, GestureDirection.Left, GestureDirection.Right, GestureDirection.Click];

    public static string Label(this GestureDirection d) => d switch
    {
        GestureDirection.Up => "Up",
        GestureDirection.Down => "Down",
        GestureDirection.Left => "Left",
        GestureDirection.Right => "Right",
        GestureDirection.Click => "Click",
        _ => d.ToString(),
    };

    public static string Glyph(this GestureDirection d) => d switch
    {
        GestureDirection.Up => "↑",
        GestureDirection.Down => "↓",
        GestureDirection.Left => "←",
        GestureDirection.Right => "→",
        GestureDirection.Click => "·",
        _ => "·",
    };
}

/// <summary>Grouping for the action-picker section headers.</summary>
public enum Category { Editing, Browser, Media, Mouse, Dpi, Scroll, Navigation, System }

public static class CategoryExtensions
{
    /// <summary>Short, already-uppercase label for popover section headers.</summary>
    public static string Label(this Category c) => c switch
    {
        Category.Editing => "EDITING",
        Category.Browser => "BROWSER",
        Category.Media => "MEDIA",
        Category.Mouse => "MOUSE",
        Category.Dpi => "DPI",
        Category.Scroll => "SCROLL",
        Category.Navigation => "NAVIGATION",
        Category.System => "SYSTEM",
        _ => c.ToString().ToUpperInvariant(),
    };
}

/// <summary>
/// A modifier + virtual-key keystroke. <see cref="Modifiers"/> is a bitmask of
/// <see cref="ModCmd"/> etc.; <see cref="KeyCode"/> is the macOS virtual key
/// (kVK_*). <see cref="Display"/> is a pre-rendered label for the UI. Ported
/// from Rust <c>binding::KeyCombo</c>.
/// </summary>
public sealed record KeyCombo
{
    public const byte ModCmd = 1 << 0;
    public const byte ModShift = 1 << 1;
    public const byte ModCtrl = 1 << 2;
    public const byte ModOption = 1 << 3;

    public required byte Modifiers { get; init; }
    public required ushort KeyCode { get; init; }
    public string Display { get; init; } = "";

    /// <summary>
    /// Build the human-readable label from the modifier bitmask + key code,
    /// or return <see cref="Display"/> when set.
    /// </summary>
    public string RenderedLabel()
    {
        if (Display.Length != 0) return Display;
        var sb = new System.Text.StringBuilder();
        if ((Modifiers & ModCtrl) != 0) sb.Append('⌃');
        if ((Modifiers & ModOption) != 0) sb.Append('⌥');
        if ((Modifiers & ModShift) != 0) sb.Append('⇧');
        if ((Modifiers & ModCmd) != 0) sb.Append('⌘');
        sb.Append(KeyCode switch
        {
            0x00 => "A", 0x01 => "S", 0x02 => "D", 0x03 => "F", 0x06 => "Z", 0x07 => "X",
            0x08 => "C", 0x09 => "V", 0x0B => "B", 0x0C => "Q", 0x0D => "W", 0x0E => "E",
            0x0F => "R", 0x10 => "Y", 0x11 => "T", 0x20 => "U", 0x22 => "I", 0x1F => "O",
            0x23 => "P",
            _ => $"key 0x{KeyCode:X2}",
        });
        return sb.ToString();
    }
}

/// <summary>
/// Discriminator for <see cref="Action"/>. Member names equal the Rust
/// variant names and form the on-disk TOML vocabulary — they are frozen.
/// </summary>
public enum ActionKind
{
    None,
    LeftClick, RightClick, MiddleClick, MouseBack, MouseForward,
    Copy, Paste, Cut, Undo, Redo, SelectAll, Find, Save,
    BrowserBack, BrowserForward, NewTab, CloseTab, ReopenTab, NextTab, PrevTab, ReloadPage,
    TaskView, PreviousDesktop, NextDesktop, ShowDesktop, LaunchpadShow,
    LockScreen, Screenshot, CaptureRegion,
    PlayPause, NextTrack, PrevTrack, VolumeUp, VolumeDown, MuteVolume,
    CycleDpiPresets, SetDpiPreset, ToggleSmartShift,
    ScrollUp, ScrollDown, HorizontalScrollLeft, HorizontalScrollRight,
    CustomShortcut,
}

/// <summary>
/// What pressing a <see cref="ButtonId"/> should do. A tagged union: most
/// variants are unit (a bare <see cref="ActionKind"/>), <see cref="ActionKind.SetDpiPreset"/>
/// carries a preset index, and <see cref="ActionKind.CustomShortcut"/> carries a
/// <see cref="KeyCombo"/>. Pure config data — OS event synthesis lives in
/// OpenLogi.Input. Ported from Rust <c>binding::Action</c>.
/// </summary>
public sealed record Action
{
    public ActionKind Kind { get; }
    /// <summary>Preset index for <see cref="ActionKind.SetDpiPreset"/>; otherwise 0.</summary>
    public byte DpiPreset { get; }
    /// <summary>Chord for <see cref="ActionKind.CustomShortcut"/>; otherwise <c>null</c>.</summary>
    public KeyCombo? Combo { get; }

    private Action(ActionKind kind, byte dpiPreset = 0, KeyCombo? combo = null)
    {
        Kind = kind;
        DpiPreset = dpiPreset;
        Combo = combo;
    }

    /// <summary>A unit (payload-free) action. Throws for the payload-carrying kinds.</summary>
    public static Action Unit(ActionKind kind) => kind switch
    {
        ActionKind.SetDpiPreset or ActionKind.CustomShortcut =>
            throw new ArgumentException($"{kind} carries a payload; use the dedicated factory", nameof(kind)),
        _ => new Action(kind),
    };

    public static Action SetDpiPreset(byte index) => new(ActionKind.SetDpiPreset, dpiPreset: index);
    public static Action CustomShortcut(KeyCombo combo) => new(ActionKind.CustomShortcut, combo: combo);

    // Convenience accessors for the unit variants, so call sites read like the Rust enum.
    public static Action None => Unit(ActionKind.None);
    public static Action LeftClick => Unit(ActionKind.LeftClick);
    public static Action RightClick => Unit(ActionKind.RightClick);
    public static Action MiddleClick => Unit(ActionKind.MiddleClick);
    public static Action MouseBack => Unit(ActionKind.MouseBack);
    public static Action MouseForward => Unit(ActionKind.MouseForward);
    public static Action Copy => Unit(ActionKind.Copy);
    public static Action Paste => Unit(ActionKind.Paste);
    public static Action Cut => Unit(ActionKind.Cut);
    public static Action Undo => Unit(ActionKind.Undo);
    public static Action Redo => Unit(ActionKind.Redo);
    public static Action SelectAll => Unit(ActionKind.SelectAll);
    public static Action Find => Unit(ActionKind.Find);
    public static Action Save => Unit(ActionKind.Save);
    public static Action BrowserBack => Unit(ActionKind.BrowserBack);
    public static Action BrowserForward => Unit(ActionKind.BrowserForward);
    public static Action NewTab => Unit(ActionKind.NewTab);
    public static Action CloseTab => Unit(ActionKind.CloseTab);
    public static Action ReopenTab => Unit(ActionKind.ReopenTab);
    public static Action NextTab => Unit(ActionKind.NextTab);
    public static Action PrevTab => Unit(ActionKind.PrevTab);
    public static Action ReloadPage => Unit(ActionKind.ReloadPage);
    public static Action TaskView => Unit(ActionKind.TaskView);
    public static Action PreviousDesktop => Unit(ActionKind.PreviousDesktop);
    public static Action NextDesktop => Unit(ActionKind.NextDesktop);
    public static Action ShowDesktop => Unit(ActionKind.ShowDesktop);
    public static Action LaunchpadShow => Unit(ActionKind.LaunchpadShow);
    public static Action LockScreen => Unit(ActionKind.LockScreen);
    public static Action Screenshot => Unit(ActionKind.Screenshot);
    public static Action CaptureRegion => Unit(ActionKind.CaptureRegion);
    public static Action PlayPause => Unit(ActionKind.PlayPause);
    public static Action NextTrack => Unit(ActionKind.NextTrack);
    public static Action PrevTrack => Unit(ActionKind.PrevTrack);
    public static Action VolumeUp => Unit(ActionKind.VolumeUp);
    public static Action VolumeDown => Unit(ActionKind.VolumeDown);
    public static Action MuteVolume => Unit(ActionKind.MuteVolume);
    public static Action CycleDpiPresets => Unit(ActionKind.CycleDpiPresets);
    public static Action ToggleSmartShift => Unit(ActionKind.ToggleSmartShift);
    public static Action ScrollUp => Unit(ActionKind.ScrollUp);
    public static Action ScrollDown => Unit(ActionKind.ScrollDown);
    public static Action HorizontalScrollLeft => Unit(ActionKind.HorizontalScrollLeft);
    public static Action HorizontalScrollRight => Unit(ActionKind.HorizontalScrollRight);

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
        ActionKind.LaunchpadShow => "Launchpad",
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
            or ActionKind.MouseBack or ActionKind.MouseForward => OpenLogi.Core.Category.Mouse,
        ActionKind.Copy or ActionKind.Paste or ActionKind.Cut or ActionKind.Undo or ActionKind.Redo
            or ActionKind.SelectAll or ActionKind.Find or ActionKind.Save
            or ActionKind.CustomShortcut => OpenLogi.Core.Category.Editing,
        ActionKind.BrowserBack or ActionKind.BrowserForward or ActionKind.NewTab or ActionKind.CloseTab
            or ActionKind.ReopenTab or ActionKind.NextTab or ActionKind.PrevTab
            or ActionKind.ReloadPage => OpenLogi.Core.Category.Browser,
        ActionKind.TaskView or ActionKind.PreviousDesktop
            or ActionKind.NextDesktop or ActionKind.ShowDesktop
            or ActionKind.LaunchpadShow => OpenLogi.Core.Category.Navigation,
        ActionKind.None or ActionKind.LockScreen or ActionKind.Screenshot
            or ActionKind.CaptureRegion => OpenLogi.Core.Category.System,
        ActionKind.PlayPause or ActionKind.NextTrack or ActionKind.PrevTrack or ActionKind.VolumeUp
            or ActionKind.VolumeDown or ActionKind.MuteVolume => OpenLogi.Core.Category.Media,
        ActionKind.CycleDpiPresets or ActionKind.SetDpiPreset
            or ActionKind.ToggleSmartShift => OpenLogi.Core.Category.Dpi,
        ActionKind.ScrollUp or ActionKind.ScrollDown or ActionKind.HorizontalScrollLeft
            or ActionKind.HorizontalScrollRight => OpenLogi.Core.Category.Scroll,
        _ => OpenLogi.Core.Category.System,
    };

    /// <summary>
    /// All pickable actions in a deterministic order. <see cref="ActionKind.CustomShortcut"/>
    /// is excluded — it is opened via "Record shortcut…", not selected from the catalog.
    /// </summary>
    public static IReadOnlyList<Action> Catalog() =>
    [
        LeftClick, RightClick, MiddleClick, MouseBack, MouseForward,
        Copy, Paste, Cut, Undo, Redo, SelectAll, Find, Save,
        BrowserBack, BrowserForward, NewTab, CloseTab, ReopenTab, NextTab, PrevTab, ReloadPage,
        TaskView, PreviousDesktop, NextDesktop, ShowDesktop, LaunchpadShow,
        None, LockScreen, Screenshot, CaptureRegion,
        PlayPause, NextTrack, PrevTrack, VolumeUp, VolumeDown, MuteVolume,
        CycleDpiPresets, ToggleSmartShift,
        ScrollUp, ScrollDown, HorizontalScrollLeft, HorizontalScrollRight,
    ];
}

/// <summary>
/// What a single rebindable <see cref="ButtonId"/> does: one <see cref="Action"/>
/// (<see cref="Single"/>), or — for a button in gesture mode — a per-direction map
/// (<see cref="Gesture"/>). Ported from Rust <c>binding::Binding</c>. Immutable;
/// the in-place Rust mutators return a new instance here.
/// </summary>
public abstract record Binding
{
    private Binding() { }

    /// <summary>One action, fired on press.</summary>
    public sealed record Single(Action Action) : Binding;

    /// <summary>Per-direction sub-bindings for a button in gesture mode.</summary>
    public sealed record Gesture : Binding
    {
        public SortedDictionary<GestureDirection, Action> Map { get; }

        public Gesture(SortedDictionary<GestureDirection, Action> map) => Map = map;

        public Gesture(IEnumerable<KeyValuePair<GestureDirection, Action>> entries)
            => Map = new SortedDictionary<GestureDirection, Action>(entries.ToDictionary(e => e.Key, e => e.Value));

        public bool Equals(Gesture? other)
        {
            if (other is null || Map.Count != other.Map.Count) return false;
            foreach (var (k, v) in Map)
                if (!other.Map.TryGetValue(k, out var ov) || !v.Equals(ov)) return false;
            return true;
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var (k, v) in Map) { hash.Add(k); hash.Add(v); }
            return hash.ToHashCode();
        }
    }

    /// <summary>The plain-click action: the <see cref="Single"/> action, or the gesture
    /// map's <see cref="GestureDirection.Click"/> entry (falling back to <see cref="Action.None"/>).</summary>
    public Action ClickAction() => this switch
    {
        Single s => s.Action,
        Gesture g => g.Map.TryGetValue(GestureDirection.Click, out var a) ? a : Action.None,
        _ => Action.None,
    };

    /// <summary>The action bound to <paramref name="direction"/>, if this is a gesture binding.</summary>
    public Action? DirectionAction(GestureDirection direction) => this switch
    {
        Gesture g => g.Map.TryGetValue(direction, out var a) ? a : null,
        _ => null,
    };

    /// <summary>Whether this binding drives raw-XY swipe capture.</summary>
    public bool IsGesture() => this is Gesture;

    /// <summary>
    /// Promote a <see cref="Single"/> to a <see cref="Gesture"/>, keeping its action as
    /// the <see cref="GestureDirection.Click"/> entry. A no-op when already a gesture.
    /// </summary>
    public Binding UpgradeToGesture() => this switch
    {
        Single s => new Gesture(new SortedDictionary<GestureDirection, Action>
        {
            [GestureDirection.Click] = s.Action,
        }),
        _ => this,
    };

    /// <summary>
    /// Fill any unbound directions of a gesture binding with their canonical
    /// default, so a promoted button exposes the full five-direction set.
    /// A no-op on <see cref="Single"/>; existing user choices are preserved.
    /// </summary>
    public Binding FillGestureDefaults()
    {
        if (this is not Gesture g) return this;
        var map = new SortedDictionary<GestureDirection, Action>(g.Map);
        foreach (var dir in GestureDirectionExtensions.All)
            if (!map.ContainsKey(dir))
                map[dir] = Bindings.DefaultGestureBinding(dir);
        return new Gesture(map);
    }

    public static implicit operator Binding(Action action) => new Single(action);
}

/// <summary>Canonical default bindings. Ported from the free functions in Rust <c>binding</c>.</summary>
public static class Bindings
{
    /// <summary>Sensible default action for a fresh button so the panel isn't empty.</summary>
    public static Action DefaultBinding(ButtonId button) => button switch
    {
        ButtonId.LeftClick => Action.LeftClick,
        ButtonId.RightClick => Action.RightClick,
        ButtonId.MiddleClick => Action.MiddleClick,
        ButtonId.Back => Action.BrowserBack,
        ButtonId.Forward => Action.BrowserForward,
        ButtonId.DpiToggle => Action.CycleDpiPresets,
        ButtonId.Thumbwheel => Action.TaskView,
        ButtonId.ThumbwheelScrollUp => Action.HorizontalScrollRight,
        ButtonId.ThumbwheelScrollDown => Action.HorizontalScrollLeft,
        ButtonId.GestureButton => Action.TaskView,
        _ => Action.None,
    };

    /// <summary>Per-direction defaults for the gesture button.</summary>
    public static Action DefaultGestureBinding(GestureDirection direction) => direction switch
    {
        GestureDirection.Up => Action.TaskView,
        GestureDirection.Down => Action.ShowDesktop,
        GestureDirection.Left => Action.PrevTab,
        GestureDirection.Right => Action.NextTab,
        GestureDirection.Click => Action.TaskView,
        _ => Action.None,
    };

    /// <summary>
    /// The canonical default <see cref="Binding"/> for a fresh button:
    /// <see cref="ButtonId.GestureButton"/> defaults to a full gesture map; every
    /// other button to a <see cref="Binding.Single"/> of its <see cref="DefaultBinding"/>.
    /// </summary>
    public static Binding DefaultBindingFor(ButtonId button) => button switch
    {
        ButtonId.GestureButton => new Binding.Gesture(
            GestureDirectionExtensions.All.Select(d =>
                new KeyValuePair<GestureDirection, Action>(d, DefaultGestureBinding(d)))),
        _ => new Binding.Single(DefaultBinding(button)),
    };
}
