using OpenLogi.Core.DeviceInfo;

namespace OpenLogi.Tests.Core;

public class LegacyDeviceCatalogTests
{
    [Fact]
    public void MarathonM705IsListedByItsWirelessPid()
    {
        var entry = LegacyDeviceCatalog.Lookup(0x101b);
        Assert.NotNull(entry);
        Assert.Equal("Marathon Mouse M705", entry.Name);
        Assert.Equal(DeviceKind.Mouse, entry.Kind);
    }

    [Fact]
    public void Hidpp20DevicesAreNotInTheCatalog()
    {
        // The newer M705 revision (M-R0073) and the MX Master 4 speak HID++ 2.0 and
        // name themselves — the sweep must not find them here.
        Assert.Null(LegacyDeviceCatalog.Lookup(0x406d));
        Assert.Null(LegacyDeviceCatalog.Lookup(0xb042));
    }
}
