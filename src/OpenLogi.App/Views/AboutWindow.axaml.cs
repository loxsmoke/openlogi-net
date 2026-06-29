using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OpenLogi.Core;

namespace OpenLogi.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1";
        this.FindControl<TextBlock>("VersionText")!.Text = $"v{version}";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnGitHub(object? sender, RoutedEventArgs e) => OpenUrl(Brand.RepoUrl);
    private void OnReleases(object? sender, RoutedEventArgs e) => OpenUrl(Brand.ReleasesUrl);

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser / blocked — ignore */ }
    }
}
