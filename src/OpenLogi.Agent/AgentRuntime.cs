using System.Threading.Channels;
using OpenLogi.Core.Actions;
using OpenLogi.Core.Config;
using OpenLogi.Core.Gestures;
using OpenLogi.Core.Logging;
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

    // Actions are injected from a single background consumer so a slow SendInput or
    // foreground-app lookup can never stall the callers — the HID++ event pumps
    // (a stalled pump queues gesture events and replays them, stale, in a burst
    // later) or the low-level mouse hook (which Windows removes if it blocks too
    // long). Bounded + DropOldest: if injection wedges anyway, stale gestures are
    // discarded rather than replayed.
    private readonly Channel<System.Action> _injections = Channel.CreateBounded<System.Action>(
        new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

    public AgentRuntime(Config config)
    {
        _config = config;
        _ = InjectionPumpAsync();
    }

    private async Task InjectionPumpAsync()
    {
        await foreach (var work in _injections.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { work(); }
            catch (Exception ex) { DiagnosticLog.Warn("agent", $"action injection failed: {ex.Message}"); }
        }
    }

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
        // The disposition must be decided synchronously (it suppresses the native
        // click), but the injection itself must not run inside the hook callback.
        if (inject is { } action)
            _injections.Writer.TryWrite(() => ActionInjector.Execute(action));
        return disposition;
    }

    /// <summary>
    /// Fire the action bound to a HID++-diverted button (e.g. the DPI/ModeShift
    /// button) on the device identified by <paramref name="configKey"/>, which the
    /// OS mouse hook can't see. Because the event already carries the source device,
    /// this is per-mouse (unlike the global OS hook). Already diverted at the device,
    /// so there is no native click to suppress.
    /// </summary>
    public void DispatchDivertedButton(string? configKey, ButtonId button) =>
        _injections.Writer.TryWrite(() =>
        {
            var appBundle = MouseHook.FrontmostProcessPath();
            var bindings = BindingMaps.BindingsFor(_config, configKey, appBundle);
            if (bindings.TryGetValue(button, out var action))
                ActionInjector.Execute(action);
        });

    /// <summary>
    /// Fire the action bound to a committed gesture <paramref name="direction"/> on
    /// <paramref name="button"/> of the device identified by <paramref name="configKey"/>.
    /// Resolves that button's per-direction gesture map (empty unless it has gestures
    /// configured, so a stray call is a no-op) and injects the bound action. Like the
    /// diverted DPI button, the gesture is already captured at the device, so there
    /// is no native input to suppress.
    /// </summary>
    public void DispatchGesture(string? configKey, ButtonId button, GestureDirection direction) =>
        _injections.Writer.TryWrite(() =>
        {
            var bindings = BindingMaps.GestureBindingsFor(_config, configKey, button);
            if (bindings.TryGetValue(direction, out var action) && action.Kind != ActionKind.None)
                ActionInjector.Execute(action);
        });

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
        _injections.Writer.TryComplete();
    }
}
