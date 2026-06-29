using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using OpenLogi.App.ViewModels;
using OpenLogi.Core;

namespace OpenLogi.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    public SettingsWindow(Config config) : this() => DataContext = new SettingsViewModel(config);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
