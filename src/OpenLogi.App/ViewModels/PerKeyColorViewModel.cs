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
    // Persist the editor's state (painted keys, base color, paint color) on change.
    private readonly Action<IReadOnlyDictionary<byte, Color>, Color, Color>? _save;

    [ObservableProperty] private Color _selectedColor = Colors.Red;
    [ObservableProperty] private Color _baseColor = Colors.White;
    [ObservableProperty] private string _status = "Pick a color, then press a key to paint it. Press it again to reset.";

    public PerKeyColorViewModel(
        DeviceSession session,
        Color? baseColor = null,
        Color? paintColor = null,
        IReadOnlyDictionary<byte, Color>? initialPainted = null,
        Action<IReadOnlyDictionary<byte, Color>, Color, Color>? save = null)
    {
        _session = session;
        if (baseColor is { } b) _baseColor = b;
        if (paintColor is { } p) _selectedColor = p;
        if (initialPainted is not null)
            foreach (var (zone, color) in initialPainted) _painted[zone] = color;
        _save = save;
    }

    /// <summary>Persist the current state (painted keys + both colors). No-op until wired by the host.</summary>
    private void Persist() => _save?.Invoke(_painted, BaseColor, SelectedColor);

    // Remember the chosen colors across restarts, too (not just painted keys).
    partial void OnBaseColorChanged(Color value) => Persist();
    partial void OnSelectedColorChanged(Color value) => Persist();

    /// <summary>
    /// Keys that can't be pressed into the window (OS-swallowed) or have no key code
    /// (G-keys, media, logo) — click to paint. Zones are hardware-mapped on the G915
    /// (decimal in comments), confirmed by probing; most are NOT usage−3.
    /// </summary>
    // Grouped into rows that roughly follow the physical keyboard layout.
    public IReadOnlyList<IReadOnlyList<SpecialKey>> SpecialKeyRows { get; } =
    [
        // Logo + brightness + system keys.
        [new("Logo", 0xd2), new("☀︎", 0x99), new("PrtSc", 0x43), new("ScrLk", 0x44), new("Pause", 0x45)], // 210, 153, 67–69
        // G-keys.
        [new("G1", 0xb4), new("G2", 0xb5), new("G3", 0xb6), new("G4", 0xb7), new("G5", 0xb8)], // 180–184
        // Left modifiers.
        [new("L Shift", 0x69), new("L Ctrl", 0x68), new("L Alt", 0x6a), new("L Win", 0x6b)],  // 105,104,106,107
        // Right modifiers + menu.
        [new("R Shift", 0x6d), new("R Ctrl", 0x6c), new("R Win", 0x6f), new("R Alt", 0x6e), new("Menu", 0x62)], // 109,108,111,110,98
        // Media keys.
        [new("Prev", 0x9e), new("Play/Pause", 0x9b), new("Next", 0x9d), new("Mute", 0x9c)],   // 158,155,157,156
    ];

    [RelayCommand]
    private async Task PaintSpecial(SpecialKey? key)
    {
        if (key is not null) await PaintZoneAsync(key.Zone, key.Label);
    }

    public async Task<bool> SetZoneAsync(byte zone, Color color)
        => await _session.SetZoneAsync(zone, color.R, color.G, color.B);

    /// <summary>
    /// Prepare the keyboard for editing when the window opens: enter host mode so
    /// 0x8081 writes take effect, then re-assert the saved painted keys on top of
    /// whatever is already showing. Deliberately does NOT flood the board to the base
    /// color — that would wipe the colors currently on the keyboard.
    /// </summary>
    public async Task InitAsync()
    {
        await _session.EnsureHostModeAsync();
        foreach (var (zone, color) in _painted)
            await SetZoneAsync(zone, color);
    }

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

    /// <summary>The LED zone for a physical key. Modifiers aren't at usage−3 on the G915, so they're explicit (probed: a contiguous 104–111 block).</summary>
    private static byte? PhysicalToZone(PhysicalKey k) => k switch
    {
        PhysicalKey.ControlLeft => 0x68,  // 104
        PhysicalKey.ShiftLeft => 0x69,    // 105
        PhysicalKey.AltLeft => 0x6a,      // 106
        PhysicalKey.MetaLeft => 0x6b,     // 107
        PhysicalKey.ControlRight => 0x6c, // 108
        PhysicalKey.ShiftRight => 0x6d,   // 109
        PhysicalKey.AltRight => 0x6e,     // 110
        PhysicalKey.MetaRight => 0x6f,    // 111
        _ => PhysicalToUsage(k) is { } u && u >= 3 ? (byte)(u - 3) : null,
    };

    /// <summary>Paint a zone with the selected color, or reset it to base if already that color.</summary>
    private async Task PaintZoneAsync(byte zone, string label)
    {
        if (_painted.TryGetValue(zone, out var cur) && cur == SelectedColor)
        {
            _painted.Remove(zone);
            await SetZoneAsync(zone, BaseColor);
            Status = $"{label} reset.";
        }
        else
        {
            _painted[zone] = SelectedColor;
            await SetZoneAsync(zone, SelectedColor);
            Status = $"{label} → #{SelectedColor.R:x2}{SelectedColor.G:x2}{SelectedColor.B:x2}";
        }
        Persist();
    }

    /// <summary>Reset every painted key back to the base color with a single range fill.</summary>
    public async Task ResetAllAsync()
    {
        // One range write (zones 0x00–0xfe) + commit floods the whole board to the
        // base color at once, instead of walking key by key.
        _painted.Clear();
        await _session.ApplyPerKeyColorAsync(BaseColor.R, BaseColor.G, BaseColor.B);
        Status = "All keys reset to base.";
        Persist();
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
