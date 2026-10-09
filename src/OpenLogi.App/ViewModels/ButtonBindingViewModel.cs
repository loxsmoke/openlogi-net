using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenLogi.Core.Actions;
using OpenLogi.Core.Config;
using OpenLogi.Core.Gestures;
using CoreAction = OpenLogi.Core.Actions.MouseAction;
using OpenLogi.Core.Localization;

namespace OpenLogi.App.ViewModels;

/// <summary>Asks the user to record a chord; <c>null</c> = cancelled, <see cref="KeyCombo.Empty"/> = cleared.</summary>
public delegate Task<KeyCombo?> ShortcutRecorder(KeyCombo current);

/// <summary>
/// A row in the grouped action picker. <see cref="Selectable"/> drives the item
/// container's IsEnabled, so group headers are skipped by mouse and keyboard.
/// </summary>
public interface IActionPickerItem
{
    bool Selectable { get; }
}

/// <summary>A selectable picker row: carries the action it stands for.</summary>
public abstract partial class PickerChoice : ObservableObject, IActionPickerItem
{
    public abstract CoreAction Action { get; }
    public abstract string Label { get; }
    public bool Selectable => true;
}

/// <summary>A catalog action in the picker dropdown (wraps an action with a label).</summary>
public sealed partial class ActionChoice : PickerChoice
{
    public override CoreAction Action { get; }
    public override string Label => Action.Label();

    public ActionChoice(CoreAction action)
    {
        Action = action;
        Loc.Current.WeakSubscribe(this, static (self, e) =>
        {
            if (e.PropertyName is nameof(Loc.Culture) or "Item[]")
                self.OnPropertyChanged(nameof(Label));
        });
    }
}

/// <summary>
/// The one Keyboard-group row, owned by a single editor: "Keyboard Shortcut" plus, on
/// the right, the chord it currently holds (or "None"). Its action is always a
/// <see cref="ActionKind.CustomShortcut"/>; an empty chord is the cleared state.
/// </summary>
public sealed partial class ShortcutChoice : PickerChoice
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail), nameof(IsAssigned), nameof(Action))]
    private KeyCombo _combo = KeyCombo.Empty;

    /// <summary>Whether a chord has been recorded (an unassigned row opens the recorder when picked).</summary>
    public bool IsAssigned => !Combo.IsEmpty;

    public override CoreAction Action => CoreAction.CustomShortcut(Combo);

    /// <summary>The row title, from the kind's declared label key.</summary>
    public override string Label => Loc.Current[CoreAction.Info(ActionKind.CustomShortcut).LabelKey];

    /// <summary>The chord text, or the localized "None".</summary>
    public string Detail => Combo.Label();

    public ShortcutChoice()
    {
        Loc.Current.WeakSubscribe(this, static (self, e) =>
        {
            if (e.PropertyName is nameof(Loc.Culture) or "Item[]")
            {
                self.OnPropertyChanged(nameof(Label));
                self.OnPropertyChanged(nameof(Detail));
            }
        });
    }
}

/// <summary>A non-selectable group-header row (category name + horizontal rule).</summary>
public sealed partial class ActionGroupHeader : ObservableObject, IActionPickerItem
{
    public Category Category { get; }
    public string Name => Category.Label();
    public bool Selectable => false;

    public ActionGroupHeader(Category category)
    {
        Category = category;
        Loc.Current.WeakSubscribe(this, static (self, e) =>
        {
            if (e.PropertyName is nameof(Loc.Culture) or "Item[]")
                self.OnPropertyChanged(nameof(Name));
        });
    }
}

/// <summary>
/// A choice in the panel's Button dropdown: a button to edit, flagged with whether
/// the device can divert it for gestures. <see cref="Label"/> is the display text.
/// </summary>
public sealed partial class GestureOwnerChoice : ObservableObject
{
    public ButtonId? Button { get; }

    /// <summary>Whether the device can capture this button for gestures (HID++-divertible).</summary>
    public bool CanGesture { get; }

    public string Label => Button switch
    {
        ButtonId.GestureButton => Loc.Current["GestureOwner_GestureButton"],
        ButtonId.DpiToggle => Loc.Current["GestureOwner_DpiToggle"],
        { } button => button.Label(),
        _ => "",
    };

    public GestureOwnerChoice(ButtonId? button, bool canGesture = false)
    {
        Button = button;
        CanGesture = canGesture;
        Loc.Current.WeakSubscribe(this, static (self, e) =>
        {
            if (e.PropertyName is nameof(Loc.Culture) or "Item[]")
                self.OnPropertyChanged(nameof(Label));
        });
    }
}

/// <summary>
/// A named per-direction gesture set for the Category dropdown (mirrors the
/// Options+ gesture sets). The <c>Custom</c> sentinel (null actions) is selected
/// whenever the four swipes don't match any preset, and applies nothing.
/// </summary>
public sealed partial class GesturePreset : ObservableObject
{
    public string Name => Loc.Current[Key];
    public string Key { get; }
    public CoreAction? Up { get; }
    public CoreAction? Down { get; }
    public CoreAction? Left { get; }
    public CoreAction? Right { get; }

    public GesturePreset(string key, CoreAction? up, CoreAction? down, CoreAction? left, CoreAction? right)
    {
        Key = key;
        Up = up;
        Down = down;
        Left = left;
        Right = right;
        Loc.Current.WeakSubscribe(this, static (self, e) =>
        {
            if (e.PropertyName is nameof(Loc.Culture) or "Item[]")
                self.OnPropertyChanged(nameof(Name));
        });
    }

    public bool IsCustom => Up is null;

    /// <summary>The preset's action for <paramref name="direction"/> (swipes only).</summary>
    public CoreAction? For(GestureDirection direction) => direction switch
    {
        GestureDirection.Up => Up,
        GestureDirection.Down => Down,
        GestureDirection.Left => Left,
        GestureDirection.Right => Right,
        _ => null,
    };
}

/// <summary>
/// The action picker shared by every editor: the grouped catalog plus this editor's
/// own <see cref="ShortcutRow"/>, the current selection, and how a pick becomes a
/// persisted action. Picking the unassigned shortcut row opens the recorder
/// instead of selecting; the row is selected (and persisted) once a chord comes back.
/// </summary>
public abstract partial class ActionPickerEditor : ObservableObject
{
    private readonly ShortcutRecorder? _recorder;
    private int _recordToken;

    /// <summary>Set while the selection is being filled programmatically — no persist.</summary>
    protected bool Suppress;

    /// <summary>The catalog rows (payload-free actions), shared across editors.</summary>
    public IReadOnlyList<ActionChoice> Choices { get; }

    /// <summary>This editor's Keyboard Shortcut row.</summary>
    public ShortcutChoice ShortcutRow { get; } = new();

    /// <summary>Grouped items (headers + choices) for the dropdown, including <see cref="ShortcutRow"/>.</summary>
    public IReadOnlyList<IActionPickerItem> GroupedChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShortcutAssigned))]
    private PickerChoice _selected;

    /// <summary>Whether the shortcut row is selected (shows the Change… button, cleared or not).</summary>
    public bool IsShortcutAssigned => Selected is ShortcutChoice;

    /// <summary>
    /// The picker's SelectedItem. Widened to <see cref="IActionPickerItem"/> because the
    /// dropdown also holds group headers: <see cref="IActionPickerItem.Selectable"/> keeps
    /// a click off them, but wheel and arrow-key selection moves by index and steps onto
    /// them regardless, so a header does reach this setter. <see cref="Land"/> carries it
    /// on to the next action instead. A direct pick of the unassigned shortcut row does
    /// not select it: the previous selection is pushed back and the recorder opens.
    /// </summary>
    public IActionPickerItem SelectedPickerItem
    {
        get => Selected;
        set
        {
            var landed = Land(value, Selected);
            if (landed is ShortcutChoice { IsAssigned: false } row && !ReferenceEquals(row, Selected))
            {
                OnPropertyChanged();
                _ = RecordAsync(row);
                return;
            }
            Selected = landed;
            OnPropertyChanged();
        }
    }

    protected ActionPickerEditor(IReadOnlyList<ActionChoice> choices, ShortcutRecorder? recorder)
    {
        Choices = choices;
        _recorder = recorder;
        GroupedChoices = WithShortcutRow(
            ReferenceEquals(choices, ButtonBindingViewModel.Catalog) ? ButtonBindingViewModel.GroupedCatalog : BuildGrouped(choices),
            ShortcutRow);
        _selected = choices.Count > 0 ? choices[0] : ShortcutRow;
    }

    /// <summary>Write the chosen action wherever this editor persists to.</summary>
    protected abstract void Persist(CoreAction action);

    /// <summary>The row to show for a stored action that matches no catalog row and is not a shortcut.</summary>
    protected abstract PickerChoice Fallback();

    /// <summary>Raised whenever the shown selection changes (also programmatically).</summary>
    protected virtual void SelectionApplied() { }

    /// <summary>The "Do Nothing" row, when the catalog has one.</summary>
    protected PickerChoice? NoneChoice => Choices.FirstOrDefault(c => c.Action.Kind == ActionKind.None);

    partial void OnSelectedChanged(PickerChoice value)
    {
        OnPropertyChanged(nameof(SelectedPickerItem));
        SelectionApplied();
        if (Suppress) return;
        Persist(value.Action);
    }

    /// <summary>
    /// Show <paramref name="action"/>: the matching catalog row, or the shortcut row
    /// holding its chord. Persists unless suppressed — also when the row is already
    /// selected and only its chord changed. Invalidates any recorder still open.
    /// </summary>
    public void Apply(CoreAction action)
    {
        _recordToken++;
        var item = Resolve(action);
        if (ReferenceEquals(item, Selected))
        {
            OnPropertyChanged(nameof(SelectedPickerItem));
            SelectionApplied();
            if (!Suppress) Persist(action);
        }
        else
        {
            Selected = item;
        }
    }

    /// <summary>Show <paramref name="action"/> without persisting — used to mirror an edit made elsewhere.</summary>
    public void SetSelectedSilently(CoreAction action)
    {
        Suppress = true;
        try { Apply(action); }
        finally { Suppress = false; }
    }

    /// <summary>Seed the initial selection (constructors only).</summary>
    protected void Seed(CoreAction current) => SetSelectedSilently(current);

    /// <summary>Change… — re-record the shortcut row's chord (clearing it keeps the row selected).</summary>
    [RelayCommand]
    private Task ChangeShortcutAsync() => RecordAsync(ShortcutRow);

    private async Task RecordAsync(ShortcutChoice row)
    {
        if (_recorder is null) return;
        var token = ++_recordToken;
        KeyCombo? result;
        try { result = await _recorder(row.Combo); }
        catch { result = null; }
        // Cancelled, or the editor moved on (another pick, a mirror, a device reload) meanwhile.
        if (result is null || token != _recordToken) return;
        row.Combo = result;
        if (ReferenceEquals(Selected, row))
        {
            OnPropertyChanged(nameof(SelectedPickerItem));
            SelectionApplied();
            Persist(row.Action);
        }
        else
        {
            Selected = row;
        }
    }

    private PickerChoice Resolve(CoreAction action)
    {
        if (action.Kind == ActionKind.CustomShortcut)
        {
            ShortcutRow.Combo = action.Combo ?? KeyCombo.Empty;
            return ShortcutRow;
        }
        return Choices.FirstOrDefault(c => c.Action.Equals(action)) ?? Fallback();
    }

    /// <summary>
    /// Resolve a picker item into the row to select. Choices land on themselves; a
    /// group header keeps going the way the selection was already travelling (down the
    /// list if the header sits below <paramref name="from"/>, up if above) and lands on
    /// the first action beyond it, so headers can't be selected and can't block the
    /// wheel at a category boundary. An unassigned shortcut row is skipped the same way
    /// (stepping across the Keyboard header must not open a dialog). Sticks on
    /// <paramref name="from"/> when there is nothing beyond — the top and bottom of the list.
    /// </summary>
    internal PickerChoice Land(IActionPickerItem item, PickerChoice from)
    {
        if (item is PickerChoice choice) return choice;
        var target = IndexOf(item);
        if (target < 0) return from;
        var step = target >= IndexOf(from) ? 1 : -1;
        for (var i = target; i >= 0 && i < GroupedChoices.Count; i += step)
            if (GroupedChoices[i] is PickerChoice next && next is not ShortcutChoice { IsAssigned: false })
                return next;
        return from;
    }

    private int IndexOf(IActionPickerItem item)
    {
        for (var i = 0; i < GroupedChoices.Count; i++)
            if (Equals(GroupedChoices[i], item)) return i;
        return -1;
    }

    /// <summary>
    /// <paramref name="choices"/> interleaved with <see cref="ActionGroupHeader"/> rows, grouped by
    /// <see cref="CoreAction.Category"/> in <see cref="CategoryExtensions.PickerOrder"/>. "Do Nothing"
    /// is pulled out of its group and appended as the very last row, so the unbind option is
    /// always in the same place. Groups with no catalog action (Keyboard) get no header here.
    /// </summary>
    internal static List<IActionPickerItem> BuildGrouped(IReadOnlyList<ActionChoice> choices)
    {
        var items = new List<IActionPickerItem>();
        foreach (var category in CategoryExtensions.PickerOrder)
        {
            var group = choices.Where(c => c.Action.Category() == category && c.Action.Kind != ActionKind.None).ToList();
            if (group.Count == 0) continue;
            items.Add(new ActionGroupHeader(category));
            items.AddRange(group);
        }
        items.AddRange(choices.Where(c => c.Action.Kind == ActionKind.None));
        return items;
    }

    /// <summary>
    /// The grouped list with a Keyboard header + <paramref name="row"/> spliced in at the
    /// Keyboard group's <see cref="CategoryExtensions.PickerOrder"/> position (before the
    /// first header of a later group, else before the trailing "Do Nothing" row). The other
    /// rows are the same instances as the shared list, so items taken from either match.
    /// </summary>
    private static List<IActionPickerItem> WithShortcutRow(IReadOnlyList<IActionPickerItem> grouped, ShortcutChoice row)
    {
        var order = CategoryExtensions.PickerOrder;
        var keyboardRank = Array.IndexOf(order, Category.Keyboard);
        var insertAt = grouped.Count;
        for (var i = 0; i < grouped.Count; i++)
        {
            if (grouped[i] is ActionGroupHeader h && Array.IndexOf(order, h.Category) > keyboardRank
                || grouped[i] is ActionChoice { Action.Kind: ActionKind.None })
            {
                insertAt = i;
                break;
            }
        }
        var items = new List<IActionPickerItem>(grouped.Count + 2);
        items.AddRange(grouped.Take(insertAt));
        items.Add(new ActionGroupHeader(Category.Keyboard));
        items.Add(row);
        items.AddRange(grouped.Skip(insertAt));
        return items;
    }
}

/// <summary>
/// One direction of the gesture button's five-way map (↑ ↓ ← → and centre Click),
/// with its own action picker. Selecting an action invokes the persist callback,
/// which writes that single direction to the device's gesture binding.
/// </summary>
public sealed partial class GestureDirectionBindingViewModel : ActionPickerEditor
{
    private readonly System.Action<GestureDirection, CoreAction> _persist;

    public GestureDirection Direction { get; }
    public string Label => Direction.Label();
    public string Glyph { get; }

    public GestureDirectionBindingViewModel(
        GestureDirection direction, CoreAction current, IReadOnlyList<ActionChoice> choices,
        System.Action<GestureDirection, CoreAction> persist, ShortcutRecorder? recorder = null)
        : base(choices, recorder)
    {
        Direction = direction;
        Glyph = direction.Glyph();
        _persist = persist;
        Seed(current);
        Loc.Current.WeakSubscribe(this, static (self, e) =>
        {
            if (e.PropertyName is nameof(Loc.Culture) or "Item[]")
                self.OnPropertyChanged(nameof(Label));
        });
    }

    protected override void Persist(CoreAction action) => _persist(Direction, action);

    /// <summary>An unknown stored action shows as "Do Nothing" — never as some other action.</summary>
    protected override PickerChoice Fallback() => NoneChoice ?? Choices[0];
}

/// <summary>
/// One rebindable button with its action picker. Selecting an action invokes the
/// persist callback (which writes to config). The functional core of the
/// original's per-button action picker (sans the visual hotspot overlay).
/// </summary>
public sealed partial class ButtonBindingViewModel : ActionPickerEditor
{
    private readonly System.Action<ButtonId, CoreAction> _persist;

    public ButtonId Button { get; }
    public string Label => Button.Label();

    /// <summary>
    /// The five-direction picker set when this is the gesture button, else <c>null</c>.
    /// When set, the flyout shows a per-direction editor instead of the single picker.
    /// </summary>
    public IReadOnlyList<GestureDirectionBindingViewModel>? Directions { get; }

    /// <summary>Whether this button edits a gesture map (five directions) rather than one action.</summary>
    public bool IsGesture => Directions is not null;

    /// <summary>The one-line summary shown on the diagram label: the bound action (a shortcut shows its chord), or "Gestures".</summary>
    public string SummaryLabel => IsGesture ? Loc.Current["Binding_Gestures"] : Selected.Action.Label();

    /// <summary>
    /// The diagram label's "Gestures: …" third line — set while this button is the
    /// device's gesture owner (category name, or action names within budget), else
    /// <c>null</c> and the line is hidden.
    /// </summary>
    [ObservableProperty]
    private string? _gestureSummary;

    public ButtonBindingViewModel(
        ButtonId button, CoreAction current, IReadOnlyList<ActionChoice> choices,
        System.Action<ButtonId, CoreAction> persist, ShortcutRecorder? recorder = null)
        : base(choices, recorder)
    {
        Button = button;
        _persist = persist;
        Seed(current);
        Loc.Current.WeakSubscribe(this, static (self, e) => self.OnCultureChanged(null, e));
    }

    /// <summary>Construct the gesture-button binding: a five-direction editor, no single action.</summary>
    public ButtonBindingViewModel(
        ButtonId button, IReadOnlyList<GestureDirectionBindingViewModel> directions, IReadOnlyList<ActionChoice> choices)
        : base(choices, recorder: null)
    {
        Button = button;
        Directions = directions;
        _persist = static (_, _) => { };
        // The single picker is unused for gestures; the base seeded a non-null placeholder.
        Loc.Current.WeakSubscribe(this, static (self, e) => self.OnCultureChanged(null, e));
    }

    protected override void Persist(CoreAction action) => _persist(Button, action);

    /// <summary>An unknown stored action shows as this button's own default — never "Left Click".</summary>
    protected override PickerChoice Fallback()
    {
        var fallback = Bindings.DefaultBinding(Button);
        return Choices.FirstOrDefault(c => c.Action.Equals(fallback)) ?? NoneChoice ?? Choices[0];
    }

    protected override void SelectionApplied() => OnPropertyChanged(nameof(SummaryLabel));

    /// <summary>The catalog of pickable actions, shared across buttons.</summary>
    public static IReadOnlyList<ActionChoice> Catalog { get; } =
        [.. CoreAction.Catalog().Select(a => new ActionChoice(a))];

    /// <summary>
    /// The catalog interleaved with <see cref="ActionGroupHeader"/> rows, grouped by
    /// <see cref="CoreAction.Category"/> in <see cref="CategoryExtensions.PickerOrder"/>.
    /// Contains the same <see cref="ActionChoice"/> instances as <see cref="Catalog"/>,
    /// and every editor's <see cref="ActionPickerEditor.GroupedChoices"/> reuses these
    /// instances around its own shortcut row.
    /// </summary>
    public static IReadOnlyList<IActionPickerItem> GroupedCatalog { get; } = BuildGrouped(Catalog);

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(SummaryLabel));
        OnPropertyChanged(nameof(GestureSummary));
    }

    private void OnCultureChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Loc.Culture) or "Item[]")
            RefreshLocalizedText();
    }
}
