using System;
using System.Drawing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenLogi.App.ViewModels;
using WinForms = System.Windows.Forms;

namespace OpenLogi.App.Views;

public partial class MainWindow : Window
{
    private WinForms.NotifyIcon? _tray;
    // Set once the user confirmed quitting (or quit from the tray menu), so the
    // resumed close isn't intercepted again.
    private bool _exitConfirmed;
    // Guards against stacking dialogs when the close button is clicked repeatedly.
    private bool _exitPromptOpen;
    private bool _startHiddenApplied;
    private string? _lastTrayUpdateVersion;
    private MainWindowViewModel? _subscribedViewModel;

    public bool StartHiddenToTray { get; init; }

    public MainWindow()
    {
        InitializeComponent();
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        Title = version is null ? "OpenLogi.net" : $"OpenLogi.net {version}";
        InitTray();
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedViewModel is not null)
            _subscribedViewModel.UpdateOfferShown -= OnUpdateOfferShown;

        _subscribedViewModel = DataContext as MainWindowViewModel;
        if (_subscribedViewModel is not null)
            _subscribedViewModel.UpdateOfferShown += OnUpdateOfferShown;
    }

    // Launch/open update check. The setting defaults on for new configs and can be
    // changed in Settings; the check itself is silent on failure.
    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (StartHiddenToTray && !_startHiddenApplied)
        {
            _startHiddenApplied = true;
            HideToTray();
            if (vm.Configuration.AppSettings.CheckForUpdates)
                await vm.CheckForUpdatesAsync();
            return;
        }

        await vm.CheckForUpdatesAsync();
    }

    // System-tray icon (hidden until the window is minimized to tray).
    private void InitTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open OpenLogi.net", null, (_, _) => Dispatcher.UIThread.Post(RestoreFromTray));
        menu.Items.Add("Quit", null, (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // Quit from the tray menu is already an explicit choice (and the window
            // may be hidden, so there's nothing to own a dialog) — skip the prompt.
            _exitConfirmed = true;
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }));

        _tray = new WinForms.NotifyIcon
        {
            Icon = AppIcon(),
            Text = "OpenLogi.net",
            Visible = false,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                Dispatcher.UIThread.Post(RestoreFromTray);
        };
        _tray.BalloonTipClicked += (_, _) => Dispatcher.UIThread.Post(RestoreFromTray);
    }

    private bool MinimizeToTrayEnabled() =>
        DataContext is MainWindowViewModel vm && vm.Configuration.AppSettings.MinimizeToTray;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && WindowState == WindowState.Minimized && MinimizeToTrayEnabled())
            HideToTray();
    }

    /// <summary>
    /// F5 refreshes the device list — but only on the home gallery. While a device
    /// page is open its live HID++ session must not be torn down by a stray keypress,
    /// so F5 is ignored there (the "← Devices" back button returns home first).
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5 && DataContext is MainWindowViewModel { ShowingDevice: false } vm)
        {
            if (vm.RefreshCommand.CanExecute(null)) vm.RefreshCommand.Execute(null);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void HideToTray()
    {
        if (_tray is not null) _tray.Visible = true;
        ShowInTaskbar = false; // hide from the taskbar; the tray icon restores it
        Hide();
    }

    /// <summary>
    /// Intercept a user-initiated close: quitting silently kills the app's live
    /// functionality (remaps, gestures, smooth scrolling), so confirm first and
    /// offer minimizing to the tray instead. Programmatic/OS shutdowns (and the
    /// close resumed after the user confirmed) pass through untouched.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_exitConfirmed && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            if (!_exitPromptOpen) _ = ConfirmExitAsync();
        }
        base.OnClosing(e);
    }

    private async System.Threading.Tasks.Task ConfirmExitAsync()
    {
        _exitPromptOpen = true;
        try
        {
            switch (await new ExitConfirmWindow().ShowDialog<ExitChoice>(this))
            {
                case ExitChoice.Exit:
                    _exitConfirmed = true;
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                        lifetime.Shutdown();
                    else
                        Close();
                    break;
                case ExitChoice.MinimizeToTray:
                    HideToTray();
                    break;
                // Cancel (or dialog dismissed): stay open, nothing to do.
            }
        }
        finally { _exitPromptOpen = false; }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        Activate();
        if (_tray is not null) _tray.Visible = false;
    }

    private void OnUpdateOfferShown(string version)
    {
        if (_tray is null || _lastTrayUpdateVersion == version || IsVisible) return;

        _lastTrayUpdateVersion = version;
        _tray.Visible = true;
        _tray.ShowBalloonTip(
            10000,
            "OpenLogi.net update available",
            $"Version {version} is ready. Click to open OpenLogi.net.",
            WinForms.ToolTipIcon.Info);
    }

    private static System.Drawing.Icon AppIcon() =>
        Environment.ProcessPath is { } path
            ? System.Drawing.Icon.ExtractAssociatedIcon(path) ?? SystemIcons.Application
            : SystemIcons.Application;

    protected override void OnClosed(EventArgs e)
    {
        if (_subscribedViewModel is not null)
            _subscribedViewModel.UpdateOfferShown -= OnUpdateOfferShown;
        _tray?.Dispose();
        base.OnClosed(e);
    }

    // Opens the per-key color editor (keyboards with PerKeyLighting 0x8081).
    private void OnOpenPerKeyEditor(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.CreatePerKeyEditor() is { } editor)
        {
            var window = new PerKeyColorWindow(editor);
            // Pause the lighting keepalive while the editor is open so it can't overwrite
            // the user's live painting; resume when the window closes.
            vm.PerKeyEditorOpen = true;
            window.Closed += (_, _) => vm.PerKeyEditorOpen = false;
            window.Show(this);
        }
    }

    private void OnSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            new SettingsWindow(vm.Configuration).ShowDialog(this);
    }

    private void OnAbout(object? sender, RoutedEventArgs e) => new AboutWindow().ShowDialog(this);

    private async void OnForgetHost(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: HostSlotViewModel slot }) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var (title, message) = slot.IsCurrent
            ? ($"Forget host {slot.Number} — the one you're using?",
               $"This device is connected to this computer through host {slot.Number}. " +
               "Forgetting it disconnects the device now, and you'll have to pair it again to use it.")
            : ($"Forget host {slot.Number}?",
               "The computer paired in this slot will have to pair again to reconnect.");

        var confirmed = await new ConfirmWindow(title, message, "Forget host").ShowDialog<bool>(this);
        if (confirmed) await vm.ForgetHostAsync(slot);
    }

    /// <summary>
    /// Clicking a button's diagram label (which also opens its picker flyout)
    /// selects that button in the Gestures panel, when it can gesture.
    /// </summary>
    private void OnAnnotationLabelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DiagramAnnotationViewModel annotation }
            && DataContext is MainWindowViewModel vm)
            vm.SelectGestureOwnerFor(annotation.Binding.Button);
    }

    private async void OnPickGKey(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: GKeyViewModel gkey }) return;
        var picker = new KeyPickerWindow(gkey.Usage, gkey.Modifier);
        await picker.ShowDialog(this);
        if (picker.Confirmed && picker.Result.Usage != 0)
            gkey.Apply(picker.Result.Usage, picker.Result.Modifier);
    }
}
