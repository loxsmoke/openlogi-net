using OpenLogi.Core;

namespace OpenLogi.Tests;

public class BrandTests
{
    private static readonly DeeplinkCommand[] All =
    [
        DeeplinkCommand.Show, DeeplinkCommand.OpenSettings, DeeplinkCommand.OpenAbout,
        DeeplinkCommand.CheckForUpdates, DeeplinkCommand.Quit,
    ];

    [Fact]
    public void UrlRoundTrips()
    {
        foreach (var cmd in All)
            Assert.Equal(cmd, Deeplink.ParseUrl(cmd.ToUrl()));
    }

    [Fact]
    public void ParseUrlIgnoresTrailingPathAndQuery()
    {
        Assert.Equal(DeeplinkCommand.Show, Deeplink.ParseUrl("openlogi://show/"));
        Assert.Equal(DeeplinkCommand.OpenSettings, Deeplink.ParseUrl("openlogi://open-settings?from=tray"));
    }

    [Fact]
    public void ParseUrlRejectsForeignSchemeAndUnknownCommand()
    {
        Assert.Null(Deeplink.ParseUrl("https://example.com/show"));
        Assert.Null(Deeplink.ParseUrl("openlogi://bogus"));
        Assert.Null(Deeplink.ParseUrl("openlogi://"));
    }
}
