namespace OpenLogi.HidPP.Protocol;

/// <summary>The protocol version a device supports. Ported from Rust <c>protocol::ProtocolVersion</c>.</summary>
public abstract record ProtocolVersion
{
    private ProtocolVersion() { }

    /// <summary>The older HID++1.0 protocol, mostly used for receivers.</summary>
    public sealed record V10 : ProtocolVersion
    {
        public static readonly V10 Instance = new();
    }

    /// <summary>HID++2.0 and newer. The two version bytes now hint which host software to target.</summary>
    public sealed record V20(byte ProtocolNum, byte TargetSw) : ProtocolVersion;
}
