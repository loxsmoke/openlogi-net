using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenLogi.Core;
using CoreAction = OpenLogi.Core.Action;

namespace OpenLogi.App.ViewModels;

/// <summary>A selectable action in the picker dropdown (wraps an action with a label).</summary>
public sealed record ActionChoice(CoreAction Action)
{
    public string Label => Action.Label();
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

    [ObservableProperty]
    private ActionChoice _selected;

    public ButtonBindingViewModel(ButtonId button, CoreAction current, IReadOnlyList<ActionChoice> choices, System.Action<ButtonId, CoreAction> persist)
    {
        Button = button;
        Choices = choices;
        _persist = persist;
        _suppress = true;
        _selected = choices.FirstOrDefault(c => c.Action.Equals(current)) ?? choices[0];
        _suppress = false;
    }

    partial void OnSelectedChanged(ActionChoice value)
    {
        if (_suppress) return;
        _persist(Button, value.Action);
    }

    /// <summary>The catalog of pickable actions, shared across buttons.</summary>
    public static IReadOnlyList<ActionChoice> Catalog { get; } =
        [.. CoreAction.Catalog().Select(a => new ActionChoice(a))];
}
