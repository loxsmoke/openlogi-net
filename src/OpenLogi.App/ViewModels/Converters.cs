using Avalonia.Data.Converters;

namespace OpenLogi.App.ViewModels;

/// <summary>Shared value converters for the views.</summary>
public static class Converters
{
    /// <summary>Friendly display label for a <see cref="LightingEffect"/>.</summary>
    public static readonly IValueConverter EffectLabel =
        new FuncValueConverter<LightingEffect, string>(e => e switch
        {
            LightingEffect.Cycle => "Cycle colors",
            _ => e.ToString(),
        });
}
