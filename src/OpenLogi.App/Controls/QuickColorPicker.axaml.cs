using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace OpenLogi.App.Controls;

/// <summary>A row of quick color swatches + the full ColorPicker for exact selection.</summary>
public partial class QuickColorPicker : UserControl
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<QuickColorPicker, Color>(
            nameof(Color), Colors.White, defaultBindingMode: BindingMode.TwoWay);

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public QuickColorPicker() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnSwatch(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { Tag: string hex })
            Color = Color.Parse(hex);
    }
}
