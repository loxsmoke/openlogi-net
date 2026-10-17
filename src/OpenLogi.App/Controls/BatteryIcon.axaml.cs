using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using OpenLogi.App.Utils;

namespace OpenLogi.App.Controls;

/// <summary>
/// A small battery glyph that fills to <see cref="Percent"/> and tints itself by
/// charge level. 
/// </summary>
public partial class BatteryIcon : UserControl
{
    // Interior width available to the fill = Shell.Width − 2·BorderThickness − 2·Padding
    // (22 − 2·1.3 − 2·1.5). Keep in sync with BatteryIcon.axaml.
    private const double InnerWidth = 16.4;

    public static readonly StyledProperty<int> PercentProperty =
        AvaloniaProperty.Register<BatteryIcon, int>(nameof(Percent));

    public static readonly StyledProperty<bool> ChargingProperty =
        AvaloniaProperty.Register<BatteryIcon, bool>(nameof(Charging));

    public int Percent { get => GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public bool Charging { get => GetValue(ChargingProperty); set => SetValue(ChargingProperty, value); }

    public BatteryIcon()
    {
        InitializeComponent();
        Render();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PercentProperty || change.Property == ChargingProperty)
            Render();
    }

    private void Render()
    {
        var pct = Math.Clamp(Percent, 0, 100);
        var brush = new SolidColorBrush(BatteryUtils.ColorFor(pct, Charging));

        var shell = this.FindControl<Border>("Shell");
        var fill = this.FindControl<Border>("Fill");
        var nub = this.FindControl<Border>("Nub");
        if (shell is null || fill is null || nub is null) return; // before the template is applied

        shell.BorderBrush = brush;
        nub.Background = brush;
        fill.Background = brush;
        // A non-zero charge always shows a sliver so "critical" still reads as some fill.
        fill.Width = pct == 0 ? 0 : Math.Max(2, InnerWidth * pct / 100.0);
    }
}
