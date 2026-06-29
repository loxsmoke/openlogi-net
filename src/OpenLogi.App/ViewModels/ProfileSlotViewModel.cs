namespace OpenLogi.App.ViewModels;

/// <summary>
/// One onboard profile slot, or the synthetic "No profile" entry (<see cref="Number"/> 0)
/// that drops the keyboard out of onboard mode and applies the configured custom colour.
/// </summary>
public sealed class ProfileSlotViewModel(int number, bool isCurrent)
{
    /// <summary>One-based profile index sent to the device; 0 = "No profile" (custom colour).</summary>
    public int Number { get; } = number;

    public bool IsCurrent { get; } = isCurrent;
    public bool CanSwitch => !IsCurrent;

    public string Label => Number == 0 ? "No profile" : $"Profile {Number}";
}
