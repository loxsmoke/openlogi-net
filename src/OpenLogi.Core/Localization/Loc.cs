using System;
using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace OpenLogi.Core.Localization;

/// <summary>
/// The app's string table. Avalonia has no built-in XAML localization (no
/// <c>x:Uid</c>, no LocBaml): XamlX compiles every literal into a constant, so
/// localized text has to arrive through something that evaluates at runtime.
/// This singleton is that source — <see cref="TrExtension"/> binds to it, and a
/// culture change re-evaluates every localized binding in place, with no window
/// recreation.
///
/// English is the neutral resource set, and German currently ships as a
/// satellite resource. Other cultures resolve through <see cref="ResourceManager"/>'s
/// own fallback chain (<c>de-AT</c> → <c>de</c> → neutral).
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>
    /// Base name of the embedded resource set: root namespace + folder + file, as
    /// the SDK names <c>Localization\Strings.resx</c>. It lives in Core rather than
    /// the app because the action, button and gesture labels users read most often
    /// are Core's, and one table beats two.
    /// </summary>
    private const string ResourceBaseName = "OpenLogi.Core.Localization.Strings";

    private static readonly ResourceManager Resources = new(ResourceBaseName, typeof(Loc).Assembly);

    /// <summary>The single instance every localized binding reads from.</summary>
    public static Loc Current { get; } = new();

    /// <summary>
    /// The OS UI language, captured at type load — before <see cref="SetCulture"/>
    /// can overwrite <see cref="CultureInfo.CurrentUICulture"/>. Re-reading the
    /// thread culture later would return whatever the app last applied, which
    /// would make "System" mean "whatever I picked last time".
    /// </summary>
    public static CultureInfo SystemUiCulture { get; } = CultureInfo.CurrentUICulture;

    private Loc() { }

    /// <summary>The culture strings are currently resolved in.</summary>
    public CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;

    /// <summary>
    /// Look up <paramref name="key"/>. Bindings go through this indexer's
    /// <see cref="Culture"/> dependency rather than the indexer itself, so the
    /// path stays a plain property — see <see cref="TrExtension"/>.
    /// </summary>
    public string this[string key] => Get(key);

    /// <summary>
    /// The string for <paramref name="key"/>, or the key itself when it is missing.
    /// Rendering the key makes a gap visible in the UI instead of leaving a blank
    /// label, and keeps a typo from throwing mid-layout.
    /// </summary>
    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        try { return Resources.GetString(key, Culture) ?? key; }
        catch (MissingManifestResourceException) { return key; }
    }

    /// <summary>
    /// Look up a composite-format string and fill it in. Formatting follows
    /// CurrentCulture (OS regional settings) while the text follows the UI
    /// language, so a number lands in the reader's own notation.
    /// </summary>
    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>
    /// Switch the UI language. Also sets the thread and default-thread UI cultures
    /// so anything reading <see cref="CultureInfo.CurrentUICulture"/> agrees.
    /// <see cref="CultureInfo.CurrentCulture"/> is deliberately left alone: number
    /// and date formatting should follow the OS regional settings, not the UI
    /// language a user picked to read menus in.
    /// </summary>
    public void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (Culture.Equals(culture)) return;

        Culture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        // Culture first: TrExtension's bindings watch it. "Item[]" is the
        // conventional "every indexer value changed" notification, for anything
        // binding the indexer directly.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    /// <summary>
    /// Subscribe to culture changes without letting this process-wide singleton root
    /// transient UI rows after their collections are rebuilt.
    /// </summary>
    public IDisposable WeakSubscribe<TTarget>(TTarget target, Action<TTarget, PropertyChangedEventArgs> onChanged)
        where TTarget : class
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(onChanged);

        var weak = new WeakReference<TTarget>(target);
        PropertyChangedEventHandler? handler = null;
        handler = (_, e) =>
        {
            if (weak.TryGetTarget(out var alive))
            {
                onChanged(alive, e);
                return;
            }

            if (handler is not null)
                PropertyChanged -= handler;
        };
        PropertyChanged += handler;
        return new Subscription(() => PropertyChanged -= handler);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => System.Threading.Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
