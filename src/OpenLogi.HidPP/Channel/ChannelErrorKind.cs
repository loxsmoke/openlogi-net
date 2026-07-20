namespace OpenLogi.HidPP.Channel;

/// <summary>The kind of a <see cref="ChannelException"/>. Mirrors Rust <c>channel::ChannelError</c>.</summary>
public enum ChannelErrorKind
{
    /// <summary>The raw channel implementation returned an error.</summary>
    Implementation,
    /// <summary>The HID channel does not support HID++.</summary>
    HidppNotSupported,
    /// <summary>The channel does not support the given message type (short/long).</summary>
    MessageTypeNotSupported,
    /// <summary>No response was received following a request.</summary>
    NoResponse,
    /// <summary>The request timed out before the device responded.</summary>
    Timeout,
}
