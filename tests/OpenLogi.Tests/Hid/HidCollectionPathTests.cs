using OpenLogi.Hid;

namespace OpenLogi.Tests.Hid;

/// <summary>Pairing a receiver's HID collections by device path (issue #19 review).</summary>
public class HidCollectionPathTests
{
    private const string Guid = "#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    [Fact]
    public void CollectionsOfOneInterfaceAreSiblings()
    {
        Assert.True(HidCollectionPath.AreSiblings(
            $@"\\?\hid#vid_046d&pid_c52b&mi_02&col01#8&2de4abc&0&0000{Guid}",
            $@"\\?\hid#vid_046d&pid_c52b&mi_02&col02#8&2de4abc&0&0001{Guid}"));
    }

    [Fact]
    public void IdenticalDonglesAreNotSiblings()
    {
        Assert.False(HidCollectionPath.AreSiblings(
            $@"\\?\hid#vid_046d&pid_c52b&mi_02&col01#8&2de4abc&0&0000{Guid}",
            $@"\\?\hid#vid_046d&pid_c52b&mi_02&col02#8&17f0e9&0&0001{Guid}"));
    }

    [Fact]
    public void OtherInterfaceOfTheSameDongleIsNotASibling()
    {
        Assert.False(HidCollectionPath.AreSiblings(
            $@"\\?\hid#vid_046d&pid_c52b&mi_02&col01#8&2de4abc&0&0000{Guid}",
            $@"\\?\hid#vid_046d&pid_c52b&mi_01&col02#8&2de4abc&0&0001{Guid}"));
    }

    [Fact]
    public void PathIsNotItsOwnSibling()
    {
        var path = $@"\\?\hid#vid_046d&pid_c52b&mi_02&col01#8&2de4abc&0&0000{Guid}";
        Assert.False(HidCollectionPath.AreSiblings(path, path.ToUpperInvariant()));
    }

    [Fact]
    public void BluetoothCollectionPathsPairUp()
    {
        // Shape seen on a real host: a BLE HID device's collections.
        const string hw = @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&02046d_pid&b354_rev&0042_e0afe69fa92c";
        Assert.True(HidCollectionPath.AreSiblings($"{hw}&col01#c&1008b05f&0&0000{Guid}", $"{hw}&col04#c&1008b05f&0&0003{Guid}"));
    }

    [Theory]
    [InlineData(@"\\?\hid#vid_046d&pid_c52b&mi_00#8&2de4abc&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}")] // no collection
    [InlineData(@"\\?\hid#vid_046d&pid_c52b&mi_02&col01")] // no instance id
    [InlineData("not a device path")]
    public void NonCollectionPathsHaveNoKey(string path) =>
        Assert.Null(HidCollectionPath.InterfaceKey(path));
}
