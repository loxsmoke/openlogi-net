using OpenLogi.Hid;

namespace OpenLogi.Tests.Hid;

/// <summary>
/// The node-set signature that gates <see cref="HidDeviceWatcher"/>'s rescans:
/// it must change on any add/remove/rename but be stable against re-ordering and
/// the duplicate listings a composite device produces, so unrelated churn and a
/// device merely re-announcing its collections don't trigger a rescan.
/// </summary>
public class DeviceWatcherTests
{
    [Fact]
    public void OrderDoesNotAffectSignature()
    {
        var a = HidDeviceWatcher.Signature(["\\\\?\\hid#a", "\\\\?\\hid#b", "\\\\?\\hid#c"]);
        var b = HidDeviceWatcher.Signature(["\\\\?\\hid#c", "\\\\?\\hid#a", "\\\\?\\hid#b"]);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DuplicatePathsCollapse()
    {
        var once = HidDeviceWatcher.Signature(["\\\\?\\hid#a"]);
        var twice = HidDeviceWatcher.Signature(["\\\\?\\hid#a", "\\\\?\\hid#a"]);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void CaseInsensitivePaths()
    {
        // Windows device paths are case-insensitive; casing drift must not read as a change.
        var lower = HidDeviceWatcher.Signature(["\\\\?\\hid#vid_046d&pid_b037"]);
        var upper = HidDeviceWatcher.Signature(["\\\\?\\HID#VID_046D&PID_B037"]);
        Assert.Equal(lower, upper);
    }

    [Fact]
    public void ArrivalAndRemovalChangeSignature()
    {
        var one = HidDeviceWatcher.Signature(["\\\\?\\hid#a"]);
        var two = HidDeviceWatcher.Signature(["\\\\?\\hid#a", "\\\\?\\hid#b"]);
        Assert.NotEqual(one, two);          // a mouse's node arrived
        Assert.NotEqual(one, HidDeviceWatcher.Signature([])); // and the empty state differs from both
    }

    [Fact]
    public void EmptySetHasStableSignature()
    {
        Assert.Equal(HidDeviceWatcher.Signature([]), HidDeviceWatcher.Signature([]));
    }
}
