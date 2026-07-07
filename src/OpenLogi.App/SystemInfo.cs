using System.Diagnostics;
using Microsoft.Win32;

namespace OpenLogi.App;

/// <summary>
/// Diagnostic system facts for the About window: the Windows build (Logitech
/// gates device features on specific updates — e.g. Bolt 5.5.30 haptics needs
/// the Windows 11 March 2026 update — and feature updates are the usual cause
/// of receiver driver breakage) and any Logitech software that could contend
/// with us for the receiver.
/// </summary>
public static class SystemInfo
{
    /// <summary>
    /// Human-readable Windows version, e.g. "Windows 11 Pro 24H2 (build 26200.2110)".
    /// Read from the registry: ProductName still says "Windows 10" on Windows 11,
    /// so the marketing name is corrected by build number (22000+).
    /// </summary>
    public static string WindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var display = key?.GetValue("DisplayVersion") as string;
            var build = int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out var b) ? b : 0;
            var patch = key?.GetValue("UBR") is int u ? u : 0;
            if (build >= 22000) product = product.Replace("Windows 10", "Windows 11");
            var displayPart = string.IsNullOrEmpty(display) ? "" : $" {display}";
            var buildPart = build > 0 ? $" (build {build}.{patch})" : "";
            return $"{product}{displayPart}{buildPart}";
        }
        catch
        {
            return $"Windows (build {Environment.OSVersion.Version.Build})";
        }
    }

    /// <summary>
    /// Current process memory usage, e.g. "Memory: 142 MB working set (96 MB private, 38 MB managed)".
    /// Working set is what Task Manager's default column shows — the number users
    /// compare against; private + managed split says whether growth is ours or the GC's.
    /// </summary>
    public static string MemoryUsage()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            var workingSet = p.WorkingSet64 / (1024 * 1024);
            var privateBytes = p.PrivateMemorySize64 / (1024 * 1024);
            var managed = GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024);
            return $"Memory: {workingSet} MB working set ({privateBytes} MB private, {managed} MB managed)";
        }
        catch
        {
            return "Memory: unavailable";
        }
    }

    /// <summary>
    /// Logitech software known to hold the HID++ conversation with receivers/devices,
    /// with its default install directory and agent process names. Only one app at a
    /// time can reliably own a receiver, so any of these running alongside us means
    /// flaky reads/writes.
    /// </summary>
    private static readonly (string Name, string[] Processes, string[] InstallDirs)[] KnownSoftware =
    [
        ("Logi Options+", ["logioptionsplus_agent", "logioptionsplus"], [@"LogiOptionsPlus"]),
        ("Logitech G HUB", ["lghub", "lghub_agent", "lghub_updater"], [@"LGHUB"]),
        ("Logitech Options", ["LogiOptions", "LogiOptionsMgr"], [@"Logitech\LogiOptions"]),
        ("Logi Bolt app", ["LogiBolt"], [@"Logi\LogiBolt", @"Logitech\LogiBolt"]),
        ("Logitech SetPoint", ["SetPoint"], [@"Logitech\SetPointP"]),
    ];

    /// <summary>
    /// Detect Logitech software on this machine, worst case first: entries like
    /// "Logi Options+ (running)" or "Logitech G HUB (installed)". Running matters
    /// most (active receiver contention); installed-but-idle is informational.
    /// </summary>
    public static IReadOnlyList<string> DetectLogitechSoftware()
    {
        var processNames = RunningProcessNames();

        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];

        var running = new List<string>();
        var installed = new List<string>();
        foreach (var (name, processes, dirs) in KnownSoftware)
        {
            if (processes.Any(processNames.Contains))
                running.Add($"{name} (running)");
            else if (dirs.Any(d => roots.Where(r => !string.IsNullOrEmpty(r))
                                        .Any(r => Directory.Exists(Path.Combine(r, d)))))
                installed.Add($"{name} (installed)");
        }
        return [.. running, .. installed];
    }

    /// <summary>
    /// The names of Logitech apps <em>currently running</em> (bare, e.g. "Logi
    /// Options+"), which actively contend for the receiver. Drives the main
    /// window's contention banner; installed-but-idle software is excluded because
    /// it isn't touching the device.
    /// </summary>
    public static IReadOnlyList<string> DetectRunningLogitechSoftware()
    {
        var processNames = RunningProcessNames();
        return [.. KnownSoftware.Where(s => s.Processes.Any(processNames.Contains)).Select(s => s.Name)];
    }

    /// <summary>Set of process names running now (case-insensitive); empty if the list is unavailable.</summary>
    private static HashSet<string> RunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                names.Add(p.ProcessName);
                p.Dispose();
            }
        }
        catch { /* process list unavailable — caller treats as "nothing running" */ }
        return names;
    }
}
