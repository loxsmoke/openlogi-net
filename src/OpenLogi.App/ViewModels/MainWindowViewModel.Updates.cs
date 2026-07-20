using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using OpenLogi.App.Services;
using OpenLogi.Core;

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
    /// update banner if it's newer than this build (and not a version already
    /// dismissed). Silent on every failure; a no-op when update checks are off.
    /// </summary>
    public async Task CheckForUpdatesAsync()
    {
        if (!_config.AppSettings.CheckForUpdates) return;
        if (Assembly.GetExecutingAssembly().GetName().Version is not { } current) return;
        var newer = await UpdateService.CheckAsync(current);
        if (newer is null || newer == _config.AppSettings.DismissedUpdate) return;
        _latestUpdate = newer;
        UpdateBannerText = $"Update available: v{newer}";
        UpdateAvailable = true;
    }

    /// <summary>Open the releases page to download the available update.</summary>
    [RelayCommand]
    private void DownloadUpdate()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Brand.ReleasesUrl) { UseShellExecute = true }); }
        catch { /* no browser / blocked — ignore */ }
    }

    /// <summary>Hide the banner and remember this version so it won't reappear for it.</summary>
    [RelayCommand]
    private void DismissUpdate()
    {
        if (_latestUpdate is not null)
        {
            _config.AppSettings.DismissedUpdate = _latestUpdate;
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
