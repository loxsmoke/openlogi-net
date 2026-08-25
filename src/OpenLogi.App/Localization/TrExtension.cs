using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using OpenLogi.Core.Localization;

namespace OpenLogi.App.Localization;

/// <summary>
/// The <c>{loc:Tr Key}</c> markup extension: replaces a compiled-in literal with a
/// live binding, so the language can change without recreating windows.
///
/// It returns a <see cref="CompiledBinding"/> built in C#, which matters twice over. The
/// binding sets <see cref="CompiledBinding.Source"/> explicitly and never touches the
/// DataContext, so it coexists with the compiled bindings enabled project-wide
/// (<c>AvaloniaUseCompiledBindingsByDefault</c>) without an
/// <c>x:CompileBindings="False"</c> escape hatch that would cost type-checking
/// across the whole file.
///
/// The path is <see cref="Loc.Culture"/> — a plain CLR property — rather than the
/// indexer, with the key carried as the converter parameter. Binding a property
/// avoids depending on indexer path-parsing, and re-evaluates on exactly the
/// signal that matters: the culture changed.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    /// <summary>Resource key, as the positional argument in <c>{loc:Tr Key}</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        CompiledBinding.Create<Loc, CultureInfo>(
            loc => loc.Culture,
            Loc.Current,
            LookupConverter.Instance,
            BindingMode.OneWay,
            converterParameter: Key);

    /// <summary>Turns the culture-changed tick into the looked-up string for one key.</summary>
    private sealed class LookupConverter : IValueConverter
    {
        internal static readonly LookupConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            Loc.Current.Get(parameter as string ?? string.Empty);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException("localized text is one-way");
    }
}
