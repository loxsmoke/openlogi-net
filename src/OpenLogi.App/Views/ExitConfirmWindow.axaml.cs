using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace OpenLogi.App.Views;

/// <summary>What the user chose in the exit-confirmation dialog.</summary>
public enum ExitChoice
{
    /// <summary>Stay open (also the result when the dialog is dismissed).</summary>
    Cancel,
    /// <summary>Keep running in the system tray.</summary>
    MinimizeToTray,
    /// <summary>Really quit, accepting that remaps/gestures/smooth scrolling stop.</summary>
    Exit,
}

/// <summary>
/// A small modal dialog shown before quitting: warns that the app's live
/// functionality (remaps, gestures, smooth scrolling) stops without it and
/// offers minimizing to the tray instead. Shown via
/// <c>ShowDialog&lt;ExitChoice&gt;</c>; dismissing the window means Cancel.
/// Enter presses the default "Minimize to tray" button, Esc cancels.
/// </summary>
public partial class ExitConfirmWindow : Window
{
    public ExitConfirmWindow()
    {
        InitializeComponent();
        Opened += (_, _) => this.FindControl<Button>("MinimizeButton")?.Focus();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(ExitChoice.Cancel);
    private void OnExit(object? sender, RoutedEventArgs e) => Close(ExitChoice.Exit);
    private void OnMinimize(object? sender, RoutedEventArgs e) => Close(ExitChoice.MinimizeToTray);
}
