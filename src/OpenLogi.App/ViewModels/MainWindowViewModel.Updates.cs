using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using OpenLogi.App.Services;
using OpenLogi.Core;
using OpenLogi.Core.Logging;

namespace OpenLogi.App.ViewModels;

// Launch-time update check + the Logitech-software receiver-contention warning.
public partial class MainWindowViewModel
{
    /// <summary>
    /// Record the user's answer to the first-run "check for updates?" prompt (so it's
    /// asked only once), then run the check immediately if they opted in.
    /// </summary>
    public async Task ApplyUpdateConsentAsync(bool enable)
    {
        _config.AppSettings.CheckForUpdates = enable;
        _config.AppSettings.UpdatePromptSeen = true;
        SaveConfig();
        await CheckForUpdatesAsync();
    }

    /// <summary>
    /// When the user has opted in, ask GitHub for the latest release and show the
    /// update banner if it's newer than this build, has cleared the soak period, and
    /// isn't a version already dismissed. Silent on every failure; a no-op when update
    /// checks are off.
    /// </summary>
    /// <remarks>
    /// Only ever considers <c>/releases/latest</c> — a single release, gated on its own
    /// age. It never picks the newest release that happens to have soaked, so a hotfix
    /// released hours after a bad build keeps <em>both</em> hidden until the hotfix itself
    /// ages in, rather than offering the build it replaced.
    /// </remarks>
    public async Task CheckForUpdatesAsync()
    {
        if (!_config.AppSettings.CheckForUpdates) return;
        // Never move the banner out from under an install/download in flight.
        if (UpdateBusy) return;
        if (Assembly.GetExecutingAssembly().GetName().Version is not { } current) return;

        var release = await UpdateService.CheckAsync(current);
        switch (UpdateCheck.Decide(release, _config.AppSettings.DismissedUpdate, DateTimeOffset.UtcNow))
        {
            case UpdateCheck.BannerState.Unchanged:
                return;

            case UpdateCheck.BannerState.Hidden:
                _latestRelease = null;
                UpdateAvailable = false;
                return;

            case UpdateCheck.BannerState.Shown:
                _latestRelease = release;
                UpdateBannerText = $"Update available: v{release!.Version}";
                // Install only makes sense when there's an installer to run and we're
                // running from an install it can upgrade in place.
                CanInstallUpdate = release.SetupUrl is not null && UpdateInstaller.IsInstalledBySetup();
                UpdateAvailable = true;
                return;
        }
    }

    /// <summary>
    /// Re-check daily while the app stays open. The launch-time check can land inside a
    /// release's soak period, and a machine that is never restarted would otherwise
    /// never see it.
    /// </summary>
    private void StartUpdateTimer() =>
        _updateTimer ??= new System.Threading.Timer(
            _ => Dispatcher.UIThread.Post(() => _ = CheckForUpdatesAsync()),
            null, UpdateCheck.SoakPeriod, UpdateCheck.SoakPeriod);

    /// <summary>
    /// Download the installer and run it silently, then quit so it can replace our files
    /// (build/OpenLogi.iss restarts the app afterwards via <c>/RELAUNCH</c>).
    /// </summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_latestRelease is not { } release || UpdateBusy) return;
        var previousText = UpdateBannerText;

        var path = await DownloadInstallerAsync(release, UpdateInstaller.UpdateStagingDir());
        if (path is null) return;

        try
        {
            UpdateBannerText = $"Installing v{release.Version} — {Brand.AppName} will restart…";
            UpdateInstaller.Launch(path);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User declined the UAC prompt. Put the banner back and stay running.
            UpdateBannerText = previousText;
            UpdateBusy = false;
            return;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("update", $"launching installer failed: {ex.Message}");
            UpdateBannerText = "Couldn't start the installer — try Download instead.";
            CanInstallUpdate = false;
            UpdateBusy = false;
            return;
        }

        // Shut down through the lifetime so Dispose() still runs (HID teardown, log seal)
        // rather than killing the process out from under it.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    /// <summary>Save the installer to the user's Downloads folder without running it.</summary>
    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (_latestRelease is not { } release || UpdateBusy) return;

        var path = await DownloadInstallerAsync(release, UpdateInstaller.DownloadsFolder());
        if (path is null) return;

        UpdateInstaller.Reveal(path);
        UpdateBannerText = $"v{release.Version} saved to Downloads";
    }

    /// <summary>Open the release notes for the offered version (not just "latest").</summary>
    [RelayCommand]
    private void ViewReleaseOnGitHub()
    {
        var url = _latestRelease is { } release ? Brand.ReleaseTagUrl(release.Version) : Brand.ReleasesUrl;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser / blocked — ignore */ }
    }

    /// <summary>
    /// Shared download half of Install and Download: drives the banner text and progress
    /// bar, and on failure leaves the banner explaining itself with Install withdrawn so
    /// the user is steered to the GitHub link. Returns the file path, or null on failure.
    /// </summary>
    private async Task<string?> DownloadInstallerAsync(ReleaseInfo release, string destDir)
    {
        UpdateBusy = true;
        UpdateProgress = 0;
        UpdateBannerText = $"Downloading v{release.Version}…";

        var progress = new Progress<double>(p => UpdateProgress = p);
        var path = await UpdateInstaller.DownloadAsync(release, destDir, progress);

        if (path is null)
        {
            UpdateBannerText = $"Download of v{release.Version} failed — try View on GitHub.";
            CanInstallUpdate = false;
            UpdateBusy = false;
            return null;
        }

        UpdateBusy = false;
        return path;
    }

    /// <summary>Hide the banner and remember this version so it won't reappear for it.</summary>
    [RelayCommand]
    private void DismissUpdate()
    {
        if (_latestRelease is { } release)
        {
            _config.AppSettings.DismissedUpdate = release.Version;
            SaveConfig();
        }
        UpdateAvailable = false;
    }

    /// <summary>
    /// Hide the contention banner for the currently-detected set of running Logitech
    /// apps. Session-only (not persisted): the warning reflects a live condition, so
    /// starting a *different* Logitech app re-shows it, and a fresh launch reminds again.
    /// </summary>
    [RelayCommand]
    private void DismissLogiWarning()
    {
        _dismissedLogiWarning = _logiWarningSignature;
        LogiWarningVisible = false;
    }

    /// <summary>
    /// Re-check which Logitech apps are running (off the UI thread — it enumerates
    /// processes) and drive the contention banner. Shown whenever the running set is
    /// non-empty and differs from what the user last dismissed.
    /// </summary>
    private async Task RefreshLogiWarningAsync()
    {
        var running = await Task.Run(SystemInfo.DetectRunningLogitechSoftware);
        _logiWarningSignature = string.Join("|", running.OrderBy(n => n, StringComparer.Ordinal));
        if (running.Count == 0)
        {
            LogiWarningVisible = false;
            return;
        }
        LogiWarningText = BuildLogiWarningText(running);
        LogiWarningVisible = _logiWarningSignature != _dismissedLogiWarning;
    }

    /// <summary>The banner sentence: names of the running apps + the shared receiver-contention caution.</summary>
    private static string BuildLogiWarningText(IReadOnlyList<string> running) =>
        $"{string.Join(", ", running)} {(running.Count == 1 ? "is" : "are")} running — Logitech software "
        + "can take over the receiver; only one app at a time can reliably control your devices.";
}
