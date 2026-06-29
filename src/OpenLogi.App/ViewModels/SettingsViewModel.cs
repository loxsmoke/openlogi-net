using CommunityToolkit.Mvvm.ComponentModel;
using OpenLogi.Core;

namespace OpenLogi.App.ViewModels;

/// <summary>
/// Backs the Settings window: launch-at-login, opt-in update check, asset
/// auto-download, and thumb-wheel sensitivity. Bound to the shared <see cref="Config"/>;
/// changes apply immediately (autostart writes the registry) and save.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly Config _config;
    private readonly bool _loading;

    [ObservableProperty] private bool _launchAtLogin;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private bool _autoDownloadAssets;
    [ObservableProperty] private bool _minimizeToTray;

    public SettingsViewModel(Config config)
    {
        _config = config;
        _loading = true;
        // The registry Run-key is the source of truth for autostart.
        LaunchAtLogin = Autostart.IsEnabled();
        CheckForUpdates = config.AppSettings.CheckForUpdates;
        AutoDownloadAssets = config.AppSettings.AutoDownloadAssets;
        MinimizeToTray = config.AppSettings.MinimizeToTray;
        _loading = false;
    }

    public SettingsViewModel() : this(SafeLoad()) { } // design-time / fallback

    partial void OnLaunchAtLoginChanged(bool value)
    {
        if (_loading) return;
        Autostart.SetEnabled(value);
        _config.AppSettings.LaunchAtLogin = value;
        Save();
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (_loading) return;
        _config.AppSettings.CheckForUpdates = value;
        Save();
    }

    partial void OnAutoDownloadAssetsChanged(bool value)
    {
        if (_loading) return;
        _config.AppSettings.AutoDownloadAssets = value;
        Save();
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (_loading) return;
        _config.AppSettings.MinimizeToTray = value;
        Save();
    }

    private void Save()
    {
        try { _config.SaveAtomic(); } catch { /* keep settings fluid */ }
    }

    private static Config SafeLoad()
    {
        try { return Config.LoadOrDefault(); } catch { return new Config(); }
    }
}
