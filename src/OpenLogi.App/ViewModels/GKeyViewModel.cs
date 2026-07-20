using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenLogi.App.ViewModels;

/// <summary>One remappable G-key: its number and the key/combo it sends (set via the visual picker).</summary>
public sealed partial class GKeyViewModel : ObservableObject
{
    public int Index { get; }
    public int Number { get; }
    public string Label => $"G{Number}";

    private readonly Action<int, byte, byte> _onChange;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(CurrentLabel))] private byte _usage;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CurrentLabel))] private byte _modifier;

    /// <summary>Human-readable current binding, e.g. "Ctrl + C" or "F1".</summary>
    public string CurrentLabel => KeyboardLayout.DescribeBinding(Usage, Modifier);

    public GKeyViewModel(int index, byte usage, byte modifier, Action<int, byte, byte> onChange)
    {
        Index = index;
        Number = index + 1;
        Usage = usage;
        Modifier = modifier;
        _onChange = onChange;
    }

    /// <summary>Apply a new binding chosen in the picker (updates the label + writes to the device).</summary>
    public void Apply(byte usage, byte modifier)
    {
        Usage = usage;
        Modifier = modifier;
        _onChange(Index, usage, modifier);
    }
}
