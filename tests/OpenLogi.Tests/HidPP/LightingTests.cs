using OpenLogi.HidPP;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Feature;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.Tests.HidPP;

public class ColorLedEffectsTests
{
    [Fact]
    public async Task SetZoneEffectBuildsFixedColorRequest()
    {
        var raw = new MockRawHidChannel
        {
            // Echo the request header so SendV20Async resolves.
            OnWrite = req => V20Message.Long(V20Message.FromHidpp(req).Header, new byte[16]).ToHidpp(),
        };
        await using var channel = await HidppChannel.FromRawChannelAsync(raw);
        var led = ColorLedEffectsFeature.Create(channel, deviceIndex: 0x02, featureIndex: 0x05);

        var paramsBytes = new byte[ColorLedEffectsFeature.ZoneEffectParamCount];
        paramsBytes[0] = 0xAA; paramsBytes[1] = 0xBB; paramsBytes[2] = 0xCC; // R, G, B
        await led.SetZoneEffectAsync(zone: 1, ColorLedEffectsFeature.EffectFixedColor, paramsBytes,
            ColorLedEffectsFeature.PersistenceVolatile);

        var written = raw.WrittenReports()[0];
        Assert.Equal(HidppMessage.LongReportId, written[0]); // long report
        Assert.Equal(0x02, written[1]);                      // device index
        Assert.Equal(0x05, written[2]);                      // feature index
        Assert.Equal(U4.Combine(U4.FromLo(3), U4.FromLo(1)), written[3]); // function 3 | sw 1

        // v20 payload begins at written[4]: zone, effectIndex, R, G, B, …, persistence at +12.
        Assert.Equal(1, written[4]);     // zone
        Assert.Equal(1, written[5]);     // FixedColor effect
        Assert.Equal(0xAA, written[6]);  // R
        Assert.Equal(0xBB, written[7]);  // G
        Assert.Equal(0xCC, written[8]);  // B
        Assert.Equal(0, written[16]);    // persistence = Volatile (args[12])
    }
}
