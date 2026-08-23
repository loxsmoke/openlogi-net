using System.Runtime.CompilerServices;
using OpenLogi.Core.Logging;

// Loc is a process-wide singleton, so a test that pins the UI language changes it
// for every test running at that moment. Without this, a localization test holding
// German leaks into one asserting an English label (ActionTests, Binding-
// SerializationTests) and the suite fails at random. Serial costs ~nothing here:
// the whole run is about two seconds.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

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
