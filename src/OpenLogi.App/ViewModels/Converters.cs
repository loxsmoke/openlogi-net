using OpenLogi.Core.Localization;

namespace OpenLogi.App.ViewModels;

/// <summary>One culture-aware row in the lighting-effect dropdown.</summary>
public sealed partial class LightingEffectOption : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public LightingEffectOption(LightingEffect effect)
    {
        Effect = effect;
        Loc.Current.WeakSubscribe(this, static (self, e) =>
        {
            if (e.PropertyName is nameof(Loc.Culture) or "Item[]")
                self.OnPropertyChanged(nameof(Label));
        });
    }

    public LightingEffect Effect { get; }

    public string Label => Effect switch
    {
        LightingEffect.Solid => Loc.Current["Effect_Solid"],
        LightingEffect.Breathing => Loc.Current["Effect_Breathing"],
        LightingEffect.Cycle => Loc.Current["Effect_Cycle"],
        LightingEffect.Off => Loc.Current["Effect_Off"],
        _ => Effect.ToString(),
    };
}
