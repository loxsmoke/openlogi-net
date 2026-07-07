namespace OpenLogi.Core.Config;

/// <summary>App-wide preferences not tied to any device.</summary>
public sealed class AppSettings
{
    public const int DefaultThumbwheelSensitivity = 14;
    public const int MinThumbwheelSensitivity = 1;
    public const int MaxThumbwheelSensitivity = 100;

    public bool LaunchAtLogin { get; set; }
    public bool CheckForUpdates { get; set; }
    public bool UpdatePromptSeen { get; set; }
    /// <summary>The latest-release version the user dismissed in the update banner; the banner stays hidden for it.</summary>
    public string? DismissedUpdate { get; set; }
    public bool ShowInMenuBar { get; set; } = true;
    public bool AutoDownloadAssets { get; set; } = true;
    /// <summary>Minimize to the system tray (hiding from the taskbar) instead of normal minimize. Off by default.</summary>
    public bool MinimizeToTray { get; set; }
    /// <summary>
    /// Suppress diagnostic logging (no log.txt writes). Off by default — a fresh
    /// install or an upgraded config.toml without the key keeps logging on.
    /// </summary>
    public bool SuppressLogging { get; set; }
    public string? Language { get; set; }
    public int ThumbwheelSensitivity { get; set; } = DefaultThumbwheelSensitivity;

    /// <summary>True when nothing diverges from the default (so the block is omitted).</summary>
    public bool IsDefault() =>
        !LaunchAtLogin && !CheckForUpdates && !UpdatePromptSeen && DismissedUpdate is null
        && ShowInMenuBar && AutoDownloadAssets && !MinimizeToTray && !SuppressLogging
        && Language is null && ThumbwheelSensitivity == DefaultThumbwheelSensitivity;
}
