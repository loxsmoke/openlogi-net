using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace OpenLogi.App.Controls;

/// <summary>
/// A small battery glyph that fills to <see cref="Percent"/> and tints itself by
/// charge level. Colour scheme (green → amber → red) is a 3-step take on Logitech's
/// official green/red hardware-LED convention (red at ~10–15%); thresholds live in
/// <see cref="ColorFor"/> and are easy to tune.
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
        var brush = new SolidColorBrush(ColorFor(pct, Charging));

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

    /// <summary>
    /// Level colour, faithful to Logitech's hardware-LED convention: green when good,
    /// red below ~15% (their hardware shows red at the low-battery point; the G305
    /// flashes red &lt;15%). No amber band — Logitech uses only green/red. Charging is green.
    /// </summary>
    private static Color ColorFor(int pct, bool charging)
    {
        if (charging) return Color.Parse("#3FB950"); // charging → green
        if (pct < 15) return Color.Parse("#DA3633");  // red (low / critical)
        return Color.Parse("#3FB950");                 // green (good)
    }
}
