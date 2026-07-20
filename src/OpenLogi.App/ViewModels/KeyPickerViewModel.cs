using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OpenLogi.App.ViewModels;

/// <summary>One key in the picker grid, with live selected/active highlight.</summary>
public sealed partial class KeyCapViewModel : ObservableObject
{
    public KeyCap Cap { get; }
    public string Label => Cap.Label;
    public double PixelWidth => Cap.Width * 40;
    public bool IsEnabled => Cap.Kind != KeyKind.Disabled;
    [ObservableProperty] private bool _isActive;

    public KeyCapViewModel(KeyCap cap) => Cap = cap;
}

/// <summary>View-model for the visual keyboard key picker (choose a key + modifiers).</summary>
public sealed partial class KeyPickerViewModel : ObservableObject
{
    public IReadOnlyList<IReadOnlyList<KeyCapViewModel>> Rows { get; }

    [ObservableProperty] private string _preview = "—";

    private byte _usage;
    private byte _modifier;

    public byte ResultUsage => _usage;
    public byte ResultModifier => _modifier;

    public KeyPickerViewModel(byte usage, byte modifier)
    {
        Rows = KeyboardLayout.Rows.Select(r => (IReadOnlyList<KeyCapViewModel>)r.Select(c => new KeyCapViewModel(c)).ToList()).ToList();
        _usage = usage;
        _modifier = modifier;
        Refresh();
    }

    [RelayCommand]
    private void Select(KeyCapViewModel? key)
    {
        if (key is null || !key.IsEnabled) return;
        if (key.Cap.Kind == KeyKind.Modifier)
            _modifier ^= key.Cap.ModifierBit; // toggle this modifier
        else
            _usage = key.Cap.Usage;
        Refresh();
    }

    private void Refresh()
    {
        foreach (var k in Rows.SelectMany(r => r))
            k.IsActive = k.Cap.Kind == KeyKind.Modifier
                ? (_modifier & k.Cap.ModifierBit) != 0
                : k.Cap.Usage != 0 && k.Cap.Usage == _usage;
        Preview = KeyboardLayout.DescribeBinding(_usage, _modifier);
    }
}
