namespace OpenLogi.HidPP.Channel;

/// <summary>An error creating or interacting with a HID(++) channel.</summary>
public sealed class ChannelException(ChannelErrorKind kind, string? message = null, Exception? inner = null)
    : Exception(message ?? kind.ToString(), inner)
{
    public ChannelErrorKind Kind { get; } = kind;
}
