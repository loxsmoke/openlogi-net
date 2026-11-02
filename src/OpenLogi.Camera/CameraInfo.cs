namespace OpenLogi.Camera;

/// <summary>
/// A connected USB Video Class camera. Ported from Rust <c>openlogi_camera::Camera</c>.
/// </summary>
/// <param name="Name">Human-readable name, e.g. "Logitech StreamCam".</param>
/// <param name="UniqueId">
/// OS capture-layer identifier (the Media Foundation symbolic link). Used to open
/// the preview and controls; embeds a USB location and so may change when the
/// camera is moved to another port.
/// </param>
/// <param name="SerialNumber">USB <c>iSerialNumber</c> when the device path carries one. Port-stable.</param>
/// <param name="VendorId">USB vendor id (<c>0x046d</c> for Logitech).</param>
/// <param name="ProductId">USB product id (e.g. <c>0x0893</c> for the StreamCam).</param>
public sealed record CameraInfo(
    string Name,
    string UniqueId,
    string? SerialNumber,
    ushort VendorId,
    ushort ProductId)
{
    /// <summary>
    /// Persistence key that is stable across USB ports. Prefers the USB serial; a
    /// serial-less camera falls back to a model-scoped key, so two serial-less units
    /// of one model share settings (there is no stronger USB identity) while still
    /// showing as two cards, which are keyed by <see cref="UniqueId"/>.
    /// </summary>
    public string ConfigKey =>
        SerialNumber?.Trim() is { Length: > 0 } serial
            ? $"camera:{VendorId:x4}:{ProductId:x4}:serial:{serial.ToLowerInvariant()}"
            : $"camera:{VendorId:x4}:{ProductId:x4}";

    /// <summary>
    /// Give a camera whose firmware reports no USB product string its marketed name,
    /// replacing the placeholder the OS built from the raw ids.
    /// </summary>
    internal CameraInfo WithRegistryName() =>
        UnnamedCameras.Find(VendorId, ProductId) is { } name ? this with { Name = name } : this;
}

/// <summary>
/// Logitech UVC cameras whose USB device descriptor carries no product string, so
/// the OS names them from raw ids. Ported from Rust
/// <c>openlogi_device_registry::camera</c>. List a camera here only when its
/// descriptor carries no product string.
/// </summary>
internal static class UnnamedCameras
{
    public const ushort LogitechVendorId = 0x046d;

    private static readonly (ushort Vid, ushort Pid, string Name)[] Table =
    [
        (LogitechVendorId, 0x081d, "Logitech HD Webcam C510"),
    ];

    public static string? Find(ushort vid, ushort pid)
    {
        foreach (var (v, p, name) in Table)
            if (v == vid && p == pid)
                return name;
        return null;
    }

    internal static IReadOnlyList<(ushort Vid, ushort Pid, string Name)> All => Table;
}
