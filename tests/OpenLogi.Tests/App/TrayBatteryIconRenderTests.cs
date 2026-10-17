using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OpenLogi.App.Tray;

namespace OpenLogi.Tests.App;

/// <summary>
/// Renders the tray battery icons through the real Avalonia platform (Win32 + Skia,
/// set up without a message loop) and checks the pixels that carry meaning: the
/// fill colour at the charge level, the orange bolt while charging, and the outline
/// colour on the shared glyph/battery wall. The 16/24/32 px variants are also saved
/// as PNGs under <c>%TEMP%\openlogi-tray-icons</c> for eyeballing.
/// </summary>
public class TrayBatteryIconRenderTests
{
    private static readonly Lazy<bool> Platform = new(() =>
    {
        try
        {
            AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
            return true;
        }
        catch
        {
            return false;
        }
    });

    private const int Scale = 2; // render at 64 px: every assertion lands on a fully covered pixel
    private const int Size = (int)TrayBatteryIcon.Grid * Scale;

    private static readonly Color Green = Color.Parse("#3FB950");
    private static readonly Color Red = Color.Parse("#DA3633");
    private static readonly Color Orange = Color.Parse("#FF9A1F");
    private static readonly Color DarkOutline = Color.Parse("#F2F2F2");
    private static readonly Color LightOutline = Color.Parse("#1B1B1B");

    [Theory]
    [InlineData(TrayGlyph.Keyboard, 72, false, true)]
    [InlineData(TrayGlyph.Keyboard, 72, false, false)]
    [InlineData(TrayGlyph.Mouse, 72, false, true)]
    [InlineData(TrayGlyph.None, 72, false, true)]
    public void FillAndShellAreGreenAtGoodCharge(TrayGlyph glyph, int percent, bool charging, bool dark)
    {
        Assert.True(Platform.Value, "Avalonia platform could not be set up in the test process");
        var key = new TrayBatteryIconKey(glyph, TrayBatteryIconKey.BucketFor(percent), charging, false, dark);
        using var pixels = Render(key);

        // Fill interior (x 21.5, y 26 on the grid) sits inside the 75 % fill, which spans y 12..27.
        var (_, batteryDx) = TrayBatteryIcon.OffsetsFor(glyph);
        AssertColour(Green, pixels.At(21.5 + batteryDx, 26));
        AssertColour(Green, pixels.At(21.5 + batteryDx, 13));
        // Just above the fill's top edge the interior is empty (transparent).
        Assert.Equal(0, pixels.At(21.5 + batteryDx, 9).A);
        // The battery's shell carries the level colour too (right wall and nub).
        AssertColour(Green, pixels.At(27.5 + batteryDx, 16));
        AssertColour(Green, pixels.At(21.5 + batteryDx, 3.75));
    }

    [Theory]
    [InlineData(TrayGlyph.Keyboard)]
    [InlineData(TrayGlyph.Mouse)]
    public void LowChargePaintsTheWholeBatteryRed(TrayGlyph glyph)
    {
        Assert.True(Platform.Value);
        var (glyphDx, batteryDx) = TrayBatteryIcon.OffsetsFor(glyph);

        var kind = glyph == TrayGlyph.Keyboard ? OpenLogi.Core.DeviceInfo.DeviceKind.Keyboard : OpenLogi.Core.DeviceInfo.DeviceKind.Mouse;

        // 20 %: quarter fill (y 22..27), green fill and green shell.
        using (var pixels = Render(TrayBatteryIconKey.For(kind, 20, false, true)))
        {
            AssertColour(Green, pixels.At(21.5 + batteryDx, 26));
            AssertColour(Green, pixels.At(27.5 + batteryDx, 16));
        }

        // 15 %: the same quarter fill, now red, and the shell red with it;
        // the device glyph is never touched.
        using (var pixels = Render(TrayBatteryIconKey.For(kind, 15, false, true)))
        {
            AssertColour(Red, pixels.At(21.5 + batteryDx, 26));
            Assert.Equal(0, pixels.At(21.5 + batteryDx, 20).A);     // empty above the quarter
            AssertColour(Red, pixels.At(27.5 + batteryDx, 16));     // battery's right wall
            AssertColour(DarkOutline, pixels.At(2.5 + glyphDx, 16)); // glyph untouched
        }

        // 11 % → empty: no fill at all, the red shell (wall + nub) is the warning.
        using (var pixels = Render(TrayBatteryIconKey.For(kind, 11, false, true)))
        {
            Assert.Equal(0, pixels.At(21.5 + batteryDx, 26).A);
            AssertColour(Red, pixels.At(27.5 + batteryDx, 16));
            AssertColour(Red, pixels.At(21.5 + batteryDx, 3.75));
            AssertColour(DarkOutline, pixels.At(2.5 + glyphDx, 16));
        }
    }

    [Theory]
    [InlineData(TrayGlyph.Keyboard)]
    [InlineData(TrayGlyph.Mouse)]
    public void ChargingPaintsTheBatteryOrangeWithACutOutBolt(TrayGlyph glyph)
    {
        Assert.True(Platform.Value);
        var key = new TrayBatteryIconKey(glyph, TrayBatteryIconKey.BucketFor(80), true, false, true);
        using var pixels = Render(key);
        var (_, batteryDx) = TrayBatteryIcon.OffsetsFor(glyph);
        // At y 16 the bolt spans x 19.1..25.1; y 16 is inside the 75 % fill (y 12..27),
        // so the cut-out shows the dark taskbar colour there, orange fill beside it,
        // and an orange shell.
        AssertColour(Color.Parse("#202020"), pixels.At(22 + batteryDx, 16.5));
        AssertColour(Orange, pixels.At(18 + batteryDx, 25));
        AssertColour(Orange, pixels.At(27.5 + batteryDx, 16));
    }

    [Fact]
    public void GlyphSharesTheBatteryWallWithNoGap()
    {
        Assert.True(Platform.Value);
        foreach (var glyph in new[] { TrayGlyph.Keyboard, TrayGlyph.Mouse })
        {
            var key = new TrayBatteryIconKey(glyph, 3, false, false, true);
            using var pixels = Render(key);
            var (glyphDx, batteryDx) = TrayBatteryIcon.OffsetsFor(glyph);
            var wallX = 15.5 + batteryDx;
            // One stroke: the battery's (green) stroke lands on the wall over the glyph's,
            // transparent one unit either side (glyph interior left, battery interior right).
            AssertColour(Green, pixels.At(wallX, 16));
            Assert.Equal(0, pixels.At(wallX - 1.6, 16).A);
            Assert.Equal(0, pixels.At(wallX + 1.6, 9).A);
            // And something of the glyph is drawn further left (a key dot / the mouse arc).
            var leftmost = glyph == TrayGlyph.Keyboard ? 2.5 + glyphDx : 2.5 + glyphDx;
            AssertColour(DarkOutline, pixels.At(leftmost, 16));
        }
    }

    [Fact]
    public void SavesTaskbarSizedVariantsForReview()
    {
        Assert.True(Platform.Value);
        var dir = Path.Combine(Path.GetTempPath(), "openlogi-tray-icons");
        Directory.CreateDirectory(dir);
        foreach (var glyph in new[] { TrayGlyph.Keyboard, TrayGlyph.Mouse, TrayGlyph.None })
        foreach (var (pct, charging) in new[] { (100, false), (72, false), (40, false), (20, false), (14, false), (11, false), (55, true) })
        foreach (var dark in new[] { true, false })
        foreach (var size in new[] { 16, 24, 32, 64 })
        {
            var key = TrayBatteryIconKey.For(glyph switch
            {
                TrayGlyph.Keyboard => OpenLogi.Core.DeviceInfo.DeviceKind.Keyboard,
                TrayGlyph.Mouse => OpenLogi.Core.DeviceInfo.DeviceKind.Mouse,
                _ => OpenLogi.Core.DeviceInfo.DeviceKind.Headset,
            }, pct, charging, dark);
            var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
            using (var ctx = bitmap.CreateDrawingContext(true))
            using (ctx.PushTransform(Matrix.CreateScale(size / TrayBatteryIcon.Grid, size / TrayBatteryIcon.Grid)))
                TrayBatteryIcon.Draw(ctx, key);
            var name = $"{glyph}-{pct:000}{(charging ? "-charging" : "")}-{(dark ? "dark" : "light")}-{size}.png";
            bitmap.Save(Path.Combine(dir, name));
            Assert.True(File.Exists(Path.Combine(dir, name)));
        }
    }

    // --- helpers ---------------------------------------------------------------

    private static Pixels Render(TrayBatteryIconKey key)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext(true))
        using (ctx.PushTransform(Matrix.CreateScale(Scale, Scale)))
            TrayBatteryIcon.Draw(ctx, key);

        using var png = new MemoryStream();
        bitmap.Save(png);
        png.Position = 0;
        // Decoding at the native width is lossless; it is just the documented entry point.
        return new Pixels(WriteableBitmap.DecodeToWidth(png, Size, BitmapInterpolationMode.None));
    }

    private static void AssertColour(Color expected, Color actual)
    {
        Assert.True(actual.A > 200, $"expected an opaque pixel, got {actual}");
        Assert.True(Math.Abs(expected.R - actual.R) <= 12 && Math.Abs(expected.G - actual.G) <= 12 && Math.Abs(expected.B - actual.B) <= 12,
            $"expected {expected}, got {actual}");
    }

    /// <summary>Pixel access on grid coordinates (scaled by <see cref="Scale"/>).</summary>
    private sealed class Pixels : IDisposable
    {
        private readonly WriteableBitmap _bitmap;
        public Pixels(WriteableBitmap bitmap) => _bitmap = bitmap;

        public Color At(double gridX, double gridY)
        {
            var x = (int)Math.Floor(gridX * Scale);
            var y = (int)Math.Floor(gridY * Scale);
            using var fb = _bitmap.Lock();
            var bgra = Marshal.ReadInt32(fb.Address + y * fb.RowBytes + x * 4);
            var b = (byte)(bgra & 0xFF);
            var g = (byte)((bgra >> 8) & 0xFF);
            var r = (byte)((bgra >> 16) & 0xFF);
            var a = (byte)((bgra >> 24) & 0xFF);
            if (fb.Format == Avalonia.Platform.PixelFormat.Rgba8888) (r, b) = (b, r);
            return Color.FromArgb(a, r, g, b);
        }

        public void Dispose() => _bitmap.Dispose();
    }
}
