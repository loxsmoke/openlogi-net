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

    public void Dispose()
    {
        lock (_gate)
        {
            _hook?.Dispose();
            _hook = null;
        }
    }
}
