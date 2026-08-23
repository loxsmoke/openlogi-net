using Avalonia.Data.Converters;
using OpenLogi.Core.Localization;

namespace OpenLogi.App.ViewModels;

/// <summary>Shared value converters for the views.</summary>
public static class Converters
{
    /// <summary>Friendly display label for a <see cref="LightingEffect"/>.</summary>
    public static readonly IValueConverter EffectLabel =
        new FuncValueConverter<LightingEffect, string>(e => e switch
        {
            LightingEffect.Cycle => Loc.Current["Effect_Cycle"],
            _ => e.ToString(),
        });
}
