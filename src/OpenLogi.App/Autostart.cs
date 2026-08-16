using Microsoft.Win32;

namespace OpenLogi.App;

/// <summary>
/// Windows launch-at-login via the per-user <c>Run</c> registry key (the
/// HKCU Run-key entry the original uses on Windows — no service, no elevation).
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenLogi";
    public const string StartupArgument = "--minimized";

    /// <summary>Whether the autostart entry is currently present.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    /// <summary>Add or remove the autostart entry, pointing at the running executable.</summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe is not null) key.SetValue(ValueName, $"\"{exe}\" {StartupArgument}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* best effort — registry may be locked down */ }
    }

    /// <summary>Upgrade this app's existing Run-key entry to the current startup command.</summary>
    public static void RefreshCurrentEntry()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not string value) return;
            if (TargetsCurrentExecutable(value, exe))
                key.SetValue(ValueName, $"\"{exe}\" {StartupArgument}");
        }
        catch { /* best effort */ }
    }

    private static bool TargetsCurrentExecutable(string value, string exe)
    {
        var trimmed = value.Trim();
        return string.Equals(trimmed, exe, StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith($"\"{exe}\"", StringComparison.OrdinalIgnoreCase);
    }
}
