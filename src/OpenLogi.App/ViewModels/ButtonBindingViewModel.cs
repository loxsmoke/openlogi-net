using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenLogi.Core.Actions;
using OpenLogi.Core.Config;
using OpenLogi.Core.Gestures;
using CoreAction = OpenLogi.Core.Actions.MouseAction;

namespace OpenLogi.App.ViewModels;

/// <summary>
/// A row in the grouped action picker. <see cref="Selectable"/> drives the item
/// container's IsEnabled, so group headers are skipped by mouse and keyboard.
/// </summary>
public interface IActionPickerItem
{
    bool Selectable { get; }
}

/// <summary>A selectable action in the picker dropdown (wraps an action with a label).</summary>
public sealed record ActionChoice(CoreAction Action) : IActionPickerItem
{
    public string Label => Action.Label();
    public bool Selectable => true;
}

/// <summary>A non-selectable group-header row (category name + horizontal rule).</summary>
public sealed record ActionGroupHeader(string Name) : IActionPickerItem
{
    public bool Selectable => false;
}

/// <summary>
/// A choice in the gesture-owner dropdown: which button drives gestures on the
/// device, or <c>null</c> for "All off". <see cref="Label"/> is the display text.
/// </summary>
public sealed record GestureOwnerChoice(ButtonId? Button, string Label);

/// <summary>
/// A named per-direction gesture set for the Category dropdown (mirrors the
/// Options+ gesture sets). The <c>Custom</c> sentinel (null actions) is selected
/// whenever the four swipes don't match any preset, and applies nothing.
/// </summary>
public sealed record GesturePreset(string Name, CoreAction? Up, CoreAction? Down, CoreAction? Left, CoreAction? Right)
{
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
/// One direction of the gesture button's five-way map (↑ ↓ ← → and centre Click),
/// with its own action picker. Selecting an action invokes the persist callback,
/// which writes that single direction to the device's gesture binding.
/// </summary>
public sealed partial class GestureDirectionBindingViewModel : ObservableObject
{
    private readonly System.Action<GestureDirection, CoreAction> _persist;
    private bool _suppress;

    public GestureDirection Direction { get; }
    public string Label { get; }
    public string Glyph { get; }
    public IReadOnlyList<ActionChoice> Choices { get; }

    /// <summary>Grouped items (headers + choices) for the dropdown.</summary>
    public IReadOnlyList<IActionPickerItem> GroupedChoices => ButtonBindingViewModel.GroupedCatalog;

    [ObservableProperty]
    private ActionChoice _selected;

    /// <summary>The picker's SelectedItem — see <see cref="ButtonBindingViewModel.SelectedPickerItem"/>.</summary>
    public IActionPickerItem SelectedPickerItem
    {
        get => Selected;
        set
        {
            Selected = ButtonBindingViewModel.Land(value, Selected);
            OnPropertyChanged();
        }
    }

    public GestureDirectionBindingViewModel(
        GestureDirection direction, CoreAction current, IReadOnlyList<ActionChoice> choices,
        System.Action<GestureDirection, CoreAction> persist)
    {
        Direction = direction;
        Label = direction.Label();
        Glyph = direction.Glyph();
        Choices = choices;
        _persist = persist;
        _suppress = true;
        _selected = choices.FirstOrDefault(c => c.Action.Equals(current)) ?? choices[0];
        _suppress = false;
    }

    partial void OnSelectedChanged(ActionChoice value)
    {
        OnPropertyChanged(nameof(SelectedPickerItem));
        if (_suppress) return;
        _persist(Direction, value.Action);
    }

    /// <summary>Set the shown action without persisting — used to mirror an edit made elsewhere.</summary>
    public void SetSelectedSilently(CoreAction action)
    {
        _suppress = true;
        Selected = Choices.FirstOrDefault(c => c.Action.Equals(action)) ?? Choices[0];
        _suppress = false;
    }
}

/// <summary>
/// One rebindable button with its action picker. Selecting an action invokes the
/// persist callback (which writes to config). The functional core of the
/// original's per-button action picker (sans the visual hotspot overlay).
/// </summary>
public sealed partial class ButtonBindingViewModel : ObservableObject
{
    private readonly System.Action<ButtonId, CoreAction> _persist;
    private bool _suppress;

    public ButtonId Button { get; }
    public string Label => Button.Label();
    public IReadOnlyList<ActionChoice> Choices { get; }

    /// <summary>
    /// The five-direction picker set when this is the gesture button, else <c>null</c>.
    /// When set, the flyout shows a per-direction editor instead of the single picker.
    /// </summary>
    public IReadOnlyList<GestureDirectionBindingViewModel>? Directions { get; }

    /// <summary>Whether this button edits a gesture map (five directions) rather than one action.</summary>
    public bool IsGesture => Directions is not null;

    /// <summary>The one-line summary shown on the diagram label: the bound action, or "Gestures".</summary>
    public string SummaryLabel => IsGesture ? "Gestures" : Selected.Label;

    [ObservableProperty]
    private ActionChoice _selected;

    /// <summary>
    /// The picker's SelectedItem. Widened to <see cref="IActionPickerItem"/> because the
    /// dropdown also holds group headers: <see cref="ActionChoice.Selectable"/> keeps a
    /// click off them, but wheel and arrow-key selection moves by index and steps onto
    /// them regardless, so a header does reach this setter. <see cref="Land"/> carries it
    /// on to the next action instead.
    /// </summary>
    public IActionPickerItem SelectedPickerItem
    {
        get => Selected;
        set
        {
            Selected = Land(value, Selected);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The diagram label's "Gestures: …" third line — set while this button is the
    /// device's gesture owner (category name, or action names within budget), else
    /// <c>null</c> and the line is hidden.
    /// </summary>
    [ObservableProperty]
    private string? _gestureSummary;

    public ButtonBindingViewModel(ButtonId button, CoreAction current, IReadOnlyList<ActionChoice> choices, System.Action<ButtonId, CoreAction> persist)
    {
        Button = button;
        Choices = choices;
        _persist = persist;
        _suppress = true;
        _selected = choices.FirstOrDefault(c => c.Action.Equals(current)) ?? choices[0];
        _suppress = false;
    }

    /// <summary>Construct the gesture-button binding: a five-direction editor, no single action.</summary>
    public ButtonBindingViewModel(
        ButtonId button, IReadOnlyList<GestureDirectionBindingViewModel> directions, IReadOnlyList<ActionChoice> choices)
    {
        Button = button;
        Choices = choices;
        Directions = directions;
        _persist = static (_, _) => { };
        _suppress = true;
        _selected = choices[0]; // unused for gestures; keeps the property non-null
        _suppress = false;
    }

    partial void OnSelectedChanged(ActionChoice value)
    {
        OnPropertyChanged(nameof(SelectedPickerItem));
        if (_suppress) return;
        OnPropertyChanged(nameof(SummaryLabel));
        _persist(Button, value.Action);
    }

    /// <summary>Set the shown action without persisting — used to mirror an edit made elsewhere.</summary>
    public void SetSelectedSilently(CoreAction action)
    {
        _suppress = true;
        Selected = Choices.FirstOrDefault(c => c.Action.Equals(action)) ?? Choices[0];
        _suppress = false;
        OnPropertyChanged(nameof(SummaryLabel));
    }

    /// <summary>The catalog of pickable actions, shared across buttons.</summary>
    public static IReadOnlyList<ActionChoice> Catalog { get; } =
        [.. CoreAction.Catalog().Select(a => new ActionChoice(a))];

    /// <summary>
    /// The catalog interleaved with <see cref="ActionGroupHeader"/> rows, grouped by
    /// <see cref="CoreAction.Category"/> in <see cref="CategoryExtensions.PickerOrder"/>.
    /// Contains the same <see cref="ActionChoice"/> instances as <see cref="Catalog"/>,
    /// so a SelectedItem bound to a catalog entry matches by reference.
    /// </summary>
    public static IReadOnlyList<IActionPickerItem> GroupedCatalog { get; } = BuildGroupedCatalog();

    /// <summary>Grouped items for this button's picker (headers + choices).</summary>
    public IReadOnlyList<IActionPickerItem> GroupedChoices => GroupedCatalog;

    /// <summary>
    /// Resolve a picker item into the action to select. Choices land on themselves; a
    /// group header keeps going the way the selection was already travelling (down the
    /// list if the header sits below <paramref name="from"/>, up if above) and lands on
    /// the first action beyond it, so headers can't be selected and can't block the
    /// wheel at a category boundary. Sticks on <paramref name="from"/> when there is
    /// nothing beyond the header — the top and bottom of the list.
    /// </summary>
    internal static ActionChoice Land(IActionPickerItem item, ActionChoice from)
    {
        if (item is ActionChoice choice) return choice;
        var target = IndexOf(item);
        if (target < 0) return from;
        var step = target >= IndexOf(from) ? 1 : -1;
        for (var i = target; i >= 0 && i < GroupedCatalog.Count; i += step)
            if (GroupedCatalog[i] is ActionChoice next) return next;
        return from;
    }

    private static int IndexOf(IActionPickerItem item)
    {
        for (var i = 0; i < GroupedCatalog.Count; i++)
            if (Equals(GroupedCatalog[i], item)) return i;
        return -1;
    }

    private static List<IActionPickerItem> BuildGroupedCatalog()
    {
        var items = new List<IActionPickerItem>();
        foreach (var category in CategoryExtensions.PickerOrder)
        {
            // "Do Nothing" is pulled out of its group and appended as the very
            // last row, so the unbind option is always in the same place.
            var group = Catalog.Where(c => c.Action.Category() == category && c.Action.Kind != ActionKind.None).ToList();
            if (group.Count == 0) continue;
            items.Add(new ActionGroupHeader(category.Label()));
            items.AddRange(group);
        }
        items.AddRange(Catalog.Where(c => c.Action.Kind == ActionKind.None));
        return items;
    }
}
