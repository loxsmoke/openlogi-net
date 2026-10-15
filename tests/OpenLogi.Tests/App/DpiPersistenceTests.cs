using OpenLogi.App.ViewModels;

namespace OpenLogi.Tests.App;

/// <summary>
/// gh-18 ("DPI flops to 1000 every now and then"): the slider's value is snapped and
/// filed before any device round trip, so the snap must be a pure function of the
/// device's supported list — and tolerate a page whose list never loaded.
/// </summary>
public class DpiPersistenceTests
{
    [Theory]
    [InlineData(1000u, 1000u)]
    [InlineData(1010u, 1000u)]
    [InlineData(1049u, 1000u)]
    [InlineData(1075u, 1100u)]
    [InlineData(150u, 200u)]
    [InlineData(9000u, 8000u)]
    public void SnapsToNearestSupportedValue(uint requested, uint expected)
    {
        uint[] options = [200, 400, 800, 1000, 1100, 1600, 3200, 8000];
        Assert.Equal(expected, MainWindowViewModel.SnapDpi(options, requested));
    }

    [Fact]
    public void PassesTheValueThroughWhenNoSupportedListIsKnown()
    {
        Assert.Equal(1234u, MainWindowViewModel.SnapDpi([], 1234));
    }
}
