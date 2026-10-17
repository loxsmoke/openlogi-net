using Microsoft.Win32;

namespace OpenLogi.App.Tray;

/// <summary>
/// Whether the Windows taskbar is dark. The taskbar follows the <em>system</em>
/// theme ("SystemUsesLightTheme"), which is a separate switch from the app theme
/// Avalonia reports, so this reads the registry directly. A missing value means
/// the Windows default, which is dark.
/// </summary>
public static class TaskbarTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string SystemUsesLightTheme = "SystemUsesLightTheme";

    /// <summary>Read fresh on every access, so a theme switch is picked up on the next redraw.</summary>
    public static bool IsDark
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue(SystemUsesLightTheme) is not int light || light == 0;
            }
            catch
            {
                return true;
            }
        }
    }
}
