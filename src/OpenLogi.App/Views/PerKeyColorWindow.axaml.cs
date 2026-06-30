using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OpenLogi.App.ViewModels;

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

    public PerKeyColorWindow(PerKeyColorViewModel vm) : this()
    {
        _vm = vm;
        DataContext = _vm;
        Opened += async (_, _) => await _vm.InitAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        // Let a focused text field (the color picker's hex input) receive keys
        // normally instead of treating them as keys to paint.
        if (FocusManager?.GetFocusedElement() is TextBox) return;
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

    // Swallow key releases so Space/Enter can never activate a focused button here,
    // except while typing into a text field (the color picker's hex input).
    private void OnKeyUpTunnel(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        e.Handled = true;
    }

    private async void OnResetAll(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.ResetAllAsync();
    }
}
