using CommunityToolkit.Mvvm.ComponentModel;
using OpenLogi.Core.Localization;

namespace OpenLogi.App.ViewModels;

/// <summary>One EasySwitch host slot: its name, pairing status, bus, and whether it's current.</summary>
public sealed partial class HostSlotViewModel : ObservableObject
{
    private readonly string _busType;
    private readonly string? _name;
    private readonly bool _supportsDelete;

    public HostSlotViewModel(int index, bool isCurrent, bool paired, string busType, string? name, bool supportsDelete)
    {
        Index = index;
        Number = index + 1;
        IsCurrent = isCurrent;
        Paired = paired;
        _busType = busType;
        _name = name;
        _supportsDelete = supportsDelete;
        Loc.Current.WeakSubscribe(this, static (self, e) =>
        {
            if (e.PropertyName is nameof(Loc.Culture) or "Item[]")
            {
                self.OnPropertyChanged(nameof(Title));
                self.OnPropertyChanged(nameof(Status));
            }
        });
    }

    /// <summary>Zero-based host index sent to the device.</summary>
    public int Index { get; }

    /// <summary>One-based number shown in the UI.</summary>
    public int Number { get; }

    public bool IsCurrent { get; }
    public bool Paired { get; }

    /// <summary>Switchable only if it's a paired host that isn't the current one.</summary>
    public bool CanSwitch => Paired && !IsCurrent;

    /// <summary>
    /// Forgettable if it's an active pairing (paired, or the current host) and the
    /// device supports clearing. Forgetting the current host is allowed but warned
    /// about, since it disconnects the device from this computer.
    /// </summary>
    public bool CanClear => (Paired || IsCurrent) && _supportsDelete;

    /// <summary>The host's name, or a generic label when unnamed/empty.</summary>
    public string Title => !string.IsNullOrWhiteSpace(_name) ? _name! : Loc.Current.Format("Host_Unnamed", Number);

    /// <summary>A short status line: current/paired/empty plus the bus type.</summary>
    public string Status
    {
        get
        {
            var state = Loc.Current[IsCurrent ? "Host_Current" : Paired ? "Host_Paired" : "Host_Empty"];
            return string.IsNullOrEmpty(_busType) || _busType == "Undefined" ? state : $"{state} · {_busType}";
        }
    }
}
