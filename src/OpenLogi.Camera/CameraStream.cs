using System.Runtime.InteropServices;
using OpenLogi.Camera.Interop;

namespace OpenLogi.Camera;

/// <summary>
/// A live Media Foundation capture stream. Ported from Rust <c>capture_windows.rs</c>.
///
/// A dedicated reader thread owns the whole Media Foundation object graph —
/// device activation, the source reader, format negotiation — and pulls samples
/// synchronously, converting to RGB32 and copying into tightly packed BGRA frames
/// (RGB32 is BGRX; the fourth byte is forced opaque). Disposing the stream stops
/// the thread, which releases the device (camera LED off).
///
/// The camera opens in one of two Frame Server modes:
/// <list type="bullet">
/// <item><b>Shared</b> (the default): never locks another app (Camera, Teams, …) out,
/// and joins one already streaming. A sharing source cannot choose a mode, so it runs
/// at whatever the controlling app streams, or the camera's default when none does.</item>
/// <item><b>Exclusive</b>: the controlling source, which selects the requested
/// <see cref="StreamMode"/> (selecting the native type is what switches the camera to
/// that size and rate). Only one controlling client may stream, so other apps are
/// refused while it runs — as in the Rust app, which always opens this way.</item>
/// </list>
/// Either way, what the camera offers is read by the open and kept for
/// <see cref="KnownModes"/>, and <see cref="CurrentMode"/> reports what it runs at.
///
/// Startup waits for a decoded frame to be stored, not just a successful sample
/// request: activation and negotiation can succeed when the first read fails.
/// </summary>
public sealed class CameraStream : IDisposable
{
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>MF_SOURCE_READER_CURRENT_TYPE_INDEX: the native type the camera is running now.</summary>
    private const uint CurrentTypeIndex = 0xFFFF_FFFF;

    private static readonly Lock ModesGate = new();
    private static readonly Dictionary<string, StreamModes> Modes = [];

    private readonly Lock _frameGate = new();
    private byte[]? _latest;
    private uint _width, _height;
    private long _generation;
    private volatile bool _stop;
    private Thread? _thread;

    /// <summary>Raised on the reader thread after each new frame is stored.</summary>
    public event Action? FrameArrived;

    /// <summary>Raised on the reader thread when the stream ends on its own (read failure, unplug).</summary>
    public event Action<CaptureException>? Failed;

    /// <summary>Whether this stream controls the camera (exclusive) or shares it.</summary>
    public bool Exclusive { get; private init; }

    /// <summary>
    /// The mode the camera is streaming at — the requested one when exclusive, the
    /// controlling app's (or the camera's default) when shared. Follows a controlling
    /// app's mid-stream change. Null when the camera does not report one.
    /// </summary>
    public StreamMode? CurrentMode { get; private set; }

    /// <summary>Bumped once per stored frame.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    private CameraStream() { }

    /// <summary>The modes the camera offered when it was last opened (empty until a stream has opened).</summary>
    internal static StreamModes KnownModes(string uniqueId)
    {
        lock (ModesGate) return Modes.TryGetValue(uniqueId, out var m) ? m : StreamModes.Empty;
    }

    /// <param name="exclusive">Control the camera and stream at <paramref name="mode"/>; otherwise share it.</param>
    /// <param name="mode">The exclusive mode; null for the camera's default. Ignored when sharing.</param>
    internal static CameraStream Start(string uniqueId, bool exclusive, StreamMode? mode)
    {
        if (!Cameras.Supported) throw new CaptureException(CaptureError.Unsupported);
        var stream = new CameraStream { Exclusive = exclusive };
        var setup = new TaskCompletionSource<CaptureException?>(TaskCreationOptions.RunContinuationsAsynchronously);
        stream._thread = new Thread(() => stream.ReaderThread(uniqueId, mode, exclusive, setup))
        {
            IsBackground = true,
            Name = "openlogi-camera-reader",
        };
        stream._thread.Start();
        if (!setup.Task.Wait(SetupTimeout))
        {
            stream._stop = true;
            throw new CaptureException(CaptureError.Timeout);
        }
        if (setup.Task.Result is { } error)
        {
            stream._thread.Join();
            throw error;
        }
        return stream;
    }

    /// <summary>
    /// Hand the latest frame to <paramref name="copy"/> under the frame lock (bytes,
    /// width, height). Returns false when no frame has arrived yet.
    /// </summary>
    public bool WithLatest(Action<byte[], uint, uint> copy)
    {
        lock (_frameGate)
        {
            if (_latest is null) return false;
            copy(_latest, _width, _height);
            return true;
        }
    }

    /// <summary>A copy of the latest frame, or null before the first.</summary>
    public Frame? Snapshot()
    {
        Frame? frame = null;
        WithLatest((bytes, w, h) => frame = new Frame(w, h, (byte[])bytes.Clone()));
        return frame;
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread is { } t && t != Thread.CurrentThread)
            t.Join(TimeSpan.FromSeconds(3));
    }

    private void ReaderThread(string uniqueId, StreamMode? mode, bool exclusive, TaskCompletionSource<CaptureException?> setup)
    {
        var com = MfNative.CoInitializeEx(0, MfNative.CoInitMultithreaded);
        IMFMediaSource? source = null;
        IMFSourceReader? reader = null;
        try
        {
            MfNative.EnsureStarted();
            (source, reader) = OpenReader(uniqueId, exclusive, mode, out var width, out var height, out var stride);
            Pump(reader, width, height, stride, setup);
        }
        catch (CaptureException e)
        {
            if (!setup.TrySetResult(e)) Failed?.Invoke(e);
        }
        catch (Exception e)
        {
            var error = new CaptureException(CaptureError.Setup, e.Message);
            if (!setup.TrySetResult(error)) Failed?.Invoke(error);
        }
        finally
        {
            // The reader shuts its source down when released; the explicit
            // Shutdown covers a source that never made it into a reader.
            Com.Release(reader);
            if (source is not null) { _ = source.Shutdown(); Com.Release(source); }
            if (com >= 0) MfNative.CoUninitialize();
        }
    }

    private (IMFMediaSource Source, IMFSourceReader Reader) OpenReader(
        string uniqueId, bool exclusive, StreamMode? mode, out uint width, out uint height, out int stride)
    {
        // What this camera offered before says nothing about what it offers now.
        lock (ModesGate) Modes.Remove(uniqueId);

        var attributes = Com.CreateAttributes(1);
        IMFMediaSource source;
        IMFSourceReader reader;
        try
        {
            Com.Check(attributes.SetUINT32(Mf.SourceReaderEnableVideoProcessing, 1), "SetUINT32");
            var (activated, hr) = Cameras.ActivateSource(uniqueId, shared: !exclusive);
            if (activated is null) throw hr == 0 ? new CaptureException(CaptureError.NotFound) : MapError(hr);
            source = activated;

            var sourcePtr = Com.Pointer(source, Mf.IidMfMediaSource);
            var attrPtr = Com.Pointer(attributes, Mf.IidMfAttributes);
            nint readerPtr;
            try { hr = MfNative.MFCreateSourceReaderFromMediaSource(sourcePtr, attrPtr, out readerPtr); }
            finally { Marshal.Release(sourcePtr); Marshal.Release(attrPtr); }
            if (Com.Failed(hr))
            {
                _ = source.Shutdown();
                Com.Release(source);
                throw MapError(hr);
            }
            reader = Com.Wrap<IMFSourceReader>(readerPtr);
        }
        finally { Com.Release(attributes); }

        try
        {
            var formats = NativeFormats(reader);
            lock (ModesGate) Modes[uniqueId] = NativeFormat.Offered(formats);

            // Only the controlling source may choose: selecting the native type switches
            // the camera to that size and rate (a size hint on the RGB32 output alone is
            // dropped — the processor converts but never scales). A sharing source runs
            // at whatever the camera is already streaming.
            if (exclusive && NativeFormat.Choose(formats, mode) is { } format)
            {
                Com.Check(reader.GetNativeMediaType(Mf.FirstVideoStream, format.Index, out var native), "GetNativeMediaType");
                int selected;
                try { selected = reader.SetCurrentMediaType(Mf.FirstVideoStream, 0, native); }
                finally { Marshal.Release(native); }
                if (Com.Failed(selected)) throw MapError(selected);
            }
            CurrentMode = NativeMode(reader);

            SetRgb32Output(reader);
            (width, height, stride) = ReadGeometry(reader);
            return (source, reader);
        }
        catch
        {
            Com.Release(reader);
            _ = source.Shutdown();
            Com.Release(source);
            throw;
        }
    }

    /// <summary>The mode of the native type the camera is running now.</summary>
    private static StreamMode? NativeMode(IMFSourceReader reader)
    {
        if (Com.Failed(reader.GetNativeMediaType(Mf.FirstVideoStream, CurrentTypeIndex, out var p))) return null;
        var native = Com.Wrap<IMFMediaType>(p);
        try
        {
            if (Com.Failed(native.GetUINT64(Mf.MtFrameSize, out var size))
                || Com.Failed(native.GetUINT64(Mf.MtFrameRate, out var rate))
                || StreamModes.WholeFps((uint)(rate >> 32), (uint)(rate & 0xFFFF_FFFF)) is not { } fps)
                return null;
            return new StreamMode((uint)(size >> 32), (uint)(size & 0xFFFF_FFFF), fps);
        }
        finally { Com.Release(native); }
    }

    /// <summary>
    /// The native types the reader lists that it can convert to RGB32, in driver
    /// order. A compressed type it cannot decode would fail to open, so it is not
    /// listed, while a convertible one at another size still previews fine.
    /// </summary>
    private static List<NativeFormat> NativeFormats(IMFSourceReader reader)
    {
        var formats = new List<NativeFormat>();
        for (uint index = 0; ; index++)
        {
            if (Com.Failed(reader.GetNativeMediaType(Mf.FirstVideoStream, index, out var p))) break;
            var native = Com.Wrap<IMFMediaType>(p);
            try
            {
                if (Com.Failed(native.GetGUID(Mf.MtSubtype, out var subtype))
                    || !(subtype == Mf.VideoFormatNv12 || subtype == Mf.VideoFormatYuy2
                         || subtype == Mf.VideoFormatRgb24 || subtype == Mf.VideoFormatRgb32))
                    continue;
                if (Com.Failed(native.GetUINT64(Mf.MtFrameSize, out var size))) continue;
                uint fps = 0;
                if (!Com.Failed(native.GetUINT64(Mf.MtFrameRate, out var rate)))
                    fps = StreamModes.WholeFps((uint)(rate >> 32), (uint)(rate & 0xFFFF_FFFF)) ?? 0;
                formats.Add(new NativeFormat(index, (uint)(size >> 32), (uint)(size & 0xFFFF_FFFF), fps));
            }
            finally { Com.Release(native); }
        }
        return formats;
    }

    /// <summary>Ask the reader for RGB32 output (Media Foundation inserts the decoder/converter).</summary>
    private static void SetRgb32Output(IMFSourceReader reader)
    {
        Com.Check(MfNative.MFCreateMediaType(out var outputPtr), "MFCreateMediaType");
        var output = Com.Wrap<IMFMediaType>(outputPtr);
        try
        {
            Com.Check(output.SetGUID(Mf.MtMajorType, Mf.MediaTypeVideo), "SetGUID");
            Com.Check(output.SetGUID(Mf.MtSubtype, Mf.VideoFormatRgb32), "SetGUID");
            var p = Com.Pointer(output, Mf.IidMfMediaType);
            try { Com.Check(reader.SetCurrentMediaType(Mf.FirstVideoStream, 0, p), "SetCurrentMediaType"); }
            finally { Marshal.Release(p); }
        }
        finally { Com.Release(output); }
    }

    /// <summary>The negotiated output geometry — the stride says whether rows arrive bottom-up.</summary>
    private static (uint Width, uint Height, int Stride) ReadGeometry(IMFSourceReader reader)
    {
        Com.Check(reader.GetCurrentMediaType(Mf.FirstVideoStream, out var currentPtr), "GetCurrentMediaType");
        var current = Com.Wrap<IMFMediaType>(currentPtr);
        try
        {
            Com.Check(current.GetUINT64(Mf.MtFrameSize, out var size), "MF_MT_FRAME_SIZE");
            var width = (uint)(size >> 32);
            var height = (uint)(size & 0xFFFF_FFFF);
            var stride = Com.Failed(current.GetUINT32(Mf.MtDefaultStride, out var s)) ? (int)width * 4 : unchecked((int)s);
            return (width, height, stride);
        }
        finally { Com.Release(current); }
    }

    /// <summary>Report startup only after a frame is stored, then keep reading until stopped or failed.</summary>
    private void Pump(IMFSourceReader reader, uint width, uint height, int stride, TaskCompletionSource<CaptureException?> setup)
    {
        byte[]? back = null;
        while (!_stop)
        {
            var hr = reader.ReadSample(Mf.FirstVideoStream, 0, out _, out var flags, out _, out var samplePtr);
            if (_stop)
            {
                if (samplePtr != 0) Marshal.Release(samplePtr);
                break;
            }
            if (Com.Failed(hr)) throw MapError(hr);
            if ((flags & Mf.ReaderFlagError) != 0) throw new CaptureException(CaptureError.Setup, "source reader error");
            if ((flags & Mf.ReaderFlagEndOfStream) != 0) throw new CaptureException(CaptureError.Setup, "end of stream");
            // The controlling app can switch the camera's mode under a sharing
            // source: renegotiate RGB32 for the new native type and take its geometry.
            if ((flags & Mf.ReaderFlagNativeMediaTypeChanged) != 0)
            {
                SetRgb32Output(reader);
                CurrentMode = NativeMode(reader);
            }
            if ((flags & (Mf.ReaderFlagNativeMediaTypeChanged | Mf.ReaderFlagCurrentMediaTypeChanged)) != 0)
                (width, height, stride) = ReadGeometry(reader);
            if (samplePtr == 0) continue; // stream tick / gap

            var sample = Com.Wrap<IMFSample>(samplePtr);
            var stored = false;
            try { stored = CopySample(sample, width, height, stride, ref back); }
            finally { Com.Release(sample); }
            if (!stored) continue;

            setup.TrySetResult(null);
            FrameArrived?.Invoke();
        }
        setup.TrySetResult(new CaptureException(CaptureError.Timeout));
    }

    /// <summary>
    /// Copy one RGB32 sample into a tightly packed BGRA frame, flipping bottom-up
    /// rows when the stride is negative, then publish it. False for a malformed buffer.
    /// </summary>
    private unsafe bool CopySample(IMFSample sample, uint width, uint height, int stride, ref byte[]? back)
    {
        if (Com.Failed(sample.ConvertToContiguousBuffer(out var bufferPtr))) return false;
        var buffer = Com.Wrap<IMFMediaBuffer>(bufferPtr);
        try
        {
            if (Com.Failed(buffer.Lock(out var data, out _, out var length))) return false;
            try
            {
                var rowBytes = (int)width * 4;
                var absStride = Math.Abs(stride);
                if (width == 0 || height == 0 || absStride < rowBytes
                    || (long)absStride * (height - 1) + rowBytes > length)
                    return false;
                var frameBytes = rowBytes * (int)height;
                if (back is null || back.Length != frameBytes) back = new byte[frameBytes];
                fixed (byte* dst = back)
                {
                    var src = (byte*)data;
                    for (var y = 0; y < height; y++)
                    {
                        // A negative stride means the buffer's first row is the bottom line.
                        var srcRow = stride < 0 ? (int)height - 1 - y : y;
                        Buffer.MemoryCopy(src + (long)srcRow * absStride, dst + (long)y * rowBytes, rowBytes, rowBytes);
                    }
                    // RGB32 is BGRX: the fourth byte is undefined, so force it opaque.
                    for (var i = 3; i < frameBytes; i += 4) dst[i] = 0xFF;
                }
            }
            finally { _ = buffer.Unlock(); }
        }
        finally { Com.Release(buffer); }

        lock (_frameGate)
        {
            (_latest, back) = (back, _latest);
            _width = width;
            _height = height;
        }
        Interlocked.Increment(ref _generation);
        return true;
    }

    private static CaptureException MapError(int hr) => hr switch
    {
        Mf.EAccessDenied => new CaptureException(CaptureError.AccessDenied),
        Mf.ESharingViolation or Mf.VideoRecordingDeviceLocked or Mf.VideoRecordingDevicePreempted
            => new CaptureException(CaptureError.InUse),
        Mf.HwMftFailedStartStreaming => new CaptureException(CaptureError.ResourcesUnavailable),
        _ => new CaptureException(CaptureError.Setup, $"0x{hr:X8}"),
    };
}
