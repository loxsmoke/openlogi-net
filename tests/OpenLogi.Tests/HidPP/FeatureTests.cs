using OpenLogi.HidPP;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Device;
using OpenLogi.HidPP.Feature;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.Tests.HidPP;

public class BcdTests
{
    [Fact]
    public void ConvertsPackedBytes()
    {
        Assert.Equal((byte)42, Bcd.ConvertPackedU8(0x42));
        Assert.Equal((byte)9, Bcd.ConvertPackedU8(0x09));
        Assert.Null(Bcd.ConvertPackedU8(0x0a));
        Assert.Null(Bcd.ConvertPackedU8(0xf0));
    }

    [Fact]
    public void ConvertsPackedWords()
    {
        Assert.Equal((ushort)1234, Bcd.ConvertPackedU16(0x1234));
        Assert.Null(Bcd.ConvertPackedU16(0x12a4));
    }
}

public class FeatureTypeTests
{
    [Fact]
    public void RoundTripsBitfield()
    {
        var t = new FeatureType(Obsolete: true, Hidden: false, Engineering: true,
            ManufacturingDeactivatable: false, ComplianceDeactivatable: true);
        Assert.Equal(t, FeatureType.FromByte(t.ToByte()));
        Assert.Equal((1 << 7) | (1 << 5) | (1 << 3), t.ToByte());
    }

    [Fact]
    public void ParsesIndividualFlags()
    {
        Assert.True(FeatureType.FromByte(0b1000_0000).Obsolete);
        Assert.True(FeatureType.FromByte(0b0100_0000).Hidden);
        Assert.True(FeatureType.FromByte(0b0010_0000).Engineering);
    }
}

/// <summary>Ported from the Rust <c>feature/mod.rs</c> event_payload tests.</summary>
public class EventPayloadTests
{
    private static HidppMessage Broadcast(byte device, byte feature, byte function, byte software)
    {
        var payload = Enumerable.Repeat((byte)0xab, 16).ToArray();
        return V20Message.Long(new V20MessageHeader(device, feature, U4.FromLo(function), U4.FromLo(software)), payload).ToHidpp();
    }

    [Fact]
    public void AcceptsMatchingBroadcastAndReturnsSubId()
    {
        var result = FeatureEndpoint.EventPayload(Broadcast(2, 5, 1, 0), false, 2, 5);
        Assert.NotNull(result);
        Assert.Equal((byte)1, result!.Value.SubId.ToLo());
        Assert.Equal(Enumerable.Repeat((byte)0xab, 16).ToArray(), result.Value.Payload);
    }

    [Fact]
    public void RejectsRequestMatchedReport() =>
        Assert.Null(FeatureEndpoint.EventPayload(Broadcast(2, 5, 0, 0), true, 2, 5));

    [Fact]
    public void RejectsOtherDeviceOrFeature()
    {
        Assert.Null(FeatureEndpoint.EventPayload(Broadcast(9, 5, 0, 0), false, 2, 5));
        Assert.Null(FeatureEndpoint.EventPayload(Broadcast(2, 9, 0, 0), false, 2, 5));
    }

    [Fact]
    public void GatesOnSoftwareIdOnlyNotSubId()
    {
        Assert.Null(FeatureEndpoint.EventPayload(Broadcast(2, 5, 0, 1), false, 2, 5));
        Assert.NotNull(FeatureEndpoint.EventPayload(Broadcast(2, 5, 7, 0), false, 2, 5));
    }
}

public class DeviceInformationParseTests
{
    [Fact]
    public void ParsesDeviceInfoPayload()
    {
        var p = new byte[16];
        p[0] = 5;                 // entity_count
        p[1] = 0xde; p[2] = 0xad; p[3] = 0xbe; p[4] = 0xef; // unit_id
        p[6] = 0b0000_1010;       // transport: usb (bit3) + btle (bit1)
        p[7] = 0xb0; p[8] = 0x42; // model_id[0] = 0xb042 (big-endian)
        p[13] = 0x02;             // extended_model_id
        p[14] = 0x01;             // capabilities: serial_number
        var info = DeviceInformation.Parse(p);
        Assert.Equal(5, info.EntityCount);
        Assert.Equal(new byte[] { 0xde, 0xad, 0xbe, 0xef }, info.UnitId);
        Assert.Equal(new DeviceTransport(Usb: true, EQuad: false, Btle: true, Bluetooth: false), info.Transport);
        Assert.Equal((ushort)0xb042, info.ModelId[0]);
        Assert.Equal(0x02, info.ExtendedModelId);
        Assert.True(info.Capabilities.SerialNumber);
    }

    [Fact]
    public void ParsesFirmwareInfoWithBcdDecoding()
    {
        var p = new byte[16];
        p[0] = (byte)DeviceEntityType.MainApplication;
        p[1] = (byte)'R'; p[2] = (byte)'B'; p[3] = (byte)'M';
        p[4] = 0x12;              // firmware_number BCD -> 12
        p[5] = 0x03;             // revision BCD -> 3
        p[6] = 0x00; p[7] = 0x27; // build BCD 0x0027 -> 27
        p[8] = 0x01;             // active
        var fw = DeviceEntityFirmwareInfo.Parse(p);
        Assert.Equal(DeviceEntityType.MainApplication, fw.EntityType);
        Assert.Equal("RBM", fw.FirmwarePrefix);
        Assert.Equal((byte)12, fw.FirmwareNumber);
        Assert.Equal((byte)3, fw.Revision);
        Assert.Equal((ushort)27, fw.Build);
        Assert.True(fw.Active);
    }

    [Fact]
    public void RejectsInvalidBcdFirmware()
    {
        var p = new byte[16];
        p[0] = (byte)DeviceEntityType.MainApplication;
        p[4] = 0xaa; // invalid BCD
        var ex = Assert.Throws<Hidpp20Exception>(() => DeviceEntityFirmwareInfo.Parse(p));
        Assert.Equal(Hidpp20ErrorKind.UnsupportedResponse, ex.Kind);
    }
}

/// <summary>End-to-end device init + feature enumeration over a scripted mock channel.</summary>
public class DeviceEnumerationTests
{
    [Fact]
    public async Task InitializesAndEnumeratesFeatures()
    {
        const byte deviceIndex = 0x02;
        const byte featureSetIndex = 0x02;
        var raw = new MockRawHidChannel
        {
            OnWrite = request =>
            {
                var msg = V20Message.FromHidpp(request);
                var h = msg.Header;
                byte[] Reply(params byte[] head)
                {
                    var payload = new byte[16];
                    head.CopyTo(payload.AsSpan());
                    return payload;
                }
                HidppMessage Respond(byte[] payload) => V20Message.Long(h, payload).ToHidpp();

                // Root feature (index 0).
                if (h.FeatureIndex == 0x00 && h.FunctionId.ToLo() == 0x1) // ping
                    return Respond(Reply(0x04, 0x00, msg.ExtendPayload()[2])); // protocol_num=4 (V20)
                if (h.FeatureIndex == 0x00 && h.FunctionId.ToLo() == 0x0) // root.get_feature(FeatureSet)
                    return Respond(Reply(featureSetIndex, 0x00, 0x02)); // index, type, version

                // FeatureSet feature (index 2).
                if (h.FeatureIndex == featureSetIndex && h.FunctionId.ToLo() == 0x0) // count
                    return Respond(Reply(0x03));
                if (h.FeatureIndex == featureSetIndex && h.FunctionId.ToLo() == 0x1) // get_feature(i)
                {
                    var i = msg.ExtendPayload()[0];
                    var id = i switch { 1 => (ushort)0x0003, 2 => (ushort)0x0001, _ => (ushort)0x1b04 };
                    return Respond(Reply((byte)(id >> 8), (byte)id, 0x00, 0x01));
                }
                return null;
            },
        };

        await using var channel = await HidppChannel.FromRawChannelAsync(raw);
        var device = await HidppDevice.NewAsync(channel, deviceIndex);
        Assert.IsType<ProtocolVersion.V20>(device.ProtocolVersion);

        var features = await device.EnumerateFeaturesAsync();
        Assert.NotNull(features);
        Assert.Equal(3, features!.Count);

        Assert.True(device.ProvidesFeature<DeviceInformationFeature>());
        Assert.Equal((byte)1, device.FeatureIndex(0x0003));
        Assert.NotNull(device.GetFeature<DeviceInformationFeature>());
        Assert.Null(device.GetFeature<DeviceTypeAndNameFeature>()); // 0x0005 not reported
    }
}
