using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.HidPP.Feature;

/// <summary>Host-management capabilities for HostsInfo (0x1815).</summary>
[Flags]
public enum HostsInfoCapabilities : byte
{
    None = 0,
    GetName = 1 << 0,
    SetName = 1 << 1,
    MoveHost = 1 << 2,
    DeleteHost = 1 << 3,
    SetOsVersion = 1 << 4,
}

/// <summary>Supported host descriptor families.</summary>
[Flags]
public enum HostDescriptorCapabilities : byte
{
    None = 0,
    Equad = 1 << 0,
    Usb = 1 << 1,
    Bt = 1 << 2,
    Ble = 1 << 3,
}

/// <summary>Pairing status for a host slot.</summary>
public enum HostSlotStatus : byte { Empty = 0, Paired = 1 }

/// <summary>Bus type associated with a host slot.</summary>
public enum HostBusType : byte { Undefined = 0, Equad = 1, Usb = 2, Bt = 3, Ble = 4, BlePro = 5 }

/// <summary>A host slot selector: the current slot, or a zero-based index.</summary>
public abstract record HostIndex
{
    private HostIndex() { }
    public sealed record Current : HostIndex;
    public sealed record Slot(byte Index) : HostIndex;

    public static readonly Current CurrentValue = new();

    public byte ToByte() => this is Slot s ? s.Index : (byte)0xff;
    public static HostIndex FromByte(byte value) => value == 0xff ? CurrentValue : new Slot(value);
}

/// <summary>Static information about the HostsInfo feature.</summary>
public readonly record struct HostsInfoFeatureInfo(
    HostsInfoCapabilities Capabilities, HostDescriptorCapabilities DescriptorCapabilities, byte HostCount, HostIndex CurrentHost);

/// <summary>Information about one host slot.</summary>
public readonly record struct HostInfo(
    HostIndex HostIndex, HostSlotStatus Status, HostBusType BusType, byte PageCount, byte NameLen, byte NameMaxLen);

/// <summary>Raw host descriptor page.</summary>
public readonly record struct HostDescriptorPage(HostIndex HostIndex, HostBusType BusType, byte PageIndex, byte[] Body);

/// <summary>The `HostsInfo` / 0x1815 feature. Ported from Rust <c>feature::hosts_info</c>.</summary>
public sealed class HostsInfoFeature(FeatureEndpoint endpoint) : ICreatableFeature<HostsInfoFeature>
{
    public static ushort Id => 0x1815;
    public static byte StartingVersion => 2;
    public static HostsInfoFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(new FeatureEndpoint(channel, deviceIndex, featureIndex));

    /// <summary>Feature capabilities and host-slot count.</summary>
    public async Task<HostsInfoFeatureInfo> GetFeatureInfoAsync()
    {
        var p = (await endpoint.CallAsync(0, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload();
        return new HostsInfoFeatureInfo(
            (HostsInfoCapabilities)p[0], (HostDescriptorCapabilities)p[1], p[2], HostIndex.FromByte(p[3]));
    }

    /// <summary>Information for <paramref name="host"/>.</summary>
    public async Task<HostInfo> GetHostInfoAsync(HostIndex host)
    {
        var p = (await endpoint.CallAsync(1, [host.ToByte(), 0, 0]).ConfigureAwait(false)).ExtendPayload();
        return new HostInfo(
            HostIndex.FromByte(p[0]),
            ParseEnum<HostSlotStatus>(p[1]),
            ParseEnum<HostBusType>(p[2]),
            p[3], p[4], p[5]);
    }

    /// <summary>
    /// Read the host's friendly name (the name the paired computer stored on the
    /// device), in 13-byte chunks. Returns an empty string when unset.
    /// </summary>
    public async Task<string> GetHostFriendlyNameAsync(HostIndex host, byte nameLen)
    {
        if (nameLen == 0) return "";
        var bytes = new List<byte>(nameLen);
        byte byteIndex = 0;
        while (byteIndex < nameLen)
        {
            var p = (await endpoint.CallAsync(3, [host.ToByte(), byteIndex, 0]).ConfigureAwait(false)).ExtendPayload();
            // payload: [0]=hostIndex, [1]=byteIndex, [2..16]=name chunk (14 bytes)
            var chunkLen = Math.Min(14, nameLen - byteIndex);
            if (chunkLen <= 0) break;
            bytes.AddRange(p[2..(2 + chunkLen)]);
            byteIndex += (byte)chunkLen;
        }
        return System.Text.Encoding.UTF8.GetString([.. bytes]).TrimEnd('\0').Trim();
    }

    /// <summary>A raw descriptor <paramref name="page"/> for <paramref name="host"/>.</summary>
    public async Task<HostDescriptorPage> GetHostDescriptorAsync(HostIndex host, byte page)
    {
        var p = (await endpoint.CallAsync(2, [host.ToByte(), page, 0]).ConfigureAwait(false)).ExtendPayload();
        return new HostDescriptorPage(
            HostIndex.FromByte(p[0]),
            ParseEnum<HostBusType>((byte)(p[1] >> 4)),
            (byte)(p[1] & 0x0f),
            p[2..16]);
    }

    /// <summary>
    /// Forget the pairing stored in <paramref name="host"/> (EasySwitch "clear
    /// host"), freeing the slot. Only valid when the feature reports
    /// <see cref="HostsInfoCapabilities.DeleteHost"/>; the device refuses to
    /// delete the host it is currently connected through.
    /// </summary>
    public async Task DeleteHostAsync(HostIndex host) =>
        await endpoint.CallAsync(6, [host.ToByte(), 0, 0]).ConfigureAwait(false);

    private static T ParseEnum<T>(byte raw) where T : struct, Enum =>
        Enum.IsDefined(typeof(T), raw) ? (T)Enum.ToObject(typeof(T), raw) : throw Hidpp20Exception.UnsupportedResponse();
}
