using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OpenLogi.App.ViewModels;
using OpenLogi.Hid;

namespace OpenLogi.App.Views;

public partial class PerKeyColorWindow : Window
{
    private PerKeyColorViewModel? _vm;

    public PerKeyColorWindow()
    {
        InitializeComponent();
        // Tunnel so we catch every key before any child input consumes it. We also
        // swallow KeyUp: buttons fire their Click on Space/Enter release, which would
        // otherwise let painting the spacebar accidentally trigger a focused button.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUpTunnel, RoutingStrategies.Tunnel);
    }

    public PerKeyColorWindow(DeviceSession session) : this()
    {
        _vm = new PerKeyColorViewModel(session);
        DataContext = _vm;
        Opened += async (_, _) => await _vm.InitAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        e.Handled = true; // don't let the painted key type into the picker or hit a button
        // Alt+R resets every key to the base color (so the spacebar can be painted
        // without accidentally triggering the on-screen reset button).
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.PhysicalKey == PhysicalKey.R)
        {
            await _vm.ResetAllAsync();
            return;
        }
        await _vm.PressAsync(e.PhysicalKey);
    }

    // Swallow key releases so Space/Enter can never activate a focused button here.
    private void OnKeyUpTunnel(object? sender, KeyEventArgs e) => e.Handled = true;

    private async void OnResetAll(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.ResetAllAsync();
    }
}
