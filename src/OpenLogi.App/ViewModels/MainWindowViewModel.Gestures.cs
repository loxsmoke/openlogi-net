using CommunityToolkit.Mvvm.Input;
using OpenLogi.Agent;
using OpenLogi.Core.Actions;
using OpenLogi.Core.Config;
using OpenLogi.Core.Gestures;
using OpenLogi.Hid;

namespace OpenLogi.App.ViewModels;

// The Gestures panel: owner selection, category presets, the five-direction
// editors, undo, and the diagram's gesture summaries/highlight.
public partial class MainWindowViewModel
{
    /// <summary>
    /// Build the gesture button's five-direction editor. Seeds each direction from
    /// the device's stored gesture map (with built-in defaults filled in) so the
    /// picker always shows the full ↑ ↓ ← → + Click set.
    /// </summary>
    private ButtonBindingViewModel BuildGestureBinding(string configKey)
    {
        var directions = BuildDirectionEditors(configKey, ButtonId.GestureButton);
        return new ButtonBindingViewModel(ButtonId.GestureButton, directions, ButtonBindingViewModel.Catalog);
    }

    /// <summary>
    /// One <see cref="GestureDirectionBindingViewModel"/> per direction for
    /// <paramref name="owner"/>'s editor, persisting to that button. Seeds: the
    /// button's stored gesture map; for an unconfigured button, Click shows its
    /// current effective action and the swipes show Do Nothing (nothing is active
    /// until the user actually configures something). The dedicated gesture button
    /// falls back to the built-in defaults instead — it gestures out of the box.
    /// </summary>
    private List<GestureDirectionBindingViewModel> BuildDirectionEditors(string configKey, ButtonId owner)
    {
        var stored = _config.GestureBindingsFor(configKey, owner);
        var clickSeed = BindingMaps.BindingsFor(_config, configKey, null).TryGetValue(owner, out var click)
            ? click
            : Bindings.DefaultBinding(owner);
        return GestureDirectionExtensions.All
            .Select(d =>
            {
                var action = stored.TryGetValue(d, out var ga) ? ga
                    : owner == ButtonId.GestureButton ? Bindings.DefaultGestureBinding(d)
                    : d == GestureDirection.Click ? clickSeed
                    : Core.Actions.MouseAction.None;
                return new GestureDirectionBindingViewModel(d, action, ButtonBindingViewModel.Catalog,
                    (dir, act) => PersistGesture(configKey, owner, dir, act));
            })
            .ToList();
    }

    private void PersistGesture(string configKey, ButtonId owner, GestureDirection direction, Core.Actions.MouseAction action)
    {
        // Writes one direction into the owner button's gesture map (upgrading it to a
        // gesture binding if needed). A button's FIRST gesture edit is what activates
        // it, so re-arm the captures to divert its control.
        var wasActive = _config.GestureButtons(configKey).Contains(owner);
        _config.SetGestureDirection(configKey, owner, direction, action);
        try { _config.SaveAtomic(); }
        catch { /* keep editing fluid */ }
        if (!wasActive)
            _ = RestartGestureCaptureForSelectedAsync();
        RefreshGestureSummaries(configKey);
        // Keep the button's diagram picker in agreement: the plain click shown there is
        // the same value as the gesture Click. (The synthetic gesture button edits its
        // click inside its own five-way flyout, so skip that IsGesture binding.)
        if (direction == GestureDirection.Click
            && _bindings.TryGetValue(owner, out var diagramBinding) && !diagramBinding.IsGesture)
            diagramBinding.SetSelectedSilently(action);
        if (!_suppressGesturePanel)
        {
            // One user edit (click or a single swipe) = one undo step.
            PushGestureUndo();
            // A hand-edited swipe leaves any preset it deviates from: reflect Custom
            // (or the preset it happens to complete) in the Gestures dropdown.
            if (direction != GestureDirection.Click)
            {
                _suppressGesturePanel = true;
                SelectedGestureCategory = MatchGestureCategory();
                _suppressGesturePanel = false;
            }
        }
    }

    /// <summary>Friendly label for a button offered as a gesture trigger.</summary>
    private static string GestureOwnerLabel(ButtonId button) => button switch
    {
        ButtonId.GestureButton => "Gesture button",
        ButtonId.DpiToggle => "Wheel / DPI button",
        _ => button.Label(),
    };

    /// <summary>
    /// Populate the Gestures section for the selected mouse: the owner dropdown (Off +
    /// the device's HID++-capturable gesture buttons) and the five-direction editor.
    /// Hidden when the device exposes no eligible gesture control.
    /// </summary>
    private async Task BuildGestureSectionAsync(DeviceSession session, DeviceViewModel device)
    {
        var eligible = await session.GestureCapableButtonsAsync();
        if (!ReferenceEquals(SelectedDevice, device)) return;
        if (device.ConfigKey is not { } ck || eligible.Count == 0)
        {
            ShowGestures = false;
            return;
        }

        _suppressGestureOwner = true;
        _suppressGesturePanel = true;
        GestureOwnerChoices.Clear();
        foreach (var b in eligible)
            GestureOwnerChoices.Add(new GestureOwnerChoice(b, GestureOwnerLabel(b)));

        GesturesEnabled = _config.GesturesEnabled(ck);
        var owner = _config.GestureOwner(ck);
        SelectedGestureOwner = GesturesEnabled && owner is { } o && eligible.Contains(o)
            ? GestureOwnerChoices.First(c => c.Button == o)
            : null;
        _suppressGesturePanel = false;
        RebuildGestureDirections(ck);
        ShowGestures = true;
        _suppressGestureOwner = false;
        RefreshGestureHighlight();
    }

    private void RebuildGestureDirections(string configKey)
    {
        _suppressGesturePanel = true;
        GestureDirections.Clear();
        // A (re)built editor edits a different button (or none): undo starts fresh.
        _gestureUndo.Clear();
        _lastGestureState = null;
        if (SelectedGestureOwner?.Button is not { } owner)
        {
            // No button selected — no click editor, no category, no swipes.
            GestureClick = null;
            GestureOwnerSelected = false;
            SelectedGestureCategory = null;
            _suppressGesturePanel = false;
            return;
        }
        GestureOwnerSelected = true;
        var editors = BuildDirectionEditors(configKey, owner);
        // Click (the plain tap) is its own row under the owner dropdown; the four
        // swipes live in the list below the Gestures (category) dropdown.
        GestureClick = editors.First(v => v.Direction == GestureDirection.Click);
        foreach (var vm in editors.Where(v => v.Direction != GestureDirection.Click))
            GestureDirections.Add(vm);
        SelectedGestureCategory = MatchGestureCategory();
        _suppressGesturePanel = false;
        _lastGestureState = CurrentGestureSnapshot();
    }

    /// <summary>The five current editor actions (click first, then ↑↓←→), or null with no editor.</summary>
    private (ButtonId Owner, Core.Actions.MouseAction[] Actions)? CurrentGestureSnapshot()
    {
        if (SelectedGestureOwner?.Button is not { } owner || GestureClick is null) return null;
        var actions = new List<Core.Actions.MouseAction> { GestureClick.Selected.Action };
        actions.AddRange(GestureDirections.Select(v => v.Selected.Action));
        return (owner, [.. actions]);
    }

    /// <summary>Record the pre-edit state as one undo step (no-op during programmatic fills).</summary>
    private void PushGestureUndo()
    {
        if (_lastGestureState is { } previous)
            _gestureUndo.Push(previous);
        _lastGestureState = CurrentGestureSnapshot();
    }

    /// <summary>
    /// Ctrl+Z in the Gestures panel: restore the five editor actions to the state
    /// before the most recent click / category / swipe edit. History is cleared
    /// whenever a different button is selected.
    /// </summary>
    [RelayCommand]
    private void UndoGestureEdit()
    {
        if (_gestureUndo.Count == 0) return;
        var (owner, actions) = _gestureUndo.Pop();
        if (SelectedGestureOwner?.Button != owner || GestureClick is null)
        {
            _gestureUndo.Clear(); // stale entries for a previously edited button
            return;
        }
        _suppressGesturePanel = true;
        var editors = new List<GestureDirectionBindingViewModel> { GestureClick };
        editors.AddRange(GestureDirections);
        for (var i = 0; i < editors.Count && i < actions.Length; i++)
        {
            var choice = ButtonBindingViewModel.Catalog.FirstOrDefault(c => c.Action.Equals(actions[i]));
            if (choice is not null)
                editors[i].Selected = choice; // persists via the editor's own callback
        }
        SelectedGestureCategory = MatchGestureCategory();
        _suppressGesturePanel = false;
        _lastGestureState = CurrentGestureSnapshot();
    }

    partial void OnSelectedGestureOwnerChanged(GestureOwnerChoice? value)
    {
        RefreshGestureHighlight();
        if (_suppressGestureOwner || SelectedDevice?.ConfigKey is not { } ck) return;
        // Selecting a button only retargets the editor — it must not create or
        // clear any button's gesture map. Global on/off is the checkbox's job.
        if (value?.Button is { } button)
        {
            _config.SetGestureSelection(ck, button);
            try { _config.SaveAtomic(); } catch { /* keep editing fluid */ }
        }
        RebuildGestureDirections(ck);
        RefreshGestureSummaries(ck);
    }

    partial void OnGesturesEnabledChanged(bool value)
    {
        if (_suppressGesturePanel || SelectedDevice?.ConfigKey is not { } ck) return;
        if (value)
        {
            // Globally back on: every configured button's map comes back to life.
            _config.EnableGestures(ck);
        }
        else
        {
            // Globally off: silence every button, keep all maps, and clear the
            // editing selection so the per-button rows collapse.
            _config.DisableGestures(ck);
            _suppressGestureOwner = true;
            SelectedGestureOwner = null;
            _suppressGestureOwner = false;
            RebuildGestureDirections(ck);
            RefreshGestureHighlight();
        }
        try { _config.SaveAtomic(); } catch { /* keep editing fluid */ }
        RefreshGestureSummaries(ck);
        _ = RestartGestureCaptureForSelectedAsync(); // arm or release every gesture divert
    }

    partial void OnSelectedGestureCategoryChanged(GesturePreset? value)
    {
        if (_suppressGesturePanel || value is null || value.IsCustom) return;
        // Applies Disabled (all swipes → Do Nothing) exactly like any other preset.
        // The whole fill is one undo step.
        ApplyGesturePreset(value);
        PushGestureUndo();
    }

    /// <summary>
    /// Fill the four swipe dropdowns from <paramref name="preset"/> (persisting via
    /// each editor's own path) and, unless <paramref name="updateCategory"/> is off,
    /// reflect the preset in the Category dropdown.
    /// </summary>
    private void ApplyGesturePreset(GesturePreset preset, bool updateCategory = true)
    {
        _suppressGesturePanel = true;
        foreach (var vm in GestureDirections)
        {
            if (preset.For(vm.Direction) is not { } action) continue;
            var choice = ButtonBindingViewModel.Catalog.FirstOrDefault(c => c.Action.Equals(action));
            if (choice is not null)
                vm.Selected = choice; // fires the editor's persist callback
        }
        if (updateCategory)
            SelectedGestureCategory = preset;
        _suppressGesturePanel = false;
    }

    /// <summary>The preset the current four swipe selections equal, else Custom.</summary>
    private GesturePreset MatchGestureCategory() =>
        GestureCategories.FirstOrDefault(p =>
            !p.IsCustom && GestureDirections.All(vm => vm.Selected.Action.Equals(p.For(vm.Direction))))
        ?? CustomGesturePreset;

    /// <summary>Accent the gesture owner's label + leader line on the mouse diagram.</summary>
    private void RefreshGestureHighlight()
    {
        var owner = SelectedGestureOwner?.Button;
        foreach (var a in Annotations)
            a.Highlighted = owner is { } o && a.Binding.Button == o;
    }

    /// <summary>
    /// Select <paramref name="button"/> in the Gestures panel (clicking its diagram
    /// label). A no-op for buttons that can't gesture — selection alone never
    /// creates or clears a gesture map, so this is always safe.
    /// </summary>
    public void SelectGestureOwnerFor(ButtonId button)
    {
        var choice = GestureOwnerChoices.FirstOrDefault(c => c.Button == button);
        if (choice is not null)
            SelectedGestureOwner = choice;
    }

    /// <summary>Longest "Gestures: …" detail the diagram label column can carry.</summary>
    private const int GestureSummaryBudget = 20;

    /// <summary>
    /// The "Gestures: …" line for <paramref name="button"/>'s diagram label, or
    /// <c>null</c> when it is not the gesture owner. The detail is the shared
    /// category name when all swipe actions belong to one category, else as many
    /// action names as fit <see cref="GestureSummaryBudget"/> characters.
    /// </summary>
    private string? GestureSummaryText(string configKey, ButtonId button)
    {
        if (!_config.GestureButtons(configKey).Contains(button)) return null;
        var map = BindingMaps.GestureBindingsFor(_config, configKey, button);
        var actions = new[] { GestureDirection.Up, GestureDirection.Down, GestureDirection.Left, GestureDirection.Right }
            .Select(d => map.TryGetValue(d, out var a) ? a : Bindings.DefaultGestureBinding(d))
            .Where(a => a.Kind != ActionKind.None)
            .ToList();
        if (actions.Count == 0) return null; // swipes disabled — no label line

        var categories = actions.Select(a => a.Category()).Distinct().ToList();
        if (categories.Count == 1)
            return $"Gestures: {categories[0].Label()}";

        var detail = "";
        foreach (var label in actions.Select(a => a.Label()).Distinct())
        {
            var candidate = detail.Length == 0 ? label : $"{detail}, {label}";
            if (candidate.Length > GestureSummaryBudget)
            {
                detail += "…";
                break;
            }
            detail = candidate;
        }
        return $"Gestures: {detail}";
    }

    /// <summary>Recompute every button's "Gestures: …" label line (owner or map changed).</summary>
    private void RefreshGestureSummaries(string configKey)
    {
        foreach (var (button, vm) in _bindings)
            vm.GestureSummary = GestureSummaryText(configKey, button);
    }
}
