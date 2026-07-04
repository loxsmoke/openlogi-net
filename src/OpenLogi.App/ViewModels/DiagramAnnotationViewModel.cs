using Avalonia;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenLogi.App.ViewModels;

/// <summary>
/// One annotated hotspot on the mouse diagram: the clickable marker over the
/// render, a side label (button name + current action), and the leader-line
/// polyline connecting them. Mirrors the original's leader-line layout.
/// </summary>
public sealed partial class DiagramAnnotationViewModel : ObservableObject
{
    public ButtonBindingViewModel Binding { get; }

    /// <summary>
    /// Accent-highlights this annotation's label frame and leader line while its
    /// button is the selected gesture owner.
    /// </summary>
    [ObservableProperty]
    private bool _highlighted;

    // Marker rect over the render (canvas coords).
    public double MarkerX { get; }
    public double MarkerY { get; }
    public double Size { get; }

    // Side label panel (left column).
    public double LabelX { get; }
    public double LabelY { get; }
    public double LabelWidth { get; }

    /// <summary>The leader-line points: hotspot centre → horizontal stub → label anchor.</summary>
    public AvaloniaList<Point> LinePoints { get; }

    public DiagramAnnotationViewModel(
        ButtonBindingViewModel binding,
        double markerX, double markerY, double size,
        double labelX, double labelY, double labelWidth,
        Point center, Point stub, Point anchor)
    {
        Binding = binding;
        MarkerX = markerX;
        MarkerY = markerY;
        Size = size;
        LabelX = labelX;
        LabelY = labelY;
        LabelWidth = labelWidth;
        LinePoints = [center, stub, anchor];
    }
}
