namespace OpenLogi.App.ViewModels;

/// <summary>One EasySwitch host slot: its name, pairing status, bus, and whether it's current.</summary>
public sealed class HostSlotViewModel(int index, bool isCurrent, bool paired, string busType, string? name)
{
    /// <summary>Zero-based host index sent to the device.</summary>
    public int Index { get; } = index;

    /// <summary>One-based number shown in the UI.</summary>
    public int Number { get; } = index + 1;

    public bool IsCurrent { get; } = isCurrent;
    public bool Paired { get; } = paired;

    /// <summary>Switchable only if it's a paired host that isn't the current one.</summary>
    public bool CanSwitch => Paired && !IsCurrent;

    /// <summary>The host's name, or a generic label when unnamed/empty.</summary>
    public string Title => !string.IsNullOrWhiteSpace(name) ? name! : $"Host {Number}";

    /// <summary>A short status line: current/paired/empty plus the bus type.</summary>
    public string Status
    {
        get
        {
            var state = IsCurrent ? "current" : Paired ? "paired" : "empty";
            return string.IsNullOrEmpty(busType) || busType == "Undefined" ? state : $"{state} · {busType}";
        }
    }
}
