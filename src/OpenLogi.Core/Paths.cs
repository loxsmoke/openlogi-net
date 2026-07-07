namespace OpenLogi.Core;

/// <summary>
/// Per-OS application directories. Ported from Rust <c>openlogi-core::paths</c>,
/// but using native Windows locations rather than the Rust crate's XDG layout
/// (the Rust note called Windows "best-effort until a real Windows port lands"
/// — this is that port):
///
/// <list type="bullet">
///   <item>config → <c>%APPDATA%\OpenLogi</c> (roaming)</item>
///   <item>data / asset cache → <c>%LOCALAPPDATA%\OpenLogi</c></item>
/// </list>
/// </summary>
public static class Paths
{
    /// <summary>Subdirectory created under each base directory.</summary>
    private const string AppDir = "OpenLogi";

    /// <summary>Directory holding the user's <c>config.toml</c> (<c>%APPDATA%\OpenLogi</c>).</summary>
    public static string ConfigDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppDir);

    /// <summary>Full path to the user config file.</summary>
    public static string ConfigPath() => Path.Combine(ConfigDir(), "config.toml");

    /// <summary>
    /// Directory for downloaded application data (<c>%LOCALAPPDATA%\OpenLogi</c>);
    /// the device-render asset cache lives under <c>DataDir()/assets</c>.
    /// </summary>
    public static string DataDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDir);

    /// <summary>Directory for the device-render asset cache.</summary>
    public static string AssetCacheDir() => Path.Combine(DataDir(), "assets");

    /// <summary>Directory for diagnostic logs (<c>%LOCALAPPDATA%\OpenLogi\logs</c>).</summary>
    public static string LogDir() => Path.Combine(DataDir(), "logs");

    /// <summary>Full path to the current diagnostic log file.</summary>
    public static string LogPath() => Path.Combine(LogDir(), "log.txt");

    /// <summary>
    /// Name of the agent's named-pipe IPC endpoint. Windows named pipes are not
    /// filesystem paths; the full pipe path is <c>\\.\pipe\OpenLogi.agent</c>.
    /// Kept here so any future GUI/agent split has a single source of truth.
    /// </summary>
    public const string AgentPipeName = "OpenLogi.agent";
}
