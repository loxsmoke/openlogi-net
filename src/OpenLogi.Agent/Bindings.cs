using OpenLogi.Core.Actions;
using OpenLogi.Core.Config;
using OpenLogi.Core.Gestures;

namespace OpenLogi.Agent;

/// <summary>
/// Binding-map construction: overlay stored per-device (and per-app) bindings on
/// the built-in defaults. Ported from Rust <c>openlogi-agent-core::bindings</c>.
/// Pure and fully testable.
/// </summary>
public static class BindingMaps
{
    /// <summary>
    /// Effective per-button single-action map for <paramref name="configKey"/> with
    /// <paramref name="appBundle"/>'s overlay applied. Unset buttons fall back to the
    /// default; a gesture binding is projected to its click action (its swipes are
    /// dispatched via <see cref="GestureBindingsFor"/> / <see cref="OsHookGesturesFor"/>).
    /// </summary>
    public static SortedDictionary<ButtonId, MouseAction> BindingsFor(Config config, string? configKey, string? appBundle)
    {
        var stored = configKey is not null
            ? config.EffectiveBindings(configKey, appBundle)
            : new SortedDictionary<ButtonId, Binding>();

        var bindings = new SortedDictionary<ButtonId, MouseAction>();
        foreach (var b in ButtonIdExtensions.All)
            bindings[b] = Bindings.DefaultBinding(b);

        foreach (var (k, binding) in stored)
        {
            // A gesture binding with no explicit Click has no opinion on the plain
            // press — keep the default rather than clobbering with Action.None.
            if (binding.IsGesture() && binding.DirectionAction(GestureDirection.Click) is null)
                continue;
            bindings[k] = binding.ClickAction();
        }
        return bindings;
    }

    /// <summary>
    /// Effective gesture bindings for one gesture-configured <paramref name="button"/>
    /// on <paramref name="configKey"/> — the dedicated gesture button or any physical
    /// button (Middle/Back/Forward, DPI/wheel) with a gesture map. Several buttons may
    /// gesture at once; empty when this button has none or gestures are globally off.
    /// The dedicated gesture button falls back to the built-in per-direction defaults;
    /// other buttons only do what was explicitly configured. On Windows every gesture
    /// button is captured over HID++ (the WH_MOUSE_LL hook has no per-hold move
    /// deltas); <see cref="OsHookGesturesFor"/> exists for the macOS/Linux hook model
    /// and is unused on Windows.
    /// </summary>
    public static SortedDictionary<GestureDirection, MouseAction> GestureBindingsFor(
        Config config, string? configKey, ButtonId button)
    {
        var bindings = new SortedDictionary<GestureDirection, MouseAction>();
        if (configKey is null || !config.GestureButtons(configKey).Contains(button))
            return bindings;

        var stored = config.GestureBindingsFor(configKey, button);
        foreach (var d in GestureDirectionExtensions.All)
            bindings[d] = stored.TryGetValue(d, out var a) ? a
                : button == ButtonId.GestureButton ? Bindings.DefaultGestureBinding(d)
                : MouseAction.None;
        return bindings;
    }

    /// <summary>
    /// Per-direction maps for an OS-hook gesture button (Middle/Back/Forward) that
    /// owns gestures on <paramref name="configKey"/>, with <paramref name="appBundle"/>'s
    /// overlay applied. Empty unless an OS-hook button is the gesture owner.
    /// </summary>
    public static SortedDictionary<ButtonId, SortedDictionary<GestureDirection, MouseAction>> OsHookGesturesFor(
        Config config, string? configKey, string? appBundle)
    {
        var empty = new SortedDictionary<ButtonId, SortedDictionary<GestureDirection, MouseAction>>();
        if (configKey is null) return empty;
        var owner = config.GestureOwner(configKey);
        if (owner is not { } ownerId || !ownerId.IsOsHookButton()) return empty;

        var effective = config.EffectiveBindings(configKey, appBundle);
        if (effective.TryGetValue(ownerId, out var binding) && binding is Binding.Gesture g)
            return new SortedDictionary<ButtonId, SortedDictionary<GestureDirection, MouseAction>> { [ownerId] = g.Map };
        return empty;
    }
}
