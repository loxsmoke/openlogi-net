using OpenLogi.App;
using OpenLogi.Assets;
using OpenLogi.Core.Config;

namespace OpenLogi.Tests.App;

/// <summary>Tests for the hotspot geometry ported from Rust <c>mouse_model::geometry</c>.</summary>
public class MouseGeometryTests
{
    private static Metadata WithButtons(uint originW, params Assignment[] assignments) => new()
    {
        Images =
        [
            new ImageEntry
            {
                Key = "device_buttons_image",
                Origin = new Origin { Width = originW, Height = 1024 },
                Assignments = [.. assignments],
            },
        ],
    };

    private static Assignment Slot(string name, float x, float y) =>
        new() { SlotName = name, Marker = new Point { X = x, Y = y } };

    [Theory]
    [InlineData("SLOT_NAME_MIDDLE_BUTTON", ButtonId.MiddleClick)]
    [InlineData("SLOT_NAME_BACK_BUTTON", ButtonId.Back)]
    [InlineData("SLOT_NAME_FORWARD_BUTTON", ButtonId.Forward)]
    [InlineData("SLOT_NAME_MODESHIFT_BUTTON", ButtonId.DpiToggle)]
    [InlineData("SLOT_NAME_GESTURE_BUTTON", ButtonId.GestureButton)]
    public void MapsKnownSlotNames(string slot, ButtonId expected) =>
        Assert.Equal(expected, MouseGeometry.MapSlotName(slot));

    [Fact]
    public void UnknownSlotNameIsUnmapped() => Assert.Null(MouseGeometry.MapSlotName("SLOT_NAME_BOGUS"));

    [Fact]
    public void TranslatesMarkerPercentToCanvasCentre()
    {
        // origin == pngW → no horizontal padding; marker (50,20) on a 200x300 canvas.
        var md = WithButtons(100, Slot("SLOT_NAME_MIDDLE_BUTTON", 50, 20));
        var hotspots = MouseGeometry.HotspotsForPng(md, displayW: 200, displayH: 300, pngW: 100, pngH: 150);
        var h = Assert.Single(hotspots);
        Assert.Equal(ButtonId.MiddleClick, h.Id);
        // centre (100, 60) minus half the hotspot size.
        Assert.Equal(100 - MouseGeometry.HotspotSize / 2, h.X, 3);
        Assert.Equal(60 - MouseGeometry.HotspotSize / 2, h.Y, 3);
    }

    [Fact]
    public void AccountsForSilhouettePadding()
    {
        // origin narrower than the PNG → the bbox is centred with padding, so a
        // 50% marker still lands at the canvas centre.
        var md = WithButtons(50, Slot("SLOT_NAME_MIDDLE_BUTTON", 50, 50));
        var hotspots = MouseGeometry.HotspotsForPng(md, displayW: 200, displayH: 200, pngW: 100, pngH: 100);
        var h = Assert.Single(hotspots);
        Assert.Equal(100 - MouseGeometry.HotspotSize / 2, h.X, 3); // still centred
    }

    [Fact]
    public void LabelYsAreEvenlySpacedAndNonCrossing()
    {
        // Three hotspots out of vertical order; labels must be assigned in y-order
        // (top hotspot → top label) and evenly spaced, each distinct.
        var hotspots = new List<Hotspot>
        {
            new(ButtonId.MiddleClick, 0, 10, 0),  // top
            new(ButtonId.Forward, 0, 200, 0),      // bottom
            new(ButtonId.Back, 0, 100, 0),         // middle
        };
        var ys = MouseGeometry.LabelYs(hotspots, 300);
        Assert.Equal([75.0, 225.0, 150.0], ys);    // step = 300/4 = 75
        Assert.Equal(ys.Count, ys.Distinct().Count());
    }

    [Fact]
    public void ThumbwheelClickBecomesTwoRotationHotspots()
    {
        var md = WithButtons(100, Slot("SLOT_NAME_THUMBWHEEL", 50, 50));
        var hotspots = MouseGeometry.HotspotsForPng(md, 200, 300, 100, 150);
        Assert.DoesNotContain(hotspots, h => h.Id == ButtonId.Thumbwheel);
        var up = hotspots.Single(h => h.Id == ButtonId.ThumbwheelScrollUp);
        var down = hotspots.Single(h => h.Id == ButtonId.ThumbwheelScrollDown);
        Assert.True(up.Y < down.Y);
    }
}
