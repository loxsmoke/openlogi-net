using OpenLogi.Core;
using OpenLogi.Input;

namespace OpenLogi.Agent;

/// <summary>
/// Pure decision for what the OS hook should do with an OS-hook button event
/// (Middle/Back/Forward), given the effective single-action binding map. The
/// dispatch core of the agent's hook runtime, kept pure so it is unit-testable.
/// </summary>
public static class ButtonDispatch
{
    /// <summary>
    /// Resolve a button event. Returns the disposition and, on a remapped press,
    /// the action to inject. A binding that is the button's own native click
    /// passes through (so the physical click still works and we don't re-inject).
    /// </summary>
    public static (EventDisposition Disposition, Action? Inject) Resolve(
        ButtonId button, bool pressed, IReadOnlyDictionary<ButtonId, Action> bindings)
    {
        // Only Middle/Back/Forward are visible to / remapped by the OS hook.
        if (!button.IsOsHookButton())
            return (EventDisposition.PassThrough, null);

        if (!bindings.TryGetValue(button, out var action))
            return (EventDisposition.PassThrough, null);

        if (IsNativeIdentity(button, action))
            return (EventDisposition.PassThrough, null);

        // Remapped: swallow both edges; fire the action on press only.
        return (EventDisposition.Suppress, pressed ? action : null);
    }

    /// <summary>Whether <paramref name="action"/> is just the button's own native mouse click.</summary>
    private static bool IsNativeIdentity(ButtonId button, Action action) => (button, action.Kind) switch
    {
        (ButtonId.MiddleClick, ActionKind.MiddleClick) => true,
        (ButtonId.Back, ActionKind.MouseBack) => true,
        (ButtonId.Forward, ActionKind.MouseForward) => true,
        _ => false,
    };
}
