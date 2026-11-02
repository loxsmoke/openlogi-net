namespace OpenLogi.Camera;

/// <summary>Why a capture failed. Ported from Rust <c>openlogi_camera::CaptureError</c>.</summary>
public enum CaptureError
{
    /// <summary>The system camera privacy setting blocks desktop apps.</summary>
    AccessDenied,
    /// <summary>No camera matched the requested unique id.</summary>
    NotFound,
    /// <summary>Setup or the first frame took too long.</summary>
    Timeout,
    /// <summary>Hardware resources are unavailable; another app using the camera is one possible cause.</summary>
    ResourcesUnavailable,
    /// <summary>Exclusive access was refused: another app controls the camera.</summary>
    InUse,
    /// <summary>A platform capture object failed to construct.</summary>
    Setup,
    /// <summary>No capture backend on this platform.</summary>
    Unsupported,
}

public sealed class CaptureException(CaptureError error, string? detail = null)
    : Exception(detail is null ? Describe(error) : $"{Describe(error)}: {detail}")
{
    public CaptureError Error { get; } = error;

    private static string Describe(CaptureError error) => error switch
    {
        CaptureError.AccessDenied => "camera access denied — allow desktop apps to use the camera in Windows privacy settings",
        CaptureError.NotFound => "no camera matched that id",
        CaptureError.Timeout => "camera produced no frame in time",
        CaptureError.ResourcesUnavailable => "camera resources are unavailable; another application may be using the camera",
        CaptureError.Unsupported => "camera capture is not implemented on this platform",
        CaptureError.InUse => "another app controls the camera, so exclusive access was refused",
        _ => "capture setup failed",
    };
}

/// <summary>A tightly packed BGRA frame (opaque alpha), top row first.</summary>
public sealed class Frame(uint width, uint height, byte[] bgra)
{
    public uint Width { get; } = width;
    public uint Height { get; } = height;
    public byte[] Bgra { get; } = bgra;
}
