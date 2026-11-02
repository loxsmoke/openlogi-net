using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OpenLogi.App.ViewModels;

namespace OpenLogi.App.Views;

/// <summary>
/// The exact-value editor for one camera control. The device's reported range
/// stays authoritative: a value it doesn't support keeps the dialog open rather
/// than being rounded. Returns the value, or null when cancelled.
/// </summary>
public partial class CameraValueWindow : Window
{
    private readonly Func<int, bool> _accepts = _ => false;
    private readonly TextBox _box;

    public CameraValueWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _box = this.FindControl<TextBox>("ValueBox")!;
    }

    public CameraValueWindow(CameraValuePromptRequest request) : this()
    {
        Title = request.Title;
        _accepts = request.Accepts;
        _box.Text = request.Initial.ToString(System.Globalization.CultureInfo.InvariantCulture);
        this.FindControl<TextBlock>("HintText")!.Text = request.Hint;
        Opened += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Save(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Save();
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void Save()
    {
        if (int.TryParse(_box.Text?.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && _accepts(value))
            Close((int?)value);
        else
            _box.SelectAll();
    }
}
