namespace OpenLogi.Core.DeviceInfo;

public sealed record ReceiverInfo
{
    public required string Name { get; init; }
    public required ushort VendorId { get; init; }
    public required ushort ProductId { get; init; }
    public string? UniqueId { get; init; }
}
