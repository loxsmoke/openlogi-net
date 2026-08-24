using CommunityToolkit.Mvvm.ComponentModel;
using OpenLogi.Core.Localization;

namespace OpenLogi.App.ViewModels;

/// <summary>
/// One onboard profile slot, or the synthetic "No profile" entry (<see cref="Number"/> 0)
/// that drops the keyboard out of onboard mode and applies the configured custom colour.
/// </summary>
public sealed partial class ProfileSlotViewModel : ObservableObject
{
    public ProfileSlotViewModel(int number, bool isCurrent)
    {
        Number = number;
        IsCurrent = isCurrent;
        Loc.Current.WeakSubscribe(this, static (self, e) =>
        {
            if (e.PropertyName is nameof(Loc.Culture) or "Item[]")
                self.OnPropertyChanged(nameof(Label));
        });
    }

    /// <summary>One-based profile index sent to the device; 0 = "No profile" (custom colour).</summary>
    public int Number { get; }

    public bool IsCurrent { get; }
    public bool CanSwitch => !IsCurrent;

    public string Label => Number == 0
        ? Loc.Current["Profile_None"]
        : Loc.Current.Format("Profile_Numbered", Number);
}
