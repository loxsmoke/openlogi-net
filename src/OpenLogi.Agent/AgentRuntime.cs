using OpenLogi.Core;
using OpenLogi.Input;

namespace OpenLogi.Agent;

/// <summary>
/// Wires the OS mouse hook to the binding configuration: resolves each OS-hook
/// button press against the effective bindings (with the foreground app's
/// per-app overlay) and injects the bound action. The in-process replacement for
/// the Rust agent's hook runtime (no IPC on Windows v1).
///
/// HARDWARE-UNVERIFIED: the live remap path needs an interactive session + mouse.
/// <see cref="ButtonDispatch"/> and <see cref="BindingMaps"/> are pure + tested.
/// </summary>
public sealed class AgentRuntime : IDisposable
{
    private readonly Config _config;
    private readonly object _gate = new();
    private string? _selectedConfigKey;
    private MouseHook? _hook;

    public AgentRuntime(Config config) => _config = config;

    /// <summary>The device whose bindings are active (the carousel selection).</summary>
    public void SetSelectedDevice(string? configKey)
    {
        lock (_gate) _selectedConfigKey = configKey;
    }

    /// <summary>Install the hook and begin dispatching remapped buttons.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _hook ??= MouseHook.Start(OnMouseEvent);
        }
    }

    private EventDisposition OnMouseEvent(MouseEvent ev)
    {
        if (ev is not MouseEvent.Button button)
            return EventDisposition.PassThrough; // scroll / move / interrupt pass through

        string? configKey;
        lock (_gate) configKey = _selectedConfigKey;

        var appBundle = MouseHook.FrontmostProcessPath();
        var bindings = BindingMaps.BindingsFor(_config, configKey, appBundle);
        var (disposition, inject) = ButtonDispatch.Resolve(button.Id, button.Pressed, bindings);
        if (inject is { } action)
            ActionInjector.Execute(action);
        return disposition;
    }

    /// <summary>
    /// Fire the action bound to a HID++-diverted button (e.g. the DPI/ModeShift
    /// button) on the device identified by <paramref name="configKey"/>, which the
    /// OS mouse hook can't see. Because the event already carries the source device,
    /// this is per-mouse (unlike the global OS hook). Already diverted at the device,
    /// so there is no native click to suppress.
    /// </summary>
    public void DispatchDivertedButton(string? configKey, ButtonId button)
    {
        var appBundle = MouseHook.FrontmostProcessPath();
        var bindings = BindingMaps.BindingsFor(_config, configKey, appBundle);
        if (bindings.TryGetValue(button, out var action))
            ActionInjector.Execute(action);
    }

    /// <summary>
    /// Fire the action bound to a committed gesture <paramref name="direction"/> on
    /// <paramref name="button"/> of the device identified by <paramref name="configKey"/>.
    /// Resolves that button's per-direction gesture map (empty unless it has gestures
    /// configured, so a stray call is a no-op) and injects the bound action. Like the
    /// diverted DPI button, the gesture is already captured at the device, so there
    /// is no native input to suppress.
    /// </summary>
    public void DispatchGesture(string? configKey, ButtonId button, GestureDirection direction)
    {
        var bindings = BindingMaps.GestureBindingsFor(_config, configKey, button);
        if (bindings.TryGetValue(direction, out var action) && action.Kind != ActionKind.None)
            ActionInjector.Execute(action);
    }

    /// <summary>
    /// Re-inject diverted hi-res wheel motion as OS scrolling. <paramref name="wheelData"/>
    /// is raw Windows wheel data (±120 = one physical notch; smaller values are the
    /// smooth sub-notch steps). The wheel is already diverted at the device, so
    /// there is no native scroll to suppress.
    /// </summary>
    public void DispatchSmoothScroll(int wheelData) => ActionInjector.PostVerticalScroll(wheelData);

    public void Dispose()
    {
        lock (_gate)
        {
            _hook?.Dispose();
            _hook = null;
        }
    }
}
