using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenLogi.Camera;
using OpenLogi.Core.Localization;

namespace OpenLogi.App.ViewModels;

/// <summary>
/// One compact camera-control line: label · slider · live value (· Auto chip when
/// the device pairs one). Mirrors the Rust <c>control_row</c>. The slider updates
/// the label as it moves; the owning page writes the device once the value settles,
/// so a drag doesn't flood the camera with intermediate values.
/// </summary>
public sealed partial class CameraControlRowViewModel : ObservableObject
{
    private readonly CameraViewModel _owner;
    private bool _seating;

    public CameraControlRowViewModel(CameraViewModel owner, CameraControl control, ControlRange range)
    {
        _owner = owner;
        Control = control;
        Range = range;
        _value = range.Current;
        Loc.Current.WeakSubscribe(this, static (self, e) =>
        {
            if (e.PropertyName is nameof(Loc.Culture) or "Item[]")
            {
                self.OnPropertyChanged(nameof(Label));
                self.OnPropertyChanged(nameof(SetValueTip));
            }
        });
    }

    public CameraControl Control { get; }
    public ControlRange Range { get; }

    public double Minimum => Range.Min;
    public double Maximum => Math.Max(Range.Min, Range.Max);

    public string Label => LabelFor(Control);
    public string SetValueTip => Loc.Current.Format("Camera_SetValueLabel", Label);

    /// <summary>The slider position (whole numbers); a user move schedules the device write.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    private double _value;

    public int IntValue => (int)Math.Round(Value);
    public string ValueText => IntValue.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Whether this control has a paired auto mode on the device.</summary>
    [ObservableProperty] private bool _hasAuto;

    /// <summary>Auto owns the value: the row dims, but stays draggable — grabbing it takes over to manual.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    private bool _autoOn;

    public double RowOpacity => AutoOn ? 0.55 : 1.0;

    partial void OnValueChanged(double value)
    {
        if (!_seating) _owner.OnRowMoved(this);
    }

    /// <summary>Move the slider without it counting as a user change.</summary>
    public void Seat(int value)
    {
        _seating = true;
        try { Value = Range.Clamp(value); }
        finally { _seating = false; }
    }

    [RelayCommand] private void ToggleAuto() => _owner.ToggleAuto(this);
    [RelayCommand] private void Reset() => _owner.ResetControl(this);
    [RelayCommand] private Task EditValueAsync() => _owner.EditValueAsync(this);

    public static string LabelFor(CameraControl control) => control switch
    {
        CameraControl.Zoom => Loc.Current["Camera_Zoom"],
        CameraControl.Pan => Loc.Current["Camera_Pan"],
        CameraControl.Tilt => Loc.Current["Camera_Tilt"],
        CameraControl.Focus => Loc.Current["Camera_Focus"],
        CameraControl.Exposure => Loc.Current["Camera_Exposure"],
        CameraControl.PowerLineFrequency => Loc.Current["Camera_AntiFlicker"],
        CameraControl.LowLightCompensation => Loc.Current["Camera_LowLightCompensation"],
        CameraControl.Brightness => Loc.Current["Camera_Brightness"],
        CameraControl.Contrast => Loc.Current["Camera_Contrast"],
        CameraControl.Saturation => Loc.Current["Camera_Saturation"],
        CameraControl.Sharpness => Loc.Current["Camera_Sharpness"],
        CameraControl.Gain => Loc.Current["Camera_Gain"],
        CameraControl.BacklightCompensation => Loc.Current["Camera_BacklightCompensation"],
        CameraControl.WhiteBalance => Loc.Current["Camera_WhiteBalance"],
        _ => Loc.Current["Camera_Tint"],
    };
}

/// <summary>A selectable camera-profile chip: a built-in, a saved custom (deletable), or New.</summary>
public sealed partial class CameraProfileChipViewModel(string id, string label, bool isCustom, bool isNew) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public bool IsCustom { get; } = isCustom;
    public bool IsNew { get; } = isNew;

    [ObservableProperty] private bool _isActive;
}

/// <summary>One frame size in the Resolution dropdown.</summary>
public sealed record CameraResolutionOption(uint Width, uint Height)
{
    public string Label => $"{Width} × {Height}";
}

/// <summary>One rate in the Frame rate dropdown.</summary>
public sealed record CameraRateOption(uint Fps)
{
    public string Label => Loc.Current.Format("Camera_FrameRateFps", Fps);
}

/// <summary>What the exact-value dialog needs: its title, the range hint, the starting text, and a validator.</summary>
public sealed record CameraValuePromptRequest(string Title, string Hint, int Initial, Func<int, bool> Accepts);
