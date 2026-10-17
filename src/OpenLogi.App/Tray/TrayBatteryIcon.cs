using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using OpenLogi.App.Controls;
using OpenLogi.App.Utils;
using OpenLogi.Core.DeviceInfo;

namespace OpenLogi.App.Tray;

/// <summary>Which device silhouette sits beside the battery in a tray icon.</summary>
public enum TrayGlyph
{
    /// <summary>A keyboard standing on its side (keyboards, numpads).</summary>
    Keyboard,
    /// <summary>The left half of a mouse (mice, trackballs).</summary>
    Mouse,
    /// <summary>No silhouette: the battery alone, centred (headsets, presenters, …).</summary>
    None,
}

/// <summary>
/// Everything that decides what a tray battery icon looks like. Two icons with the
/// same key are pixel-identical, so the key doubles as the bitmap cache key: charge
/// is bucketed to quarters (<see cref="BucketStep"/>) because at 16 px only four
/// fill heights stay tellable apart, and it also means BLE battery chatter does not
/// re-create native icons for a one-point change.
/// </summary>
public readonly record struct TrayBatteryIconKey(TrayGlyph Glyph, int Bucket, bool Charging, bool Low, bool DarkTaskbar)
{
    public const int BucketStep = 25;

    /// <summary>Charge the icon draws, in whole buckets (0, 25, 50, 75, 100).</summary>
    public int Percent => Bucket * BucketStep;

    /// <summary>
    /// <see cref="Low"/> is decided on the raw reading, not the bucket: 20 % and 15 %
    /// both draw the quarter fill, but only 15 % draws it red. Charging is never low.
    /// </summary>
    public static TrayBatteryIconKey For(DeviceKind kind, int percent, bool charging, bool darkTaskbar) =>
        new(GlyphFor(kind), BucketFor(percent), charging, BatteryUtils.IsLow(percent), darkTaskbar);

    public static TrayGlyph GlyphFor(DeviceKind kind) => kind switch
    {
        DeviceKind.Keyboard or DeviceKind.Numpad => TrayGlyph.Keyboard,
        DeviceKind.Mouse or DeviceKind.Trackball => TrayGlyph.Mouse,
        _ => TrayGlyph.None,
    };

    /// <summary>
    /// Nearest quarter: 0–12 % → empty, 13–37 % → 25 %, 38–62 % → 50 %, 63–87 % → 75 %,
    /// 88–100 % → full. A flat battery draws empty; its red shell (see
    /// <see cref="TrayBatteryIcon"/>) is what keeps it noticeable.
    /// </summary>
    public static int BucketFor(int percent) =>
        (int)Math.Round(Math.Clamp(percent, 0, 100) / (double)BucketStep, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Draws the tray battery icon: a device glyph on the left sharing its right wall
/// with an upright battery on the right, whose interior fills from the bottom to the
/// charge level. The whole battery — shell, nub and fill — carries the level colour
/// The device glyph always keeps the taskbar's own outline colour. Geometry is
/// authored on a 32-unit grid (the proposal's SVG, verbatim) and scaled to whatever
/// pixel size the taskbar wants.
/// </summary>
public static class TrayBatteryIcon
{
    /// <summary>Design grid the geometry below is authored on.</summary>
    public const double Grid = 32;

    private static readonly Color DarkOutline = Color.Parse("#F2F2F2");
    private static readonly Color LightOutline = Color.Parse("#1B1B1B");
    private static readonly Color DarkTaskbar = Color.Parse("#202020");
    private static readonly Color LightTaskbar = Color.Parse("#EEEEEE");

    // Battery, upright: body 15.5..27.5 × 5..29, nub on top, interior 17.5..25.5 × 7..27.
    private static readonly RoundedRect BatteryBody = new(new Rect(15.5, 5, 12, 24), 2.5);
    private static readonly RoundedRect BatteryNub = new(new Rect(19, 2.5, 5, 2.5), 1);
    private const double FillLeft = 17.5, FillWidth = 8, FillBottom = 27, FillMaxHeight = 20, FillMinHeight = 1.2;
    private static readonly Geometry Bolt = Geometry.Parse("M 24.2,7.6 L 17.8,18.2 L 21.4,18.2 L 18.9,26.4 L 25.3,15.6 L 21.7,15.6 Z");

    // Keyboard on its side: 2.5..11.5 × 3..29, two key columns, the long bar is the space bar.
    private static readonly RoundedRect KeyboardBody = new(new Rect(2.5, 3, 9, 26), 2.5);
    private static readonly double[] KeyRows = [6, 9.6, 13.2, 16.8, 20.4, 24];
    private const double KeyColumnLeft = 4.4, KeyColumnRight = 7.6, KeySize = 1.8;
    private static readonly RoundedRect SpaceBar = new(new Rect(7.6, 20.4, 1.8, 5.4), 0.6);

    // Left half of a mouse: a closed "D" with the button split and the scroll wheel on the cut edge.
    private static readonly Geometry MouseBody = Geometry.Parse("M 11,3.2 C 6,3.2 2.5,7 2.5,12 L 2.5,20 C 2.5,25 6,28.8 11,28.8 Z");
    private static readonly Point MouseSplitFrom = new(2.5, 13.5), MouseSplitTo = new(11, 13.5);
    private static readonly RoundedRect MouseWheel = new(new Rect(8.6, 6.6, 2.4, 5.4), 1.2);

    /// <summary>
    /// Horizontal offsets that close the gap: the glyph slides right until its right
    /// edge sits on the battery's left wall (one shared stroke), then the pair shifts
    /// left so it is centred in the frame. Without a glyph the battery alone is centred.
    /// </summary>
    public static (double Glyph, double Battery) OffsetsFor(TrayGlyph glyph) => glyph switch
    {
        TrayGlyph.Keyboard => (3, -1),
        TrayGlyph.Mouse => (3.5, -1),
        _ => (0, -5.5),
    };

    /// <summary>Height of the fill rectangle on the design grid for a given bucketed charge.</summary>
    public static double FillHeightFor(int percent) =>
        percent <= 0 ? 0 : Math.Max(FillMinHeight, FillMaxHeight * Math.Min(percent, 100) / 100.0);

    /// <summary>
    /// Pixel size to render at for a taskbar scaling factor: Windows draws tray icons
    /// at 16 px × scaling, so matching it keeps the strokes crisp instead of resampled.
    /// </summary>
    public static int PixelSizeFor(double scaling) =>
        Math.Max(16, (int)Math.Round(16 * (double.IsFinite(scaling) && scaling > 0 ? scaling : 1)));

    /// <summary>Renders the icon for <paramref name="key"/> at <paramref name="pixelSize"/> square pixels.</summary>
    public static WindowIcon Render(TrayBatteryIconKey key, int pixelSize)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(pixelSize, pixelSize), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext(true))
        {
            var scale = pixelSize / Grid;
            using (ctx.PushTransform(Matrix.CreateScale(scale, scale)))
                Draw(ctx, key);
        }
        return new WindowIcon(bitmap);
    }

    /// <summary>Draws the icon on the 32-unit design grid.</summary>
    public static void Draw(DrawingContext ctx, TrayBatteryIconKey key)
    {
        var outline = new ImmutableSolidColorBrush(key.DarkTaskbar ? DarkOutline : LightOutline);
        var taskbar = new ImmutableSolidColorBrush(key.DarkTaskbar ? DarkTaskbar : LightTaskbar);
        var stroke = new ImmutablePen(outline, 1.8, lineJoin: PenLineJoin.Round);
        var battery = new ImmutableSolidColorBrush(BatteryUtils.ColorFor(key.Low, key.Charging, true));
        var batteryStroke = new ImmutablePen(battery, 1.8, lineJoin: PenLineJoin.Round);
        var (glyphDx, batteryDx) = OffsetsFor(key.Glyph);

        using (ctx.PushTransform(Matrix.CreateTranslation(glyphDx, 0)))
        {
            switch (key.Glyph)
            {
                case TrayGlyph.Keyboard: DrawKeyboard(ctx, outline, stroke); break;
                case TrayGlyph.Mouse: DrawMouse(ctx, outline, stroke); break;
            }
        }

        using (ctx.PushTransform(Matrix.CreateTranslation(batteryDx, 0)))
            DrawBattery(ctx, key, battery, taskbar, batteryStroke);
    }

    private static void DrawKeyboard(DrawingContext ctx, IImmutableBrush outline, IPen stroke)
    {
        ctx.DrawRectangle(null, stroke, KeyboardBody);
        foreach (var y in KeyRows)
            ctx.DrawRectangle(outline, null, new RoundedRect(new Rect(KeyColumnLeft, y, KeySize, KeySize), 0.4));
        for (var i = 0; i < 4; i++)
            ctx.DrawRectangle(outline, null, new RoundedRect(new Rect(KeyColumnRight, KeyRows[i], KeySize, KeySize), 0.4));
        ctx.DrawRectangle(outline, null, SpaceBar);
    }

    private static void DrawMouse(DrawingContext ctx, IImmutableBrush outline, IPen stroke)
    {
        ctx.DrawGeometry(null, stroke, MouseBody);
        ctx.DrawLine(new ImmutablePen(outline, 1.4), MouseSplitFrom, MouseSplitTo);
        ctx.DrawRectangle(outline, null, MouseWheel);
    }

    private static void DrawBattery(DrawingContext ctx, TrayBatteryIconKey key, IImmutableBrush battery, IImmutableBrush taskbar, IPen stroke)
    {
        ctx.DrawRectangle(null, stroke, BatteryBody);
        ctx.DrawRectangle(battery, null, BatteryNub);

        var h = FillHeightFor(key.Percent);
        if (h > 0)
            ctx.DrawRectangle(battery, null, new RoundedRect(new Rect(FillLeft, FillBottom - h, FillWidth, h), 1));

        if (key.Charging)
        {
            // The bolt is a cut-out in the taskbar colour; its halo keeps the
            // shape readable where it crosses the empty (transparent) interior.
            ctx.DrawGeometry(null, new ImmutablePen(battery, 0.9, lineJoin: PenLineJoin.Round), Bolt);
            ctx.DrawGeometry(taskbar, null, Bolt);
        }
    }
}
