using System.Text;
using System.Text.RegularExpressions;

namespace OpenLogi.Tests.App;

/// <summary>
/// Guards the string table against drift: any user-facing text added to the UI
/// projects has to go through <c>{loc:Tr}</c> or <c>Loc.Current</c>, or be listed
/// here as deliberately untranslated.
///
/// This is a lint over source text, not a semantic analysis — it flags literals
/// that <em>look</em> like prose. When it catches something that genuinely should
/// stay English, add it to <see cref="ExemptFiles"/> or <see cref="ExemptText"/>
/// with the reason; that list is the record of what was decided and why.
/// </summary>
public class TranslationCoverageTests
{
    /// <summary>The projects that render UI. Hid/HidPP/Agent/Assets/Cli carry no user-facing text.</summary>
    private static readonly string[] ScannedProjects = ["OpenLogi.App", "OpenLogi.Core"];

    /// <summary>
    /// Files whose every string is non-UI by nature. Whole-file exemptions, each
    /// with the reason it will never need translating.
    /// </summary>
    private static readonly Dictionary<string, string> ExemptFiles = new()
    {
        // Diagnostic log and its plumbing: a translated log makes an issue report
        // unreadable to the maintainer. Deliberately English forever.
        ["OpenLogi.Core/Logging/LogFile.cs"] = "diagnostic log text",
        ["OpenLogi.Core/Logging/DiagnosticLog.cs"] = "diagnostic log text",

        // Exception messages and TOML key names — read by developers and by the
        // config parser, never shown in the UI.
        ["OpenLogi.Core/Config/Config.cs"] = "exception messages",
        ["OpenLogi.Core/Config/ConfigCodec.cs"] = "exception messages and config keys",

        // Windows version strings and the Logitech process names used for
        // detection; both are matched against the system, not displayed as prose.
        ["OpenLogi.App/SystemInfo.cs"] = "OS strings and process names for detection",

        // Filesystem and download plumbing: folder names, executables, byte-count
        // errors headed for the log.
        ["OpenLogi.App/Services/UpdateInstaller.cs"] = "filesystem and protocol strings",

        // Avalonia's view-location convention ("View" suffix, missing-view text).
        ["OpenLogi.App/ViewLocator.cs"] = "framework plumbing",

        // Physical key legends. These follow the keyboard, not the UI language: a
        // German user on a QWERTZ board wants what is printed on the key.
        ["OpenLogi.App/ViewModels/KeyboardLayout.cs"] = "physical key legends",
    };

    /// <summary>
    /// Individual literals that stay English inside otherwise-translated files.
    /// Interpolation holes are collapsed to <c>{}</c> before matching (see
    /// <see cref="CodeLiterals"/>), so entries here are written that way too.
    /// </summary>
    private static readonly HashSet<string> ExemptText =
    [
        // Avalonia control names looked up by name, not shown to anyone.
        "Shell", "Fill",

        // Language endonym: a language names itself in its own language.
        "English US",

        // Developer-facing exception, never surfaced in the UI.
        "localized text is one-way",

        // Value reported by the device firmware for an unset bus type; compared,
        // not displayed.
        "Undefined",

        // Physical key legends on the per-key colour editor's special-key rows —
        // same reasoning as KeyboardLayout.cs above.
        "Logo", "Pause", "Menu", "Prev", "Next", "Mute",
        "R Shift", "R Ctrl", "R Win", "R Alt",

        // The About window's copy-to-clipboard diagnostics blob. It goes into bug
        // reports, so it stays English for the same reason the log does.
        "Logitech software: {}",
    ];

    private static readonly Regex XamlAttribute = new(
        @"(?<![A-Za-z])(Text|Content|Header|Title|Watermark|PlaceholderText|ToolTip\.Tip)=""([^""{][^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex XamlComment = new("<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Literal = new("\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex InterpolationHole = new("[{][^{}]*[}]", RegexOptions.Compiled);
    private static readonly Regex TwoLetters = new("[A-Za-z]{2}", RegexOptions.Compiled);

    /// <summary>Prose: two letter-runs separated by a space ("Left Click", "is ready").</summary>
    private static readonly Regex Prose = new("[A-Za-z] [A-Za-z]", RegexOptions.Compiled);

    /// <summary>A lone capitalised word is prose too — "Buttons", "Asleep".</summary>
    private static readonly Regex SingleWord = new("^[A-Z][a-z]{3,}$", RegexOptions.Compiled);

    /// <summary>Identifiers, paths, URLs, SVG path data, format specifiers — not language.</summary>
    private static readonly Regex Technical = new(
        """^[a-z0-9_.-]+$|^[A-Z_]+$|^https?:|\.(cs|exe|dll|png|ico|toml|txt|json|resx|md)$|^avares:|^openlogi:|\\|^\{|^[0-9]|^[MLACZ][ 0-9]""",
        RegexOptions.Compiled);

    /// <summary>Lines whose strings are log or developer output, not UI.</summary>
    private static readonly string[] NonUiCalls = ["DiagnosticLog.", "Debug.WriteLine", "Console.Write", "nameof("];

    [Fact]
    public void NoUntranslatedTextInXaml()
    {
        var found = new List<string>();
        foreach (var (path, relative) in ScannedFiles("*.axaml"))
        {
            var src = File.ReadAllText(path);
            var comments = XamlComment.Matches(src).Select(m => (m.Index, End: m.Index + m.Length)).ToList();
            foreach (Match m in XamlAttribute.Matches(src))
            {
                if (comments.Any(c => c.Index <= m.Index && m.Index < c.End)) continue;
                var value = m.Groups[2].Value;
                if (!TwoLetters.IsMatch(value) || ExemptText.Contains(value)) continue;
                found.Add($"{relative}:{LineOf(src, m.Index)}  {m.Groups[1].Value}=\"{value}\"");
            }
        }
        Assert.True(found.Count == 0, Report(found, "use {loc:Tr Key}"));
    }

    [Fact]
    public void NoUntranslatedTextInCode()
    {
        var found = new List<string>();
        foreach (var (path, relative) in ScannedFiles("*.cs"))
            foreach (var (line, text) in CodeLiterals(path))
                if (!ExemptText.Contains(text))
                    found.Add($"{relative}:{line}  \"{text}\"");
        Assert.True(found.Count == 0, Report(found, "use Loc.Current[\"Key\"] or Loc.Current.Format"));
    }

    /// <summary>
    /// Prose string literals in one C# file. Log and developer-output calls are
    /// skipped whole (including the continuation lines of a multi-line call), and
    /// interpolation holes are blanked to <c>{}</c> first so that quotes belonging
    /// to code inside a hole are not mistaken for text.
    /// </summary>
    private static IEnumerable<(int Line, string Text)> CodeLiterals(string path)
    {
        var skippingCall = false;
        var lineNumber = 0;
        foreach (var raw in File.ReadLines(path))
        {
            lineNumber++;
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith("//") || trimmed.StartsWith('*') || trimmed.StartsWith("/*")) continue;

            if (skippingCall)
            {
                if (trimmed.TrimEnd().EndsWith(';')) skippingCall = false;
                continue;
            }
            if (NonUiCalls.Any(raw.Contains))
            {
                if (!trimmed.TrimEnd().EndsWith(';')) skippingCall = true;
                continue;
            }

            var code = InterpolationHole.Replace(StripTrailingComment(raw), "{}");
            foreach (Match m in Literal.Matches(code))
            {
                var text = m.Groups[1].Value;
                if (text.Length < 3 || Technical.IsMatch(text)) continue;
                if (Prose.IsMatch(text) || SingleWord.IsMatch(text))
                    yield return (lineNumber, text);
            }
        }
    }

    /// <summary>
    /// Drop a trailing <c>//</c> comment, which often quotes UI text while
    /// explaining code ("RebuildProfiles(0); // "No profile" selected"). Tracks
    /// string state so a <c>//</c> inside a literal — a URL, say — is left alone.
    /// </summary>
    private static string StripTrailingComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length - 1; i++)
        {
            var c = line[i];
            if (c == '\\' && inString) { i++; continue; }
            if (c == '"') inString = !inString;
            else if (!inString && c == '/' && line[i + 1] == '/') return line[..i];
        }
        return line;
    }

    private static IEnumerable<(string Path, string Relative)> ScannedFiles(string pattern)
    {
        var src = LocalizationTests.SrcRoot();
        foreach (var project in ScannedProjects)
            foreach (var path in Directory.EnumerateFiles(Path.Combine(src, project), pattern, SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                var relative = Path.GetRelativePath(src, path).Replace(Path.DirectorySeparatorChar, '/');
                if (ExemptFiles.ContainsKey(relative)) continue;
                yield return (path, relative);
            }
    }

    private static int LineOf(string text, int index) => text.Take(index).Count(c => c == '\n') + 1;

    private static string Report(List<string> found, string fix)
    {
        var sb = new StringBuilder()
            .AppendLine($"{found.Count} string(s) look user-facing but are not localized.")
            .AppendLine($"Either {fix}, or add the text to ExemptFiles/ExemptText in TranslationCoverageTests with a reason.")
            .AppendLine();
        foreach (var f in found) sb.AppendLine("  " + f);
        return sb.ToString();
    }
}
