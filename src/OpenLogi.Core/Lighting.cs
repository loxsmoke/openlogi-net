namespace OpenLogi.Core;

/// <summary>Per-device RGB lighting: a single static color, brightness, on/off.</summary>
public sealed record Lighting
{
    public bool Enabled { get; init; } = true;
    /// <summary>Static color as 6 hex digits "RRGGBB" (no leading '#').</summary>
    public string Color { get; init; } = "ffffff";

    private readonly byte _brightness = 100;
    /// <summary>Brightness percent, clamped to 0–100.</summary>
    public byte Brightness
    {
        get => _brightness;
        init => _brightness = Math.Min(value, (byte)100);
    }

    /// <summary>
    /// Per-key color overrides for the press-to-color editor: LED zone → "RRGGBB"
    /// hex. Empty when no keys are painted; only painted keys are stored.
    /// </summary>
    public IReadOnlyDictionary<byte, string> PerKey { get; init; } = EmptyPerKey;

    /// <summary>The press-to-color editor's last-used brush color as "RRGGBB" hex; null until set.</summary>
    public string? PaintColor { get; init; }

    /// <summary>
    /// The lighting source the user last selected: <c>0</c> = "No profile" (app-driven
    /// host-mode custom colour / per-key), <c>N ≥ 1</c> = onboard profile N. Persisted so
    /// the "No profile" custom colours can be re-applied after the keyboard sleeps (host
    /// mode is volatile and the firmware reverts to onboard profile 1 on wake).
    /// </summary>
    public int Profile { get; init; }

    private static readonly IReadOnlyDictionary<byte, string> EmptyPerKey = new Dictionary<byte, string>();

    // The compiler-generated record equality compares PerKey by reference; override
    // so two equal maps (incl. two empty ones — see the roundtrip tests) compare equal.
    public bool Equals(Lighting? other) =>
        other is not null
        && Enabled == other.Enabled
        && Color == other.Color
        && _brightness == other._brightness
        && PaintColor == other.PaintColor
        && Profile == other.Profile
        && PerKeyEquals(PerKey, other.PerKey);

    public override int GetHashCode() => HashCode.Combine(Enabled, Color, _brightness, PaintColor, Profile, PerKey.Count);

    private static bool PerKeyEquals(IReadOnlyDictionary<byte, string> a, IReadOnlyDictionary<byte, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (zone, hex) in a)
            if (!b.TryGetValue(zone, out var other) || other != hex) return false;
        return true;
    }
}
