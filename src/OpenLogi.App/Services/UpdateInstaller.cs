using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using OpenLogi.Core;
using OpenLogi.Core.Logging;

namespace OpenLogi.App.Services;

/// <summary>
/// Downloads a release's setup.exe and, for the Install action, runs it.
/// </summary>
/// <remarks>
/// GitHub Releases publish no SHA-256 and the release workflow does not Authenticode-sign,
/// so integrity rests on three checks rather than a hash: the URL must be https on a
/// GitHub-owned host, and the downloaded length must match the size the API reported.
/// </remarks>
public static class UpdateInstaller
{
    /// <summary>The stable Inno <c>AppId</c> from build/OpenLogi.iss; <c>_is1</c> is Inno's suffix.</summary>
    private const string UninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8E0F7A12-BFB3-4FE8-B9A5-48FD50A15A9A}_is1";

    /// <summary>Subdirectory of <see cref="Paths.DataDir"/> holding a downloaded installer.</summary>
    private const string UpdateDirName = "update";

    /// <summary>Hosts a release asset may be served from. Suffix-matched on a label boundary.</summary>
    private static readonly string[] AllowedHosts = ["github.com", "githubusercontent.com"];

    /// <summary>
    /// <c>FOLDERID_Downloads</c> from the Windows SDK's <c>KnownFolders.h</c>. Downloads
    /// post-dates the CSIDL scheme that <see cref="Environment.SpecialFolder"/> wraps, so
    /// there is no managed constant for it and the GUID has to be spelled out here.
    /// </summary>
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>
    /// True when this build was installed by our setup.exe and is running from that install
    /// — the only case where launching the installer produces an in-place upgrade. A
    /// portable copy unzipped next to an unrelated install is correctly rejected, because
    /// the registry's InstallLocation won't match where we're actually running from.
    /// </summary>
    public static bool IsInstalledBySetup()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") as string is not { Length: > 0 } location) return false;
            return SamePath(location, AppContext.BaseDirectory);
        }
        catch { return false; }
    }

    /// <summary>Directory a downloaded-but-not-run installer is saved to.</summary>
    public static string DownloadsFolder()
    {
        try
        {
            // Via the known-folder API rather than %USERPROFILE%\Downloads, so a user who
            // has relocated the folder gets their real one.
            if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out var path) == 0
                && !string.IsNullOrEmpty(path))
                return path;
        }
        catch { /* fall through to the default location */ }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>Directory the Install action stages its download in.</summary>
    public static string UpdateStagingDir() => Path.Combine(Paths.DataDir(), UpdateDirName);

    /// <summary>
    /// Download the release's installer into <paramref name="destDir"/>, reporting progress
    /// in 0..1. Returns the file path, or <c>null</c> on any failure (all of which are logged
    /// and none of which should be fatal to the caller).
    /// </summary>
    public static async Task<string?> DownloadAsync(
        ReleaseInfo release, string destDir, IProgress<double>? progress = null,
        HttpClient? http = null, CancellationToken ct = default)
    {
        if (release.SetupUrl is not { } url || !IsTrustedUrl(url))
        {
            DiagnosticLog.Info("update", $"refusing installer URL: {release.SetupUrl ?? "(none)"}");
            return null;
        }

        var client = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            Directory.CreateDirectory(destDir);
            var dst = UniquePath(destDir, UpdateCheck.SetupAssetName(release.Version));
            var tmp = dst + ".part";
            // A .part left by an interrupted attempt would otherwise block the write.
            TryDelete(tmp);

            // Up to 3 attempts with exponential backoff, matching AssetClient.GetBytesAsync.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await FetchToFileAsync(client, url, tmp, release.SetupSize, progress, ct).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException && attempt < 2 && !ct.IsCancellationRequested)
                {
                    DiagnosticLog.Info("update", $"download attempt {attempt + 1} failed: {ex.Message}");
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << attempt)), ct).ConfigureAwait(false);
                }
            }

            File.Move(tmp, dst, overwrite: true);
            DiagnosticLog.Info("update", $"downloaded {dst}");
            return dst;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Info("update", $"download failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (http is null) client.Dispose();
        }
    }

    /// <summary>
    /// Stream the response to <paramref name="tmp"/>, verifying the length matches what the
    /// release API advertised. A short or padded body means the asset isn't what we asked
    /// for, so the partial file is deleted rather than left to be executed.
    /// </summary>
    private static async Task FetchToFileAsync(
        HttpClient client, string url, string tmp, long expectedSize,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        // Prefer the API's size; fall back to Content-Length when the release JSON omitted it.
        var total = expectedSize > 0 ? expectedSize : resp.Content.Headers.ContentLength ?? 0;
        var written = 0L;

        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dstFile = File.Create(tmp))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dstFile.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)written / total));
                }
            }

            if (expectedSize > 0 && written != expectedSize)
                throw new IOException($"size mismatch: expected {expectedSize} bytes, got {written}");
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>
    /// Run the downloaded installer. <c>/SILENT</c> shows a progress bar but no wizard;
    /// <c>/RELAUNCH</c> is our own switch, handled in build/OpenLogi.iss, which restarts the
    /// app afterwards (the stock [Run] entry is skipped under /SILENT). The caller must shut
    /// the app down after this returns so the installer can replace the files.
    /// </summary>
    /// <remarks>
    /// Throws <see cref="System.ComponentModel.Win32Exception"/> with
    /// <c>NativeErrorCode == 1223</c> when the user declines the UAC prompt — the caller
    /// must treat that as "cancelled" and stay running.
    /// </remarks>
    public static void Launch(string setupPath)
    {
        var psi = new ProcessStartInfo(setupPath)
        {
            // Required for setup.exe to self-elevate: the app itself runs as-invoker.
            UseShellExecute = true,
        };
        psi.ArgumentList.Add("/SILENT");
        psi.ArgumentList.Add("/NORESTART");
        psi.ArgumentList.Add("/RELAUNCH");
        Process.Start(psi);
        DiagnosticLog.Info("update", $"launched installer {setupPath}");
    }

    /// <summary>Open Explorer with <paramref name="path"/> selected. Best-effort.</summary>
    public static void Reveal(string path)
    {
        try
        {
            // /select needs the argument quoted as one token, so pass the whole switch
            // through Arguments rather than ArgumentList.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { DiagnosticLog.Info("update", $"reveal failed: {ex.Message}"); }
    }

    /// <summary>
    /// Whether a release-asset URL is safe to fetch: https, on a GitHub-owned host. Matching
    /// is on a label boundary so <c>github.com.evil.com</c> is rejected.
    /// </summary>
    public static bool IsTrustedUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        foreach (var allowed in AllowedHosts)
        {
            if (host.Equals(allowed, StringComparison.OrdinalIgnoreCase)) return true;
            if (host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// A free path in <paramref name="dir"/> for <paramref name="fileName"/>, suffixing
    /// " (2)", " (3)", … rather than clobbering a file the user already has.
    /// </summary>
    public static string UniquePath(string dir, string fileName)
    {
        var candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        // Pathological case only; overwrite rather than fail the download outright.
        return Path.Combine(dir, fileName);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    /// <summary>Compare two directory paths ignoring case and any trailing separator.</summary>
    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token,
        [MarshalAs(UnmanagedType.LPWStr)] out string path);
}
