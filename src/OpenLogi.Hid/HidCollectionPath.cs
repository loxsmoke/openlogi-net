using System.Text.RegularExpressions;

namespace OpenLogi.Hid;

/// <summary>
/// Pairs up the HID collections Windows splits one USB interface into. A receiver's
/// HID++ interface becomes col01, col02, … nodes whose device paths differ only in
/// the col token and the last segment of the instance id (the collection index):
/// <code>
/// \\?\hid#vid_046d&amp;pid_c52b&amp;mi_02&amp;col01#8&amp;2de4abc&amp;0&amp;0000#{guid}
/// \\?\hid#vid_046d&amp;pid_c52b&amp;mi_02&amp;col02#8&amp;2de4abc&amp;0&amp;0001#{guid}
/// </code>
/// Two identical dongles share VID/PID but not the instance prefix, so this is what
/// tells which col02 belongs to which col01 (issue #19 review: with two Unifying
/// receivers, VID/PID matching probed one receiver's slots through the other's col02).
/// </summary>
internal static partial class HidCollectionPath
{
    [GeneratedRegex(@"^col[0-9a-f]+$", RegexOptions.IgnoreCase)]
    private static partial Regex CollectionToken();

    /// <summary>
    /// The key every collection of one interface shares, or <c>null</c> when the path
    /// isn't a collection path of that shape (then it has no siblings).
    /// </summary>
    public static string? InterfaceKey(string devicePath)
    {
        var parts = devicePath.Split('#');
        if (parts.Length < 3) return null;
        var hardware = parts[1].Split('&');
        if (!hardware.Any(t => CollectionToken().IsMatch(t))) return null;
        var lastSegment = parts[2].LastIndexOf('&');
        if (lastSegment <= 0) return null;
        var hardwareKey = string.Join("&", hardware.Where(t => !CollectionToken().IsMatch(t)));
        return $"{hardwareKey}#{parts[2][..lastSegment]}".ToLowerInvariant();
    }

    /// <summary>Whether two device paths are different collections of the same interface.</summary>
    public static bool AreSiblings(string a, string b) =>
        !string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        && InterfaceKey(a) is { } key
        && key == InterfaceKey(b);
}
