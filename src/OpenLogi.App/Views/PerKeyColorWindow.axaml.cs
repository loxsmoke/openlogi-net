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
        // Tunnel so we catch every key before any child input consumes it.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
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
        e.Handled = true; // don't let the painted key type into the picker
        await _vm.PressAsync(e.PhysicalKey);
    }

    private async void OnResetAll(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.ResetAllAsync();
    }
}
