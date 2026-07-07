namespace OpenLogi.Core.DeviceInfo;

/// <summary>
/// HID++ DeviceInformation (feature 0x0003) snapshot used to identify a device
/// against external registries. Ported from Rust <c>device::DeviceModelInfo</c>.
/// </summary>
public sealed record DeviceModelInfo
{
    public required byte EntityCount { get; init; }
    public string? SerialNumber { get; init; }
    /// <summary>4-byte unit id.</summary>
    public required byte[] UnitId { get; init; }
    public required DeviceTransports Transports { get; init; }
    /// <summary>Per-transport PID array (3 entries).</summary>
    public required ushort[] ModelIds { get; init; }
    public required byte ExtendedModelId { get; init; }

    /// <summary>
    /// Stable identifier used to key per-device config and look up assets:
    /// <c>{ExtendedModelId:x}{ModelIds[0]:x4}</c> (e.g. <c>"2b042"</c>).
    /// </summary>
    public string ConfigKey() => $"{ExtendedModelId:x}{ModelIds[0]:x4}";

    public bool Equals(DeviceModelInfo? other) =>
        other is not null
        && EntityCount == other.EntityCount
        && SerialNumber == other.SerialNumber
        && UnitId.AsSpan().SequenceEqual(other.UnitId)
        && Transports == other.Transports
        && ModelIds.AsSpan().SequenceEqual(other.ModelIds)
        && ExtendedModelId == other.ExtendedModelId;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(EntityCount);
        hash.Add(SerialNumber);
        foreach (var b in UnitId) hash.Add(b);
        hash.Add(Transports);
        foreach (var m in ModelIds) hash.Add(m);
        hash.Add(ExtendedModelId);
        return hash.ToHashCode();
    }
}
