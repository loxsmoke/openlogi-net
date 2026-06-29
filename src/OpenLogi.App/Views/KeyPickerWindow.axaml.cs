using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OpenLogi.App.ViewModels;

namespace OpenLogi.App.Views;

public partial class KeyPickerWindow : Window
{
    public bool Confirmed { get; private set; }

    public KeyPickerWindow() => InitializeComponent();

    public KeyPickerWindow(byte usage, byte modifier) : this() =>
        DataContext = new KeyPickerViewModel(usage, modifier);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>The chosen (usage, modifier) after the dialog closes with OK.</summary>
    public (byte Usage, byte Modifier) Result =>
        DataContext is KeyPickerViewModel vm ? (vm.ResultUsage, vm.ResultModifier) : ((byte)0, (byte)0);

    private void OnOk(object? sender, RoutedEventArgs e) { Confirmed = true; Close(); }
    private void OnCancel(object? sender, RoutedEventArgs e) { Confirmed = false; Close(); }
}
