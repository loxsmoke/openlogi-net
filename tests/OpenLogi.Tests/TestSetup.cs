using System.Runtime.CompilerServices;
using OpenLogi.Core.Logging;

namespace OpenLogi.Tests;

/// <summary>Assembly-wide test environment setup, run once before any test.</summary>
internal static class TestSetup
{
    /// <summary>
    /// Code under test logs through the static <see cref="DiagnosticLog"/>, which
    /// lazily opens the user's real log file (%LOCALAPPDATA%\OpenLogi\logs) —
    /// suppress before anything writes, so test runs never pollute it.
    /// <see cref="LogFile"/> tests are unaffected: they construct instances
    /// directly against temp directories.
    /// </summary>
    [ModuleInitializer]
    internal static void SuppressDiagnosticLog() => DiagnosticLog.Suppressed = true;
}
