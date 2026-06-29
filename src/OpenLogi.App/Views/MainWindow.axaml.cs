using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using OpenLogi.App.ViewModels;

namespace OpenLogi.App.Views;

public partial class MainWindow : Window
{
    private TrayIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        Title = version is null ? "OpenLogi.net" : $"OpenLogi.net {version}";
        InitTray();
    }

    // System-tray icon (hidden until the window is minimized to tray).
    private void InitTray()
    {
        var open = new NativeMenuItem("Open OpenLogi.net");
        open.Click += (_, _) => RestoreFromTray();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) =>
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://OpenLogi.App/Assets/icon.ico"))),
            ToolTipText = "OpenLogi.net",
            IsVisible = false,
            Menu = new NativeMenu { Items = { open, quit } },
        };
        _tray.Clicked += (_, _) => RestoreFromTray();
    }

    private bool MinimizeToTrayEnabled() =>
        DataContext is MainWindowViewModel vm && vm.Configuration.AppSettings.MinimizeToTray;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && WindowState == WindowState.Minimized && MinimizeToTrayEnabled())
        {
            if (_tray is not null) _tray.IsVisible = true;
            ShowInTaskbar = false; // hide from the taskbar; the tray icon restores it
            Hide();
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        Activate();
        if (_tray is not null) _tray.IsVisible = false;
    }

    // Secret hotkey: Ctrl+Alt+L opens the per-key color editor (keyboards with 0x8081).
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.L && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt))
        {
            if (DataContext is MainWindowViewModel { PerKeySession: { } session })
                new PerKeyColorWindow(session).Show(this);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void OnSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            new SettingsWindow(vm.Configuration).ShowDialog(this);
    }

    private void OnAbout(object? sender, RoutedEventArgs e) => new AboutWindow().ShowDialog(this);

    private async void OnPickGKey(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: GKeyViewModel gkey }) return;
        var picker = new KeyPickerWindow(gkey.Usage, gkey.Modifier);
        await picker.ShowDialog(this);
        if (picker.Confirmed && picker.Result.Usage != 0)
            gkey.Apply(picker.Result.Usage, picker.Result.Modifier);
    }
}
