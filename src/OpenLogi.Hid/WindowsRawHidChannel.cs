using HidSharp;
using HidSharp.Reports;
using OpenLogi.HidPP.Channel;

namespace OpenLogi.Hid;

/// <summary>
/// A <see cref="IRawHidChannel"/> backed by HidSharp, for the Windows HID stack.
/// Replaces the Rust <c>async-hid</c> transport (<c>openlogi-hid::transport</c> +
/// <c>windows_hid.rs</c>); the multi-method write fallback there is a future
/// hardening step — this uses HidSharp's stream write.
/// </summary>
public sealed class WindowsRawHidChannel : IRawHidChannel, IDisposable
{
    private readonly HidStream _stream;
    private readonly int _maxInput;
    private readonly int _maxOutput;
    private readonly (bool SupportsShort, bool SupportsLong)? _support;

    public ushort VendorId { get; }
    public ushort ProductId { get; }

    private WindowsRawHidChannel(HidDevice device, HidStream stream, (bool, bool)? support)
    {
        _stream = stream;
        _stream.ReadTimeout = 250;
        _stream.WriteTimeout = 1000;
        _maxInput = Math.Max(device.GetMaxInputReportLength(), HidppMessage.LongReportLength);
        // Windows rejects HID writes that aren't exactly the interface's output
        // report length (Win32 error 87). Do NOT floor this to the long-report
        // size: LIGHTSPEED receivers expose a short-only interface (7-byte
        // reports) where a padded 20-byte write fails — HARDWARE-VERIFIED on a
        // G915 dongle (046d:c547). Combined Bolt/Unifying interfaces report 20
        // here, so their behavior is unchanged.
        _maxOutput = device.GetMaxOutputReportLength();
        _support = support;
        VendorId = (ushort)device.VendorID;
        ProductId = (ushort)device.ProductID;
    }

    /// <summary>Open a HID device into a raw channel.</summary>
    public static WindowsRawHidChannel Open(HidDevice device)
    {
        var support = DetectSupport(device);
        var stream = device.Open();
        return new WindowsRawHidChannel(device, stream, support);
    }

    public Task<int> WriteReportAsync(ReadOnlyMemory<byte> src)
    {
        var buffer = new byte[Math.Max(src.Length, _maxOutput)];
        src.CopyTo(buffer);
        _stream.Write(buffer, 0, buffer.Length);
        return Task.FromResult(src.Length);
    }

    public Task<int> ReadReportAsync(Memory<byte> buf, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var temp = new byte[_maxInput];
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var n = _stream.Read(temp, 0, temp.Length);
                    var len = Math.Min(n, buf.Length);
                    temp.AsSpan(0, len).CopyTo(buf.Span);
                    return len;
                }
                catch (TimeoutException)
                {
                    // Transient: loop so cancellation is observed promptly.
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }, cancellationToken);

    public (bool SupportsShort, bool SupportsLong)? SupportsShortLongHidpp() => _support;

    public Task<int> GetReportDescriptorAsync(Memory<byte> buf) =>
        throw new NotSupportedException("descriptor is parsed eagerly in SupportsShortLongHidpp");

    public void Dispose() => _stream.Dispose();

    /// <summary>
    /// Detect HID++ short (report id 0x10) / long (0x11) support from the device's
    /// report descriptor, falling back to the max input report length if the
    /// descriptor can't be parsed. Returns <c>null</c> only when neither is present.
    /// </summary>
    public static (bool SupportsShort, bool SupportsLong)? DetectSupport(HidDevice device)
    {
        bool shortSup = false, longSup = false;
        try
        {
            var descriptor = device.GetReportDescriptor();
            foreach (var report in descriptor.Reports)
            {
                if (report.ReportType != ReportType.Input) continue;
                if (report.ReportID == HidppMessage.ShortReportId) shortSup = true;
                if (report.ReportID == HidppMessage.LongReportId) longSup = true;
            }
        }
        catch
        {
            var max = device.GetMaxInputReportLength();
            shortSup = max >= HidppMessage.ShortReportLength;
            longSup = max >= HidppMessage.LongReportLength;
        }
        return shortSup || longSup ? (shortSup, longSup) : null;
    }
}
