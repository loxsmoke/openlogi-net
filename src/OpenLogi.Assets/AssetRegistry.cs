using System.Text.Json.Serialization;

namespace OpenLogi.Assets;

/// <summary>One file in a depot, with its expected size and hash.</summary>
public sealed class FileEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("bytes")] public ulong Bytes { get; set; }
}

/// <summary>One depot entry in <c>index.json</c>. Ported from Rust <c>index::DeviceEntry</c>.</summary>
public sealed class DeviceEntry
{
    [JsonPropertyName("modelId")] public string ModelId { get; set; } = "";
    [JsonPropertyName("modelIds")] public List<string> ModelIds { get; set; } = [];
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("type")] public string Kind { get; set; } = "";
    [JsonPropertyName("asset_path")] public string AssetPath { get; set; } = "";
    [JsonPropertyName("files")] public List<FileEntry> Files { get; set; } = [];

    /// <summary>Filename schemas Logi ships, most-preferred first.</summary>
    public static readonly string[] MetadataFiles = ["core_metadata.json", "metadata.json"];
    public static readonly string[] FrontRenderFiles = ["front_core.png", "front.png"];
    public static readonly string[] ButtonsRenderFiles = ["side_core.png", "side.png"];

    /// <summary>Every model id this depot answers to, primary first (deduplicated).</summary>
    public IEnumerable<string> ModelIdCandidates()
    {
        yield return ModelId;
        foreach (var id in ModelIds)
            if (!id.Equals(ModelId, StringComparison.OrdinalIgnoreCase))
                yield return id;
    }

    /// <summary>First of <paramref name="candidates"/> this depot actually lists, or <c>null</c>.</summary>
    public string? PreferredFile(string[] candidates) =>
        candidates.FirstOrDefault(name => Files.Any(f => f.Name == name));

    /// <summary>Baseline files a per-device sync fetches: metadata, manifest, hero render.</summary>
    public List<string> BaselineFiles()
    {
        var files = new List<string>(3);
        if (PreferredFile(MetadataFiles) is { } meta) files.Add(meta);
        if (Files.Any(f => f.Name == "manifest.json")) files.Add("manifest.json");
        if (PreferredFile(FrontRenderFiles) is { } front) files.Add(front);
        return files;
    }
}

/// <summary>The <c>index.json</c> registry from assets.openlogi.org. Ported from Rust <c>index::Index</c>.</summary>
public sealed class AssetIndex
{
    [JsonPropertyName("schema_version")] public uint SchemaVersion { get; set; }
    [JsonPropertyName("devices")] public Dictionary<string, DeviceEntry> Devices { get; set; } = [];

    /// <summary>Depot one of whose model ids matches <paramref name="modelId"/> exactly.</summary>
    public (string Depot, DeviceEntry Entry)? FindByModelId(string modelId)
    {
        foreach (var (depot, entry) in Devices)
            if (entry.ModelIdCandidates().Any(id => id.Equals(modelId, StringComparison.OrdinalIgnoreCase)))
                return (depot, entry);
        return null;
    }

    /// <summary>Depot one of whose model ids ends with <paramref name="suffix"/> (e.g. the bolt PID).</summary>
    public (string Depot, DeviceEntry Entry)? FindByModelIdSuffix(string suffix)
    {
        foreach (var (depot, entry) in Devices)
            if (entry.ModelIdCandidates().Any(id => id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                return (depot, entry);
        return null;
    }

    /// <summary>Depot whose <c>displayName</c> equals <paramref name="name"/> (case-insensitive, exact).</summary>
    public (string Depot, DeviceEntry Entry)? FindByDisplayName(string name)
    {
        foreach (var (depot, entry) in Devices)
            if (entry.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return (depot, entry);
        return null;
    }
}

/// <summary>A depot's <c>manifest.json</c>. Ported from Rust <c>manifest::DepotManifest</c>.</summary>
public sealed class DepotManifest
{
    [JsonPropertyName("devices")] public List<ManifestDevice> Devices { get; set; } = [];

    /// <summary>The <c>device_image</c> filename for the variant matching <paramref name="modelId"/>.</summary>
    public string? DeviceImageFor(string modelId) => ResourceFor(modelId, "device_image");

    /// <summary>The <c>src</c> filename of resource <paramref name="resourceKey"/> for <paramref name="modelId"/>.</summary>
    public string? ResourceFor(string modelId, string resourceKey) =>
        Devices.FirstOrDefault(d => d.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase))
            ?.Resources.FirstOrDefault(r => r.Key == resourceKey)?.Src;

    /// <summary>Resolve a resource for the colour variant identified by <paramref name="ext"/> (0 = base).</summary>
    public string? ResourceForVariant(string baseModelId, byte ext, string resourceKey) =>
        ResourceFor(VariantModelId(baseModelId, ext), resourceKey);

    /// <summary>Build the variant model-id (<c>ext == 0</c> → base; else <c>{base}_ext{N}</c>).</summary>
    public static string VariantModelId(string baseId, byte ext) => ext == 0 ? baseId : $"{baseId}_ext{ext}";
}

public sealed class ManifestDevice
{
    [JsonPropertyName("modelId")] public string ModelId { get; set; } = "";
    [JsonPropertyName("resources")] public List<ManifestResource> Resources { get; set; } = [];
}

public sealed class ManifestResource
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("src")] public string Src { get; set; } = "";
}
