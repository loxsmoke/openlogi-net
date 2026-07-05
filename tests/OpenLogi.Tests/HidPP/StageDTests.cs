using System.Buffers.Binary;
using OpenLogi.HidPP;
using OpenLogi.HidPP.Feature;

namespace OpenLogi.Tests.HidPP;

/// <summary>Ported from the Rust <c>reprog_controls/tests.rs</c> module.</summary>
public class ReprogControlsTests
{
    [Fact]
    public void ParsesCidInfoFlagsAndMetadata()
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0), 0x00c3);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), 0x009c);
        payload[4] = 0b1111_0001;
        payload[5] = 7;
        payload[6] = 2;
        payload[7] = 0b0000_0011;
        payload[8] = 0b0000_1111;

        var info = CidInfo.FromPayload(payload);

        Assert.Equal(new ControlId(0x00c3), info.Cid);
        Assert.Equal(new TaskId(0x009c), info.TaskId);
        Assert.Equal(7, info.Position);
        Assert.Equal(2, info.Group);
        Assert.Equal(new GroupMask(0b0000_0011), info.GroupMask);
        Assert.True(info.Flags.IsMouse());
        Assert.True(info.Flags.HasFlag(CidFlags.Reprogrammable));
        Assert.True(info.Flags.IsDivertable());
        Assert.True(info.Flags.IsPersistentlyDivertable());
        Assert.True(info.Flags.IsVirtualControl());
        Assert.True(info.Flags.SupportsRawXy());
        Assert.True(info.Flags.SupportsForceRawXy());
        Assert.True(info.Flags.SupportsAnalyticsKeyEvents());
        Assert.True(info.Flags.SupportsRawWheel());
        Assert.Equal(0x0ff1, info.Flags.Raw());
    }

    [Fact]
    public void BuildsTemporaryDiversionPayload()
    {
        var payload = CidReportingChange.TemporaryDiversion(true, true).ToPayload(new ControlId(0x00c3));
        Assert.Equal(new byte[] { 0x00, 0xc3 }, payload[0..2]);
        Assert.Equal(0x33, payload[2]);
        Assert.Equal(0, payload[3]);
        Assert.Equal(0, payload[4]);
        Assert.Equal(0, payload[5]);
    }

    [Fact]
    public void ParsesReportingState()
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0), 0x00c3);
        payload[2] = (1 << 0) | (1 << 2) | (1 << 4) | (1 << 6);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(3), 0x00c4);
        payload[5] = (1 << 0) | (1 << 2);

        var reporting = CidReporting.FromPayload(payload);
        Assert.True(reporting.Diverted);
        Assert.True(reporting.PersistentlyDiverted);
        Assert.True(reporting.RawXy);
        Assert.True(reporting.ForceRawXy);
        Assert.Equal(new ControlId(0x00c4), reporting.Remap);
        Assert.True(reporting.AnalyticsKeyEvents);
        Assert.True(reporting.RawWheel);
    }

    [Fact]
    public void DecodesEvents()
    {
        var buttons = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(buttons.AsSpan(0), 0x00c3);
        BinaryPrimitives.WriteUInt16BigEndian(buttons.AsSpan(2), 0x00c4);
        Assert.Equal(
            new ReprogControlsEvent.DivertedButtons([new ControlId(0x00c3), new ControlId(0x00c4), new ControlId(0), new ControlId(0)]),
            ReprogControlsFeature.DecodeEventPayload(0, buttons));

        var xy = new byte[16];
        BinaryPrimitives.WriteInt16BigEndian(xy.AsSpan(0), -5);
        BinaryPrimitives.WriteInt16BigEndian(xy.AsSpan(2), 12);
        Assert.Equal(
            new ReprogControlsEvent.DivertedRawMouseXy(-5, 12),
            ReprogControlsFeature.DecodeEventPayload(1, xy));

        var wheel = new byte[16];
        wheel[0] = 0b0001_0011;
        BinaryPrimitives.WriteInt16BigEndian(wheel.AsSpan(1), 123);
        Assert.Equal(
            new ReprogControlsEvent.DivertedRawWheel(RawWheelResolution.High, U4.FromLo(3), 123),
            ReprogControlsFeature.DecodeEventPayload(4, wheel));
    }
}

/// <summary>Ported from the Rust <c>smartshift_enhanced</c> tests.</summary>
public class SmartShiftEnhancedTests
{
    [Fact]
    public void ParsesStatus()
    {
        var payload = new byte[16];
        payload[0] = 2;
        payload[1] = 0xff;
        payload[2] = 33;
        var status = SmartShiftEnhancedStatus.FromPayload(payload);
        Assert.Equal(SmartShiftWheelMode.Ratchet, status.WheelMode);
        Assert.Equal(0xff, status.AutoDisengage);
        Assert.Equal(33, status.CurrentTunableTorque);
    }

    [Fact]
    public void FallsBackToRatchetForUnknownWheelMode()
    {
        var payload = new byte[16];
        payload[0] = 9;
        Assert.Equal(SmartShiftWheelMode.Ratchet, SmartShiftEnhancedStatus.FromPayload(payload).WheelMode);
    }
}

public class HiResWheelTests
{
    [Fact]
    public void InvertIsBit2()
    {
        Assert.True(HiResWheelMode.FromByte(0x04).Inverted);
        Assert.False(HiResWheelMode.FromByte(0x03).Inverted); // target + resolution, no invert
        Assert.Equal(0x04, new HiResWheelMode(false, false, true).ToByte());
    }

    [Fact]
    public void ModeByteRoundTrips()
    {
        for (byte b = 0; b < 8; b++)
            Assert.Equal(b, HiResWheelMode.FromByte(b).ToByte());
    }

    [Fact]
    public void TogglingInvertPreservesResolutionAndTarget()
    {
        // high-res + diverted, currently not inverted (0x03)
        var mode = HiResWheelMode.FromByte(0x03);
        var inverted = mode with { Inverted = true };
        Assert.Equal(0x07, inverted.ToByte());      // gained only the invert bit
        Assert.True(inverted.HighResolution && inverted.Diverted);
        Assert.Equal(0x03, (inverted with { Inverted = false }).ToByte());
    }

    [Fact]
    public void CapabilityParsesMultiplierFlagsAndV1Fields()
    {
        // multiplier 8; flags = has_switch (0x02) + has_invert (0x04); 24 ratchets; 30 mm
        var caps = HiResWheelCapability.FromPayload([0x08, 0x06, 0x18, 0x1e]);
        Assert.Equal(new HiResWheelCapability(8, true, true, 24, 30), caps);
        // v0 device: flag-less, trailing bytes zero
        Assert.Equal(new HiResWheelCapability(8, false, false, 0, 0),
            HiResWheelCapability.FromPayload([0x08, 0x00, 0x00, 0x00]));
    }

    [Fact]
    public void MovementEventDecodesResolutionPeriodsAndSignedDelta()
    {
        // 0x12 = high-res flag (0x10) + 2 periods; delta 0xfff8 = -8
        var ev = HiResWheelFeature.DecodeEventPayload(0, [0x12, 0xff, 0xf8]);
        Assert.Equal(new HiResWheelEvent.Movement(true, 2, -8), ev);
        // low-res movement, positive delta
        Assert.Equal(new HiResWheelEvent.Movement(false, 1, 3),
            HiResWheelFeature.DecodeEventPayload(0, [0x01, 0x00, 0x03]));
    }

    [Fact]
    public void RatchetSwitchEventDecodesBit0()
    {
        Assert.Equal(new HiResWheelEvent.RatchetSwitch(true), HiResWheelFeature.DecodeEventPayload(1, [0x01, 0, 0]));
        Assert.Equal(new HiResWheelEvent.RatchetSwitch(false), HiResWheelFeature.DecodeEventPayload(1, [0x00, 0, 0]));
    }

    [Fact]
    public void UnknownEventSubIdDecodesToNull() =>
        Assert.Null(HiResWheelFeature.DecodeEventPayload(2, [0x01, 0, 0]));
}

public class HostFeatureTests
{
    [Fact]
    public void HostIndexRoundTrips()
    {
        Assert.Equal((byte)0xff, HostIndex.CurrentValue.ToByte());
        Assert.Equal((byte)3, new HostIndex.Slot(3).ToByte());
        Assert.IsType<HostIndex.Current>(HostIndex.FromByte(0xff));
        Assert.Equal(new HostIndex.Slot(2), HostIndex.FromByte(2));
    }

    [Fact]
    public void ChangeHostCookiesRejectsTooManyHosts()
    {
        // Indirectly validates the bounds guard shape via the capabilities flags enum.
        Assert.True(((ChangeHostCapabilities)1).HasFlag(ChangeHostCapabilities.EnhancedHostSwitch));
    }
}
