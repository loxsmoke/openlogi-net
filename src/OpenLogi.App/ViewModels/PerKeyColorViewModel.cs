using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenLogi.Hid;

namespace OpenLogi.App.ViewModels;

/// <summary>A key that can't be typed normally (OS-intercepted), painted by clicking. Zone = HID usage − 3.</summary>
public sealed record SpecialKey(string Label, byte Zone);

/// <summary>
/// Press-to-color editor: pick a color, then press a physical key to paint it
/// (press again to reset it to the base). Uses the G915 zone formula
/// <c>zone = HID usage − 3</c>, so no per-key calibration is needed.
/// </summary>
public sealed partial class PerKeyColorViewModel : ObservableObject
{
    private readonly DeviceSession _session;
    private readonly Dictionary<byte, Color> _painted = [];

    [ObservableProperty] private Color _selectedColor = Colors.Red;
    [ObservableProperty] private Color _baseColor = Colors.White;
    [ObservableProperty] private string _status = "Pick a color, then press a key to paint it. Press it again to reset.";

    public PerKeyColorViewModel(DeviceSession session) => _session = session;

    /// <summary>Keys the OS swallows (so they can't be pressed into the window) — click to paint. Zones = usage − 3.</summary>
    public IReadOnlyList<SpecialKey> SpecialKeys { get; } =
    [
        // Hardware-mapped zones (modifiers are NOT at usage-3 on the G915).
        new("PrtSc", 0x43), new("Menu", 0x62),
        new("L Ctrl", 0x68), new("L Shift", 0x69), new("L Alt", 0x66),
        // Win + right-side modifiers: still to be probed.
    ];

    [RelayCommand]
    private async Task PaintSpecial(SpecialKey? key)
    {
        if (key is not null) await PaintZoneAsync(key.Zone, key.Label);
    }

    /// <summary>Set the whole keyboard to the base color (enters host mode) when the window opens.</summary>
    public async Task InitAsync() => await _session.ApplyPerKeyColorAsync(BaseColor.R, BaseColor.G, BaseColor.B);

    /// <summary>Handle a physical key press: paint that key, or reset it if already painted with this color.</summary>
    public async Task PressAsync(PhysicalKey physical)
    {
        if (PhysicalToZone(physical) is not { } zone)
        {
            Status = $"{physical} isn't a paintable key.";
            return;
        }
        await PaintZoneAsync(zone, physical.ToString());
    }

    /// <summary>The LED zone for a physical key. Modifiers aren't at usage−3 on the G915, so they're explicit.</summary>
    private static byte? PhysicalToZone(PhysicalKey k) => k switch
    {
        PhysicalKey.ControlLeft => 0x68,
        PhysicalKey.ShiftLeft => 0x69,
        PhysicalKey.AltLeft => 0x66,
        _ => PhysicalToUsage(k) is { } u && u >= 3 ? (byte)(u - 3) : null,
    };

    /// <summary>Paint a zone with the selected color, or reset it to base if already that color.</summary>
    private async Task PaintZoneAsync(byte zone, string label)
    {
        if (_painted.TryGetValue(zone, out var cur) && cur == SelectedColor)
        {
            _painted.Remove(zone);
            await _session.SetZoneAsync(zone, BaseColor.R, BaseColor.G, BaseColor.B);
            Status = $"{label} reset.";
        }
        else
        {
            _painted[zone] = SelectedColor;
            await _session.SetZoneAsync(zone, SelectedColor.R, SelectedColor.G, SelectedColor.B);
            Status = $"{label} → #{SelectedColor.R:x2}{SelectedColor.G:x2}{SelectedColor.B:x2}";
        }
    }

    /// <summary>Reset every painted key back to the base color.</summary>
    public async Task ResetAllAsync()
    {
        _painted.Clear();
        await _session.ApplyPerKeyColorAsync(BaseColor.R, BaseColor.G, BaseColor.B);
        Status = "All keys reset to base.";
    }

    /// <summary>Map a physical key position to its HID keyboard usage (null = not paintable). Layout-independent.</summary>
    private static byte? PhysicalToUsage(PhysicalKey k) => k switch
    {
        >= PhysicalKey.A and <= PhysicalKey.Z => (byte)(0x04 + (k - PhysicalKey.A)),
        PhysicalKey.Digit0 => 0x27,
        >= PhysicalKey.Digit1 and <= PhysicalKey.Digit9 => (byte)(0x1e + (k - PhysicalKey.Digit1)),
        >= PhysicalKey.F1 and <= PhysicalKey.F12 => (byte)(0x3a + (k - PhysicalKey.F1)),
        PhysicalKey.Enter => 0x28,
        PhysicalKey.Escape => 0x29,
        PhysicalKey.Backspace => 0x2a,
        PhysicalKey.Tab => 0x2b,
        PhysicalKey.Space => 0x2c,
        PhysicalKey.Minus => 0x2d,
        PhysicalKey.Equal => 0x2e,
        PhysicalKey.BracketLeft => 0x2f,
        PhysicalKey.BracketRight => 0x30,
        PhysicalKey.Backslash => 0x31,
        PhysicalKey.Semicolon => 0x33,
        PhysicalKey.Quote => 0x34,
        PhysicalKey.Backquote => 0x35,
        PhysicalKey.Comma => 0x36,
        PhysicalKey.Period => 0x37,
        PhysicalKey.Slash => 0x38,
        PhysicalKey.CapsLock => 0x39,
        PhysicalKey.PrintScreen => 0x46,
        PhysicalKey.ScrollLock => 0x47,
        PhysicalKey.Pause => 0x48,
        PhysicalKey.Insert => 0x49,
        PhysicalKey.Home => 0x4a,
        PhysicalKey.PageUp => 0x4b,
        PhysicalKey.Delete => 0x4c,
        PhysicalKey.End => 0x4d,
        PhysicalKey.PageDown => 0x4e,
        PhysicalKey.ArrowRight => 0x4f,
        PhysicalKey.ArrowLeft => 0x50,
        PhysicalKey.ArrowDown => 0x51,
        PhysicalKey.ArrowUp => 0x52,
        // Numeric keypad.
        PhysicalKey.NumLock => 0x53,
        PhysicalKey.NumPadDivide => 0x54,
        PhysicalKey.NumPadMultiply => 0x55,
        PhysicalKey.NumPadSubtract => 0x56,
        PhysicalKey.NumPadAdd => 0x57,
        PhysicalKey.NumPadEnter => 0x58,
        PhysicalKey.NumPad0 => 0x62,
        >= PhysicalKey.NumPad1 and <= PhysicalKey.NumPad9 => (byte)(0x59 + (k - PhysicalKey.NumPad1)),
        PhysicalKey.NumPadDecimal => 0x63,
        // Modifiers (HID usage page 0xE0-0xE7).
        PhysicalKey.ControlLeft => 0xe0,
        PhysicalKey.ShiftLeft => 0xe1,
        PhysicalKey.AltLeft => 0xe2,
        PhysicalKey.MetaLeft => 0xe3,
        PhysicalKey.ControlRight => 0xe4,
        PhysicalKey.ShiftRight => 0xe5,
        PhysicalKey.AltRight => 0xe6,
        PhysicalKey.MetaRight => 0xe7,
        PhysicalKey.ContextMenu => 0x65,
        _ => null,
    };
}
