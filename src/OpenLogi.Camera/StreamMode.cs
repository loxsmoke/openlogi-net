namespace OpenLogi.Camera;

/// <summary>
/// One frame size and rate a camera can stream. Unlike brightness or zoom this is
/// not a register the camera holds: it is agreed every time a stream opens, so a
/// mode chosen here shapes OpenLogi's preview and nothing else. Ported from Rust
/// <c>openlogi_camera::stream_mode</c>.
/// </summary>
/// <param name="Fps">Whole frames per second (drivers report a rate ratio; see <see cref="StreamModes.WholeFps"/>).</param>
public readonly record struct StreamMode(uint Width, uint Height, uint Fps)
{
    public (uint Width, uint Height) Resolution => (Width, Height);

    public override string ToString() => $"{Width}x{Height} @ {Fps} fps";
}

/// <summary>
/// Every <see cref="StreamMode"/> a camera offers for the pixel formats the preview
/// converts, largest frame first and fastest rate first within a frame size. The
/// ordering is part of the contract: the UI lists resolutions and rates in it.
/// </summary>
public sealed class StreamModes
{
    /// <summary>The size a stream opens at when nothing else is asked for.</summary>
    internal static readonly (uint Width, uint Height) DefaultResolution = (1280, 720);

    /// <summary>The rate a stream opens at when nothing else is asked for.</summary>
    internal const uint DefaultFps = 30;

    private readonly List<StreamMode> _modes;

    public static StreamModes Empty { get; } = new([]);

    /// <summary>Collect <paramref name="modes"/>, dropping empty sizes and rates and duplicates, and order them.</summary>
    public StreamModes(IEnumerable<StreamMode> modes)
    {
        _modes = modes
            .Where(m => m.Width > 0 && m.Height > 0 && m.Fps > 0)
            .Distinct()
            .OrderByDescending(m => Area(m.Resolution))
            .ThenByDescending(m => m.Width)
            .ThenByDescending(m => m.Fps)
            .ToList();
    }

    public bool IsEmpty => _modes.Count == 0;

    public IReadOnlyList<StreamMode> All => _modes;

    public bool Contains(StreamMode mode) => _modes.Contains(mode);

    /// <summary>The distinct frame sizes on offer, largest first.</summary>
    public IReadOnlyList<(uint Width, uint Height)> Resolutions()
    {
        var sizes = new List<(uint, uint)>();
        foreach (var m in _modes)
            if (!sizes.Contains(m.Resolution))
                sizes.Add(m.Resolution);
        return sizes;
    }

    /// <summary>The frame rates on offer at <paramref name="resolution"/>, fastest first.</summary>
    public IReadOnlyList<uint> RatesAt((uint Width, uint Height) resolution) =>
        [.. _modes.Where(m => m.Resolution == resolution).Select(m => m.Fps)];

    /// <summary>
    /// The mode a stream opens at when nothing was asked for: the frame size nearest
    /// 1280×720 by pixel count (the larger on a tie), at the rate nearest 30 fps (the
    /// slower on a tie). <c>null</c> for an empty list.
    /// </summary>
    public StreamMode? DefaultMode()
    {
        var wanted = Area(DefaultResolution);
        var sizes = Resolutions();
        if (sizes.Count == 0) return null;
        var resolution = sizes
            .OrderBy(s => AbsDiff(Area(s), wanted))
            .ThenByDescending(Area)
            .First();
        return NearestRate(resolution, DefaultFps);
    }

    /// <summary>
    /// The mode a stream should actually open at for <paramref name="requested"/>:
    /// that mode when the camera offers it, else <see cref="DefaultMode"/>. A saved
    /// choice can outlive what the camera offers, so it is never trusted blindly.
    /// </summary>
    public StreamMode? Resolve(StreamMode? requested) =>
        requested is { } r && Contains(r) ? r : DefaultMode();

    /// <summary>
    /// The mode to move to when the user picks <paramref name="resolution"/> while
    /// streaming at <paramref name="current"/>: the same rate if the new size offers
    /// it, else the nearest one it does (the slower on a tie).
    /// </summary>
    public StreamMode? WithResolution(StreamMode current, (uint Width, uint Height) resolution) =>
        NearestRate(resolution, current.Fps);

    private StreamMode? NearestRate((uint Width, uint Height) resolution, uint fps)
    {
        StreamMode? best = null;
        foreach (var m in _modes)
        {
            if (m.Resolution != resolution) continue;
            if (best is not { } b
                || AbsDiff(m.Fps, fps) < AbsDiff(b.Fps, fps)
                || (AbsDiff(m.Fps, fps) == AbsDiff(b.Fps, fps) && m.Fps < b.Fps))
                best = m;
        }
        return best;
    }

    /// <summary>
    /// Whole frames per second for a rate of <paramref name="numerator"/> /
    /// <paramref name="denominator"/> fps: the nearest whole number, rounding half
    /// up (a UVC camera's 30 is often 10000000/333333, and 7.5 reads as 8).
    /// <c>null</c> for a zero denominator or a rate that rounds to zero.
    /// </summary>
    public static uint? WholeFps(uint numerator, uint denominator)
    {
        if (denominator == 0) return null;
        var fps = (2UL * numerator + denominator) / (2UL * denominator);
        return fps is > 0 and <= uint.MaxValue ? (uint)fps : null;
    }

    private static ulong Area((uint Width, uint Height) size) => (ulong)size.Width * size.Height;

    private static ulong AbsDiff(ulong a, ulong b) => a > b ? a - b : b - a;
}

/// <summary>
/// One native media type, reduced to what a mode names. Ported from Rust
/// <c>mf_formats.rs</c>: turns the Media Foundation native-type list into
/// <see cref="StreamModes"/>, and back.
/// </summary>
/// <param name="Index">The type's index for the reader's <c>GetNativeMediaType</c>.</param>
/// <param name="Fps">Whole frames per second; 0 when the type names no rate, which makes it no mode.</param>
internal readonly record struct NativeFormat(uint Index, uint Width, uint Height, uint Fps)
{
    public StreamMode Mode => new(Width, Height, Fps);

    /// <summary>Every mode <paramref name="formats"/> offer (one size+rate in several pixel formats is one mode).</summary>
    public static StreamModes Offered(IEnumerable<NativeFormat> formats) => new(formats.Select(f => f.Mode));

    /// <summary>
    /// The native type to select to stream at <paramref name="requested"/>: one
    /// offering that mode when the camera has it, else one offering the camera's
    /// default. The first the driver lists wins, so the pick is stable across opens.
    /// </summary>
    public static NativeFormat? Choose(IReadOnlyList<NativeFormat> formats, StreamMode? requested)
    {
        if (Offered(formats).Resolve(requested) is not { } mode) return null;
        foreach (var f in formats)
            if (f.Mode == mode)
                return f;
        return null;
    }
}
