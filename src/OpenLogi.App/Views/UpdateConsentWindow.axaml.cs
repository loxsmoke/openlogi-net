using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace OpenLogi.App.Views;

/// <summary>
/// One-time first-run prompt asking whether to enable launch-time update checks.
/// Shown via <c>ShowDialog&lt;bool&gt;</c>; returns <c>true</c> when the user opts in.
/// </summary>
public partial class UpdateConsentWindow : Window
{
    public UpdateConsentWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDecline(object? sender, RoutedEventArgs e) => Close(false);
    private void OnAccept(object? sender, RoutedEventArgs e) => Close(true);
}
