using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OpenLogi.Core;
using OpenLogi.Core.Localization;

namespace OpenLogi.App.Views;

public partial class AboutWindow : Window
{
    /// <summary>App + Windows + Logitech-software facts, as copied by "Copy info".</summary>
    private readonly string _diagnostics;

    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1";
        var windows = SystemInfo.WindowsVersion();
        var memory = SystemInfo.MemoryUsage();
        var logi = SystemInfo.DetectLogitechSoftware();

        this.FindControl<TextBlock>("VersionText")!.Text = $"v{version}";
        this.FindControl<TextBlock>("SystemText")!.Text = $"{windows}\n{memory}";
        if (logi.Count > 0)
        {
            var warning = this.FindControl<TextBlock>("LogiWarningText")!;
            warning.Text = Loc.Current.Format("About_LogiWarning", string.Join(", ", logi));
            warning.IsVisible = true;
        }

        _diagnostics = $"OpenLogi.net v{version}\n{windows}\n{memory}\n"
            + $"Logitech software: {(logi.Count > 0 ? string.Join(", ", logi) : "none detected")}";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnGitHub(object? sender, RoutedEventArgs e) => OpenUrl(Brand.RepoUrl);
    private void OnReleases(object? sender, RoutedEventArgs e) => OpenUrl(Brand.ReleasesUrl);
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Open the log folder in Explorer, with the current log selected when one
    /// exists. The folder is created if missing so the link always lands somewhere.
    /// </summary>
    private void OnOpenLogs(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Paths.LogDir());
            var log = Paths.LogPath();
            var info = File.Exists(log)
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{log}\"")
                : new ProcessStartInfo("explorer.exe", $"\"{Paths.LogDir()}\"");
            Process.Start(info);
        }
        catch { /* explorer unavailable / folder locked down — ignore */ }
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(_diagnostics);
            if (sender is Button button)
            {
                button.Content = Loc.Current["About_Copied"];
                await System.Threading.Tasks.Task.Delay(1500);
                button.Content = Loc.Current["About_CopyInfo"];
            }
        }
        catch { /* clipboard unavailable — nothing to signal */ }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser / blocked — ignore */ }
    }
}
