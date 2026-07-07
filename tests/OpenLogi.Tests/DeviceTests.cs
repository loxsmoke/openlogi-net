using OpenLogi.Core.DeviceInfo;

namespace OpenLogi.Tests;

public class DeviceTests
{
    [Fact]
    public void RegistryTypeIsCaseFolded()
    {
        Assert.Equal(DeviceKind.Mouse, DeviceKindExtensions.FromRegistryType("mouse"));
        Assert.Equal(DeviceKind.Mouse, DeviceKindExtensions.FromRegistryType("MOUSE"));
        Assert.Equal(DeviceKind.Keyboard, DeviceKindExtensions.FromRegistryType("  Keyboard "));
    }

    [Fact]
    public void UnknownRegistryTypeDefersToTheCaller()
    {
        Assert.Equal(DeviceKind.Unknown, DeviceKindExtensions.FromRegistryType("webcam"));
        Assert.Equal(DeviceKind.Unknown, DeviceKindExtensions.FromRegistryType(""));
    }

    [Fact]
    public void CapabilitiesTrackTheDrivingFeatureIds()
    {
        var mouse = Capabilities.FromFeatureIds([0x0003, 0x1b04, 0x2202, 0x2110]);
        Assert.Equal(new Capabilities { Buttons = true, Pointer = true }, mouse);

        var keyboard = Capabilities.FromFeatureIds([0x0001, 0x8080]);
        Assert.Equal(new Capabilities { Lighting = true }, keyboard);

        var invertibleWheel = Capabilities.FromFeatureIds([0x0003, 0x1b04, 0x2202, 0x2121]);
        Assert.Equal(new Capabilities { Buttons = true, Pointer = true, ScrollInversion = true }, invertibleWheel);

        Assert.Equal(new Capabilities(), Capabilities.FromFeatureIds([0x0000, 0x0003]));
    }

    [Fact]
    public void PresumedCapabilitiesKeepAnUnprobedMouseConfigurable()
    {
        var mouse = Capabilities.PresumedFromKind(DeviceKind.Mouse);
        Assert.True(mouse.Buttons && mouse.Pointer && !mouse.Lighting);
        Assert.True(Capabilities.PresumedFromKind(DeviceKind.Keyboard).Lighting);
        Assert.Equal(new Capabilities(), Capabilities.PresumedFromKind(DeviceKind.Unknown));
    }

    [Fact]
    public void ConfigKeyMatchesExtendedModelPlusPid()
    {
        var info = new DeviceModelInfo
        {
            EntityCount = 1,
            UnitId = [0, 0, 0, 0],
            Transports = new DeviceTransports { Usb = true },
            ModelIds = [0xb042, 0, 0],
            ExtendedModelId = 0x02,
        };
        Assert.Equal("2b042", info.ConfigKey());
    }
}
