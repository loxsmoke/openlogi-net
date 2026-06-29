using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace OpenLogi.App.Views;

/// <summary>
/// A small modal confirmation dialog with a warning icon. Shown via
/// <c>ShowDialog&lt;bool&gt;</c>; returns <c>true</c> when the user confirms.
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow() => InitializeComponent();

    public ConfirmWindow(string title, string message, string confirmText)
    {
        InitializeComponent();
        Title = title;
        this.FindControl<TextBlock>("TitleText")!.Text = title;
        this.FindControl<TextBlock>("MessageText")!.Text = message;
        this.FindControl<Button>("ConfirmButton")!.Content = confirmText;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
}
