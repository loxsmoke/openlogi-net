namespace OpenLogi.Assets;

/// <summary>A device's resolved local asset paths plus its hotspot metadata.</summary>
public sealed record ResolvedAsset(string? FrontRenderPath, string? ButtonsImagePath, Metadata Metadata);

/// <summary>
/// Resolves and caches a device's renders + hotspot metadata. Combines the Rust
/// GUI's <c>asset</c> sync: match the depot by model id (then bolt-PID suffix,
/// then display name), resolve variant filenames from the depot manifest, fetch
/// + cache, and return local paths.
/// </summary>
public sealed class AssetResolver(string cacheDir, AssetClient? client = null)
{
    private readonly AssetClient _client = client ?? new AssetClient();
    private AssetIndex? _index;

    /// <summary>Resolve just the front render path (used for the device card image).</summary>
    public async Task<string?> ResolveFrontRenderAsync(string configKey, string? codename, byte ext = 0, CancellationToken ct = default)
        => (await ResolveAsync(configKey, codename, ext, ct).ConfigureAwait(false))?.FrontRenderPath;

    /// <summary>
    /// Resolve the front render, the buttons render, and the hotspot metadata for
    /// a device, downloading any that aren't cached. Returns <c>null</c> when the
    /// device has no depot in the registry.
    /// </summary>
    public async Task<ResolvedAsset?> ResolveAsync(string configKey, string? codename, byte ext = 0, CancellationToken ct = default)
    {
        _index ??= await _client.FetchIndexAsync(ct).ConfigureAwait(false);

        var pid = configKey.Length >= 4 ? configKey[^4..] : configKey;
        var hit = _index.FindByModelId(configKey)
                  ?? _index.FindByModelIdSuffix(pid)
                  ?? (codename is not null ? _index.FindByDisplayName(codename) : null);
        if (hit is not { } found) return null;

        var (depot, entry) = found;
        var depotDir = Path.Combine(cacheDir, depot);

        DepotManifest? manifest = null;
        if (entry.Files.Any(f => f.Name == "manifest.json"))
        {
            try { manifest = AssetClient.ParseManifest(await _client.FetchFileAsync(entry.AssetPath, "manifest.json", ct).ConfigureAwait(false)); }
            catch { /* fall back to preferred filenames */ }
        }

        var frontName = manifest?.ResourceForVariant(entry.ModelId, ext, "device_image")
                        ?? entry.PreferredFile(DeviceEntry.FrontRenderFiles);
        var buttonsName = manifest?.ResourceForVariant(entry.ModelId, ext, "device_buttons_image")
                          ?? entry.PreferredFile(DeviceEntry.ButtonsRenderFiles);
        var metadataName = entry.PreferredFile(DeviceEntry.MetadataFiles);

        var frontPath = await FetchIntoCacheAsync(entry, depotDir, frontName, ct).ConfigureAwait(false);
        var buttonsPath = await FetchIntoCacheAsync(entry, depotDir, buttonsName, ct).ConfigureAwait(false);
        var metadataPath = await FetchIntoCacheAsync(entry, depotDir, metadataName, ct).ConfigureAwait(false);

        var metadata = new Metadata();
        if (metadataPath is not null)
        {
            try { metadata = System.Text.Json.JsonSerializer.Deserialize(await File.ReadAllBytesAsync(metadataPath, ct).ConfigureAwait(false), AssetJsonContext.Default.Metadata) ?? new Metadata(); }
            catch { /* keep empty metadata */ }
        }

        return new ResolvedAsset(frontPath, buttonsPath, metadata);
    }

    private async Task<string?> FetchIntoCacheAsync(DeviceEntry entry, string depotDir, string? name, CancellationToken ct)
    {
        if (name is null) return null;
        var fileEntry = entry.Files.FirstOrDefault(f => f.Name == name) ?? new FileEntry { Name = name };
        try
        {
            await _client.FetchEntryIfStaleAsync(entry.AssetPath, depotDir, fileEntry, ct).ConfigureAwait(false);
            return Path.Combine(depotDir, name);
        }
        catch
        {
            return null;
        }
    }
}
