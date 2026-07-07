namespace OpenLogi.Core.Config;

/// <summary>
/// Per-device SmartShift wheel config, re-applied on reconnect (values live in
/// device RAM and reset on power cycle). Ported from Rust <c>config::SmartShift</c>.
/// </summary>
public sealed record SmartShift
{
    public required WheelMode Mode { get; init; }
    /// <summary>Auto-disengage threshold (0x01–0xFE), or 0xFF for a permanent ratchet.</summary>
    public required byte AutoDisengage { get; init; }
    /// <summary>Tunable-torque force percent (1–100); 0 when unsupported.</summary>
    public required byte TunableTorque { get; init; }
}
