namespace OpenLogi.HidPP.Channel;

/// <summary>
/// An arbitrary readable + writable HID communication channel with async I/O.
/// Ported from Rust <c>channel::RawHidChannel</c>. The OpenLogi.Hid layer
/// (section 4) provides the Windows implementation.
/// </summary>
public interface IRawHidChannel
{
    /// <summary>Vendor ID of the connected HID device.</summary>
    ushort VendorId { get; }

    /// <summary>Product ID of the connected HID device.</summary>
    ushort ProductId { get; }

    /// <summary>Write a raw report; returns the number of bytes written.</summary>
    Task<int> WriteReportAsync(ReadOnlyMemory<byte> src);

    /// <summary>
    /// Read a raw report into <paramref name="buf"/>; returns bytes read. Blocks
    /// until a report arrives: an idle channel must not throw. Cancellation (the
    /// channel being disposed) must unblock the read promptly and surface as an
    /// <see cref="OperationCanceledException"/>. Any other exception thrown here is
    /// treated by the read loop as a failed read and retried with exponential
    /// backoff — a stale handle (device asleep, unplugged, or re-enumerated) fails
    /// instantly on every call, so the loop must never retry it hot — and after
    /// sustained failure the loop raises <see cref="HidppChannel.ReadsDead"/>.
    /// </summary>
    Task<int> ReadReportAsync(Memory<byte> buf, CancellationToken cancellationToken);

    /// <summary>
    /// If the implementation already knows whether HID++ short/long reports are
    /// supported, return <c>(supportsShort, supportsLong)</c>; otherwise
    /// <c>null</c> and the report descriptor will be parsed instead.
    /// </summary>
    (bool SupportsShort, bool SupportsLong)? SupportsShortLongHidpp();

    /// <summary>Retrieve the raw HID report descriptor into <paramref name="buf"/>; returns its size.</summary>
    Task<int> GetReportDescriptorAsync(Memory<byte> buf);
}
