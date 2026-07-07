using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using OpenLogi.App.ViewModels;
using OpenLogi.Core.Config;

namespace OpenLogi.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    public SettingsWindow(Config config) : this() => DataContext = new SettingsViewModel(config);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Escape closes the settings window (settings apply live, so there's nothing to discard).
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
