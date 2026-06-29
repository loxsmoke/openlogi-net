using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenLogi.Assets;

/// <summary>
/// HTTP client for an asset host (default assets.openlogi.org), with SHA-256
/// verification and a cache-skip fetch. Ported from Rust <c>http::AssetClient</c>
/// (ureq → HttpClient).
/// </summary>
public sealed class AssetClient
{
    /// <summary>The default asset host.</summary>
    public const string DefaultBaseUrl = "https://assets.openlogi.org";

    private const string IndexName = "index.json";

    private readonly string _base;
    private readonly HttpClient _http;

    public AssetClient(string? baseUrl = null, HttpClient? http = null)
    {
        _base = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd("openlogi-assets/0.1 (+https://github.com/AprilNEA/OpenLogi)"))
        {
            // header already set by a shared client — fine
        }
    }

    /// <summary>GET <c>{base}/index.json</c> and parse it.</summary>
    public async Task<AssetIndex> FetchIndexAsync(CancellationToken ct = default)
    {
        var body = await GetBytesAsync($"{_base}/{IndexName}", ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(body, AssetJsonContext.Default.AssetIndex) ?? new AssetIndex();
    }

    /// <summary>GET a per-depot file, e.g. <c>("v1/devices/mx/", "front_core.png")</c>.</summary>
    public Task<byte[]> FetchFileAsync(string assetPath, string name, CancellationToken ct = default) =>
        GetBytesAsync($"{_base}/{assetPath.TrimStart('/')}{name}", ct);

    /// <summary>Parse a depot's <c>manifest.json</c> from raw bytes.</summary>
    public static DepotManifest? ParseManifest(byte[] bytes) => JsonSerializer.Deserialize(bytes, AssetJsonContext.Default.DepotManifest);

    /// <summary>Outcome of a cache-checked fetch.</summary>
    public enum FetchOutcome { CacheHit, Fetched }

    /// <summary>
    /// Fetch <paramref name="file"/> into <paramref name="dir"/> unless an on-disk copy already
    /// matches its SHA-256. The download is verified before it is written.
    /// </summary>
    public async Task<FetchOutcome> FetchEntryIfStaleAsync(string assetPath, string dir, FileEntry file, CancellationToken ct = default)
    {
        var dst = SafeComponentPath(dir, file.Name);
        if (CachedMatches(dst, file.Sha256))
            return FetchOutcome.CacheHit;

        var bytes = await FetchFileAsync(assetPath, file.Name, ct).ConfigureAwait(false);
        if (file.Sha256.Length > 0)
        {
            var actual = Sha256Hex(bytes);
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"checksum mismatch for {file.Name}: expected {file.Sha256}, got {actual}");
        }
        Directory.CreateDirectory(dir);
        await WriteReplaceAsync(dst, bytes, ct).ConfigureAwait(false);
        return FetchOutcome.Fetched;
    }

    private async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        // Up to 3 attempts with exponential backoff on transient failures.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var code = (int)resp.StatusCode;
                    if (attempt < 2 && (code >= 500 || code is 408 or 429))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << attempt)), ct).ConfigureAwait(false);
                        continue;
                    }
                    throw new HttpRequestException($"GET {url} failed: {code}");
                }
                return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << attempt)), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Hex SHA-256 of a blob.</summary>
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Whether <paramref name="path"/> exists and matches <paramref name="expectedSha"/> (any error → false).</summary>
    public static bool CachedMatches(string path, string expectedSha)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            return actual.Equals(expectedSha, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Join one untrusted registry filename onto a trusted dir, rejecting traversal.</summary>
    public static string SafeComponentPath(string dir, string component)
    {
        if (string.IsNullOrEmpty(component) || component.Contains('/') || component.Contains('\\')
            || component is "." or ".." || Path.IsPathRooted(component))
            throw new ArgumentException($"unsafe asset file name: {component}", nameof(component));
        return Path.Combine(dir, component);
    }

    private static async Task WriteReplaceAsync(string dst, byte[] bytes, CancellationToken ct)
    {
        var tmp = dst + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
        File.Move(tmp, dst, overwrite: true);
    }
}
