namespace OpenLogi.Core.DeviceInfo;

/// <summary>One receiver and its paired devices — the unit of an inventory snapshot.</summary>
public sealed record DeviceInventory
{
    public required ReceiverInfo Receiver { get; init; }
    public required IReadOnlyList<PairedDevice> Paired { get; init; }
}
