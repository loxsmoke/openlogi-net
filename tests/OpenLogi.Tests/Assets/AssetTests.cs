using System.Text.Json;
using OpenLogi.Assets;

namespace OpenLogi.Tests.Assets;

/// <summary>Ported from the Rust <c>openlogi-assets::index</c> + <c>manifest</c> tests.</summary>
public class AssetIndexTests
{
    private static DeviceEntry Entry(string modelId, string displayName) => new()
    {
        ModelId = modelId,
        DisplayName = displayName,
        Kind = "mouse",
        AssetPath = "v1/devices/mx_master_3s/",
    };

    private static AssetIndex IndexOf(string depot, DeviceEntry entry) =>
        new() { SchemaVersion = 1, Devices = { [depot] = entry } };

    [Fact]
    public void ModelIdCandidatesFallsBackToPrimaryForLegacyEntry()
    {
        var e = Entry("2b043", "MX Master 3S");
        Assert.Equal(["2b043"], e.ModelIdCandidates().ToArray());
    }

    [Fact]
    public void ModelIdCandidatesListsPrimaryThenExtrasWithoutDupes()
    {
        var e = Entry("2b043", "MX Master 3S");
        e.ModelIds = ["2b043", "2b034"];
        Assert.Equal(["2b043", "2b034"], e.ModelIdCandidates().ToArray());
    }

    [Fact]
    public void FindByModelIdMatchesAnyListedId()
    {
        var e = Entry("2b043", "MX Master 3S");
        e.ModelIds = ["2b043", "2b034"];
        var index = IndexOf("mx_master_3s", e);
        Assert.Equal("mx_master_3s", index.FindByModelId("2b034")?.Depot);
        Assert.Equal("mx_master_3s", index.FindByModelId("2b043")?.Depot);
    }

    [Fact]
    public void FindByModelIdSuffixMatchesSecondaryId()
    {
        var e = Entry("2b043", "MX Master 3S");
        e.ModelIds = ["2b043", "2b034"];
        var index = IndexOf("mx_master_3s", e);
        Assert.Equal("mx_master_3s", index.FindByModelIdSuffix("b034")?.Depot);
    }

    [Fact]
    public void FindByDisplayNameMatchesCaseInsensitivelyButNotSubstring()
    {
        var index = IndexOf("mx_master_3s", Entry("2b043", "MX Master 3S"));
        Assert.Equal("mx_master_3s", index.FindByDisplayName("mx master 3s")?.Depot);
        Assert.Null(index.FindByDisplayName("MX Master 3"));
    }

    [Fact]
    public void DeserializesModelIdsSchema()
    {
        const string json = """
        {
          "schema_version": 1,
          "devices": {
            "mx_master_3s": {
              "modelId": "2b043",
              "modelIds": ["2b034", "2b043"],
              "displayName": "MX Master 3S",
              "extendedDisplayName": "Wireless Mouse MX Master 3S",
              "type": "MOUSE",
              "asset_path": "v1/devices/mx_master_3s/",
              "files": [{"name": "front_core.png", "sha256": "ab", "bytes": 1}]
            }
          }
        }
        """;
        var index = JsonSerializer.Deserialize<AssetIndex>(json)!;
        Assert.Equal("mx_master_3s", index.FindByModelIdSuffix("b034")?.Depot);
        Assert.Equal(["2b034", "2b043"], index.Devices["mx_master_3s"].ModelIds);
    }

    private static DeviceEntry EntryWithFiles(params string[] names)
    {
        var e = Entry("2b043", "MX Master 3S");
        e.Files = [.. names.Select(n => new FileEntry { Name = n })];
        return e;
    }

    [Fact]
    public void BaselineFilesResolvesCoreSchema()
    {
        var e = EntryWithFiles("core_metadata.json", "manifest.json", "front_core.png");
        Assert.Equal(["core_metadata.json", "manifest.json", "front_core.png"], e.BaselineFiles());
    }

    [Fact]
    public void BaselineFilesResolvesOldSchema()
    {
        var e = EntryWithFiles("metadata.json", "manifest.json", "front.png", "side.png");
        Assert.Equal(["metadata.json", "manifest.json", "front.png"], e.BaselineFiles());
        Assert.Equal("side.png", e.PreferredFile(DeviceEntry.ButtonsRenderFiles));
    }

    [Fact]
    public void BaselineFilesSkipsMissingSlots()
    {
        var e = EntryWithFiles("manifest.json");
        Assert.Equal(["manifest.json"], e.BaselineFiles());
        Assert.Null(e.PreferredFile(DeviceEntry.MetadataFiles));
    }
}

public class DepotManifestTests
{
    [Fact]
    public void VariantModelIdFormats()
    {
        Assert.Equal("2b042", DepotManifest.VariantModelId("2b042", 0));
        Assert.Equal("2b042_ext1", DepotManifest.VariantModelId("2b042", 1));
    }

    [Fact]
    public void ResolvesDeviceImageForBaseAndVariant()
    {
        const string json = """
        {
          "devices": [
            { "modelId": "2b042", "resources": [ { "key": "device_image", "src": "front_core.png" } ] },
            { "modelId": "2b042_ext1", "resources": [ { "key": "device_image", "src": "front_ext_1.png" } ] }
          ]
        }
        """;
        var manifest = JsonSerializer.Deserialize<DepotManifest>(json)!;
        Assert.Equal("front_core.png", manifest.DeviceImageFor("2b042"));
        Assert.Equal("front_ext_1.png", manifest.ResourceForVariant("2b042", 1, "device_image"));
        Assert.Null(manifest.DeviceImageFor("unknown"));
    }
}

public class AssetClientPathTests
{
    [Fact]
    public void SafeComponentPathRejectsTraversal()
    {
        foreach (var name in new[] { "", ".", "..", "../x", "a/b.png", "a\\b.png", "/etc/passwd" })
            Assert.ThrowsAny<ArgumentException>(() => AssetClient.SafeComponentPath("C:\\cache", name));
        Assert.Equal(Path.Combine("C:\\cache", "front_core.png"), AssetClient.SafeComponentPath("C:\\cache", "front_core.png"));
    }
}
