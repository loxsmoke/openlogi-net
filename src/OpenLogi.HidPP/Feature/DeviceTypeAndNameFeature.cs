using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.HidPP.Feature;

/// <summary>The marketing type of a HID++2.0 device. Ported from Rust <c>DeviceType</c>.</summary>
public enum DeviceType : byte
{
    Keyboard = 0, RemoteControl = 1, Numpad = 2, Mouse = 3, Trackpad = 4, Trackball = 5,
    Presenter = 6, Receiver = 7, Headset = 8, Webcam = 9, SteeringWheel = 10, Joystick = 11,
    Gamepad = 12, Dock = 13, Speaker = 14, Microphone = 15, IlluminationLight = 16,
    ProgrammableController = 17, CarSimPedals = 18, Adapter = 19,
}

/// <summary>The `DeviceTypeAndName` / 0x0005 feature. Ported from Rust <c>feature::device_type_and_name</c>.</summary>
public sealed class DeviceTypeAndNameFeature(FeatureEndpoint endpoint) : ICreatableFeature<DeviceTypeAndNameFeature>
{
    public static ushort Id => 0x0005;
    public static byte StartingVersion => 0;
    public static DeviceTypeAndNameFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(new FeatureEndpoint(channel, deviceIndex, featureIndex));

    /// <summary>The number of characters in the device's marketing name.</summary>
    public async Task<byte> GetDeviceNameCountAsync() =>
        (await endpoint.CallAsync(0, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload()[0];

    /// <summary>A chunk of marketing-name bytes starting at <paramref name="index"/> (3 or 16 bytes depending on channel width).</summary>
    public async Task<byte[]> GetDeviceNameAsync(byte index)
    {
        var response = await endpoint.CallAsync(1, [index, 0x00, 0x00]).ConfigureAwait(false);
        // The Rust returns the raw (un-extended) payload — 3 bytes for short, 16 for long.
        var width = response.Kind == HidppReportKind.Short ? V20Message.ShortPayloadLength : V20Message.LongPayloadLength;
        return response.ExtendPayload()[..width];
    }

    /// <summary>Retrieve the whole marketing name by chunked reads, trimming trailing NULs.</summary>
    public async Task<string> GetWholeDeviceNameAsync()
    {
        var count = await GetDeviceNameCountAsync().ConfigureAwait(false);
        var sb = new System.Text.StringBuilder(count);
        var len = 0;
        while (len < count)
        {
            var part = await GetDeviceNameAsync((byte)len).ConfigureAwait(false);
            var text = System.Text.Encoding.ASCII.GetString(part);
            sb.Append(text);
            len = System.Text.Encoding.ASCII.GetByteCount(sb.ToString());
        }
        return sb.ToString().TrimEnd('\0');
    }

    /// <summary>Retrieve the device's marketing type.</summary>
    public async Task<DeviceType> GetDeviceTypeAsync()
    {
        var raw = (await endpoint.CallAsync(2, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload()[0];
        if (!Enum.IsDefined(typeof(DeviceType), raw))
            throw Hidpp20Exception.UnsupportedResponse();
        return (DeviceType)raw;
    }
}
