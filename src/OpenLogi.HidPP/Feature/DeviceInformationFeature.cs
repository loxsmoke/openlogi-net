using System.Buffers.Binary;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.HidPP.Feature;

/// <summary>Which transport protocols a device supports (up to three). Ported from Rust <c>DeviceTransport</c>.</summary>
public readonly record struct DeviceTransport(bool Usb, bool EQuad, bool Btle, bool Bluetooth)
{
    public static DeviceTransport FromByte(byte value) => new(
        Usb: (value & (1 << 3)) != 0,
        EQuad: (value & (1 << 2)) != 0,
        Btle: (value & (1 << 1)) != 0,
        Bluetooth: (value & 1) != 0);
}

/// <summary>Additional capability flags of the DeviceInformation feature.</summary>
public readonly record struct DeviceInformationCapabilities(bool SerialNumber)
{
    public static DeviceInformationCapabilities FromByte(byte value) => new((value & 1) != 0);
}

/// <summary>The type of a device firmware entity.</summary>
public enum DeviceEntityType : byte
{
    MainApplication = 0, Bootloader = 1, Hardware = 2, Touchpad = 3, OpticalSensor = 4,
    Softdevice = 5, RfCompanionMcu = 6, FactoryApplication = 7, RgbCustomEffect = 8, MotorDrive = 9,
}

/// <summary>General device information from feature 0x0003. Ported from Rust <c>DeviceInformation</c>.</summary>
public sealed record DeviceInformation
{
    public required byte EntityCount { get; init; }
    public required byte[] UnitId { get; init; }
    public required DeviceTransport Transport { get; init; }
    public required ushort[] ModelId { get; init; }
    public required byte ExtendedModelId { get; init; }
    public required DeviceInformationCapabilities Capabilities { get; init; }

    /// <summary>Parse the 16-byte response payload of the get-device-info function.</summary>
    public static DeviceInformation Parse(ReadOnlySpan<byte> p) => new()
    {
        EntityCount = p[0],
        UnitId = p[1..5].ToArray(),
        Transport = DeviceTransport.FromByte(p[6]),
        ModelId =
        [
            BinaryPrimitives.ReadUInt16BigEndian(p[7..9]),
            BinaryPrimitives.ReadUInt16BigEndian(p[9..11]),
            BinaryPrimitives.ReadUInt16BigEndian(p[11..13]),
        ],
        ExtendedModelId = p[13],
        Capabilities = DeviceInformationCapabilities.FromByte(p[14]),
    };
}

/// <summary>Firmware info for one device entity. Ported from Rust <c>DeviceEntityFirmwareInfo</c>.</summary>
public sealed record DeviceEntityFirmwareInfo
{
    public required DeviceEntityType EntityType { get; init; }
    public required string FirmwarePrefix { get; init; }
    public required byte FirmwareNumber { get; init; }
    public required byte Revision { get; init; }
    public required ushort Build { get; init; }
    public required bool Active { get; init; }
    public required ushort TransportPid { get; init; }
    public required byte[] ExtraVersion { get; init; }

    /// <summary>Parse the response payload; throws <see cref="Hidpp20Exception"/> on bad BCD / UTF-8 / entity type.</summary>
    public static DeviceEntityFirmwareInfo Parse(ReadOnlySpan<byte> p)
    {
        if (!Enum.IsDefined(typeof(DeviceEntityType), p[0]))
            throw Hidpp20Exception.UnsupportedResponse();
        var number = Bcd.ConvertPackedU8(p[4]) ?? throw Hidpp20Exception.UnsupportedResponse();
        var revision = Bcd.ConvertPackedU8(p[5]) ?? throw Hidpp20Exception.UnsupportedResponse();
        var build = Bcd.ConvertPackedU16(BinaryPrimitives.ReadUInt16BigEndian(p[6..8])) ?? throw Hidpp20Exception.UnsupportedResponse();
        return new DeviceEntityFirmwareInfo
        {
            EntityType = (DeviceEntityType)p[0],
            FirmwarePrefix = System.Text.Encoding.ASCII.GetString(p[1..4]),
            FirmwareNumber = number,
            Revision = revision,
            Build = build,
            Active = (p[8] & 1) != 0,
            TransportPid = BinaryPrimitives.ReadUInt16BigEndian(p[9..11]),
            ExtraVersion = p[11..16].ToArray(),
        };
    }
}

/// <summary>The `DeviceInformation` / 0x0003 feature. Ported from Rust <c>feature::device_information</c>.</summary>
public sealed class DeviceInformationFeature(FeatureEndpoint endpoint) : ICreatableFeature<DeviceInformationFeature>
{
    public static ushort Id => 0x0003;
    public static byte StartingVersion => 0;
    public static DeviceInformationFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(new FeatureEndpoint(channel, deviceIndex, featureIndex));

    /// <summary>Retrieve general device information and capabilities.</summary>
    public async Task<DeviceInformation> GetDeviceInfoAsync() =>
        DeviceInformation.Parse((await endpoint.CallAsync(0, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload());

    /// <summary>Retrieve firmware info for an entity (index bounded by <see cref="DeviceInformation.EntityCount"/>).</summary>
    public async Task<DeviceEntityFirmwareInfo> GetFwInfoAsync(byte entityIndex) =>
        DeviceEntityFirmwareInfo.Parse((await endpoint.CallAsync(1, [entityIndex, 0x00, 0x00]).ConfigureAwait(false)).ExtendPayload());

    /// <summary>Retrieve the device serial number (feature version 4+; verify the capability first).</summary>
    public async Task<string> GetSerialNumberAsync()
    {
        var payload = (await endpoint.CallAsync(2, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload();
        return System.Text.Encoding.ASCII.GetString(payload[..12]);
    }
}
