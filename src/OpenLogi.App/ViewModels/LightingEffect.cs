namespace OpenLogi.App.ViewModels;

/// <summary>Keyboard lighting effect modes (live app-driven, or saved into a profile).</summary>
public enum LightingEffect
{
    /// <summary>One static colour.</summary>
    Solid,
    /// <summary>The colour fades in and out.</summary>
    Breathing,
    /// <summary>Hue cycles through the spectrum (ignores the chosen colour).</summary>
    Cycle,
    /// <summary>Lighting off.</summary>
    Off,
}
