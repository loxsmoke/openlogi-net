using System.Globalization;

namespace OpenLogi.Camera;

/// <summary>
/// Parsing of a Windows device-interface path such as
/// <c>\\?\usb#vid_046d&amp;pid_0893&amp;mi_00#SERIAL#{guid}\global</c>.
/// Ported from Rust <c>uvc_windows.rs</c> / <c>capture_windows.rs</c>.
/// </summary>
internal static class DevicePath
{
    /// <summary>The hex USB vendor/product ids from the path's <c>vid_xxxx</c> / <c>pid_xxxx</c> markers.</summary>
    public static (ushort Vid, ushort Pid)? ParseIds(string path)
    {
        var lower = path.ToLowerInvariant();
        return HexAfter(lower, "vid_") is { } vid && HexAfter(lower, "pid_") is { } pid ? (vid, pid) : null;

        static ushort? HexAfter(string text, string marker)
        {
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0 || at + marker.Length + 4 > text.Length) return null;
            return ushort.TryParse(text.AsSpan(at + marker.Length, 4), NumberStyles.HexNumber, null, out var v) ? v : null;
        }
    }

    /// <summary>
    /// The USB <c>iSerialNumber</c> embedded in the path, when present. Windows
    /// fabricates a parent-relative instance id (always containing <c>&amp;</c>) when
    /// the device has no serial — those are port-dependent and rejected.
    /// </summary>
    public static string? UsbSerial(string path)
    {
        var parts = path.Split('#');
        if (parts.Length < 3) return null;
        var instance = parts[2].Split('\\')[0].Trim();
        return instance.Length == 0 || instance.Contains('&') ? null : instance;
    }

    /// <summary>
    /// The device-instance portion of an interface path, without the trailing
    /// <c>#{interface-class-guid}\reference</c>. DirectShow and Media Foundation
    /// register one camera under different class GUIDs; this links the two.
    /// </summary>
    public static string DeviceInstance(string path) => path.Split("#{")[0];

    public static bool SameDevice(string a, string b) =>
        string.Equals(DeviceInstance(a), DeviceInstance(b), StringComparison.OrdinalIgnoreCase);
}
