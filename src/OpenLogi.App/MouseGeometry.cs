using OpenLogi.Assets;
using OpenLogi.Core;

namespace OpenLogi.App;

/// <summary>A positioned, clickable button hotspot over the rendered device image (display pixels).</summary>
public sealed record Hotspot(ButtonId Id, double X, double Y, double Size);

/// <summary>
/// Translates Logitech's percent-based hotspot markers into display-pixel rects
/// over the rendered PNG. Ported from Rust <c>mouse_model::geometry</c>.
/// </summary>
public static class MouseGeometry
{
    /// <summary>Hit-target size for each hotspot (Logi gives a point, not a rect).</summary>
    public const double HotspotSize = 40;

    private const double ThumbwheelRotationOffset = 16;

    /// <summary>Logitech's stable slot vocabulary → OpenLogi's <see cref="ButtonId"/>.</summary>
    public static ButtonId? MapSlotName(string name) => name switch
    {
        "SLOT_NAME_LEFT_BUTTON" => ButtonId.LeftClick,
        "SLOT_NAME_RIGHT_BUTTON" => ButtonId.RightClick,
        "SLOT_NAME_MIDDLE_BUTTON" => ButtonId.MiddleClick,
        "SLOT_NAME_BACK_BUTTON" => ButtonId.Back,
        "SLOT_NAME_FORWARD_BUTTON" => ButtonId.Forward,
        "SLOT_NAME_MODESHIFT_BUTTON" => ButtonId.DpiToggle,
        "SLOT_NAME_THUMBWHEEL" => ButtonId.Thumbwheel,
        "SLOT_NAME_GESTURE_BUTTON" => ButtonId.GestureButton,
        _ => null,
    };

    /// <summary>
    /// Compute hotspot rects for a metadata + rendered-PNG pairing. Markers are
    /// percentages of the silhouette bbox (<c>origin</c>), which the PNG centres
    /// with equal horizontal padding; the y ratio is 1:1.
    /// </summary>
    public static IReadOnlyList<Hotspot> HotspotsForPng(Metadata metadata, double displayW, double displayH, int pngW, int pngH)
    {
        var originW = Math.Min(metadata.OriginOf()?.Width ?? (uint)pngW, (uint)Math.Max(pngW, 1));
        var bboxWRendered = pngW > 0 ? displayW * originW / pngW : displayW;
        var bboxXOffset = (displayW - bboxWRendered) / 2.0;

        var hotspots = new List<Hotspot>();
        foreach (var a in metadata.Assignments())
        {
            if (MapSlotName(a.SlotName) is not { } id) continue;
            var cx = bboxXOffset + a.Marker.X / 100.0 * bboxWRendered;
            var cy = a.Marker.Y / 100.0 * displayH;
            hotspots.Add(new Hotspot(id, cx - HotspotSize / 2, cy - HotspotSize / 2, HotspotSize));
        }
        return WithThumbwheelRotation(hotspots);
    }

    /// <summary>
    /// Y positions for each hotspot's side label, evenly spaced down the model
    /// height and assigned in hotspot-y order so leader lines don't cross. The
    /// returned list is index-aligned with <paramref name="hotspots"/>. Ported
    /// from Rust <c>labels_from_hotspots</c>.
    /// </summary>
    public static IReadOnlyList<double> LabelYs(IReadOnlyList<Hotspot> hotspots, double mouseH)
    {
        if (hotspots.Count == 0) return [];
        var step = mouseH / (hotspots.Count + 1);
        var ranks = Enumerable.Range(0, hotspots.Count)
            .OrderBy(i => hotspots[i].Y + hotspots[i].Size / 2)
            .ToArray();
        var slotOf = new int[hotspots.Count];
        for (var rank = 0; rank < ranks.Length; rank++)
            slotOf[ranks[rank]] = rank;
        return [.. Enumerable.Range(0, hotspots.Count).Select(i => step * (slotOf[i] + 1))];
    }

    /// <summary>
    /// Replace a thumb-wheel click hotspot with two rotation hotspots stacked
    /// above/below it (the click stays bound; only the rotations are surfaced).
    /// </summary>
    public static IReadOnlyList<Hotspot> WithThumbwheelRotation(IReadOnlyList<Hotspot> hotspots)
    {
        var wheel = hotspots.FirstOrDefault(h => h.Id == ButtonId.Thumbwheel);
        if (wheel is null) return hotspots;
        var result = hotspots.Where(h => h.Id != ButtonId.Thumbwheel).ToList();
        result.Add(wheel with { Id = ButtonId.ThumbwheelScrollUp, Y = wheel.Y - ThumbwheelRotationOffset });
        result.Add(wheel with { Id = ButtonId.ThumbwheelScrollDown, Y = wheel.Y + ThumbwheelRotationOffset });
        return result;
    }
}
