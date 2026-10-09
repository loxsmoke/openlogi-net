using CommunityToolkit.Mvvm.ComponentModel;
using OpenLogi.Core.Actions;
using OpenLogi.Core.Localization;

namespace OpenLogi.App.ViewModels;

/// <summary>
/// State of the chord recorder dialog: four modifier toggles and one key.
/// While no key is recorded (<see cref="Capturing"/>), the toggles mirror the
/// modifier keys physically held — down turns a toggle on, up turns it off — and
/// the first other key pressed is recorded together with the toggles as shown
/// at that moment. From then on key presses change nothing: the chord is frozen
/// until <see cref="Clear"/>. The toggles can always be clicked, so the modifiers
/// of a recorded chord can still be adjusted (and a Win chord can be assembled
/// by hand if the Win key itself could not be captured).
/// </summary>
public sealed partial class ShortcutRecorderViewModel : ObservableObject
{
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Preview), nameof(Result))] private bool _ctrl;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Preview), nameof(Result))] private bool _alt;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Preview), nameof(Result))] private bool _shift;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Preview), nameof(Result))] private bool _win;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Preview), nameof(Result), nameof(HasKey), nameof(Capturing), nameof(Hint))]
    private ushort _keyCode;

    public ShortcutRecorderViewModel(KeyCombo current)
    {
        _ctrl = current.Modifiers.HasFlag(ShortcutModifiers.Ctrl);
        _alt = current.Modifiers.HasFlag(ShortcutModifiers.Alt);
        _shift = current.Modifiers.HasFlag(ShortcutModifiers.Shift);
        _win = current.Modifiers.HasFlag(ShortcutModifiers.Win);
        _keyCode = current.KeyCode;
        Loc.Current.WeakSubscribe(this, static (self, e) =>
        {
            if (e.PropertyName is nameof(Loc.Culture) or "Item[]")
            {
                self.OnPropertyChanged(nameof(Preview));
                self.OnPropertyChanged(nameof(Hint));
            }
        });
    }

    /// <summary>The instructions above the preview: how to record while waiting, how to start over once recorded.</summary>
    public string Hint => Loc.Current[Capturing ? "ShortcutRecorder_HintCapturing" : "ShortcutRecorder_HintRecorded"];

    /// <summary>Whether a (non-modifier) key has been recorded.</summary>
    public bool HasKey => KeyCode != 0;

    /// <summary>Whether the dialog is waiting for a key — the state in which every key is intercepted.</summary>
    public bool Capturing => !HasKey;

    public ShortcutModifiers Modifiers =>
        (Ctrl ? ShortcutModifiers.Ctrl : 0) | (Alt ? ShortcutModifiers.Alt : 0)
        | (Shift ? ShortcutModifiers.Shift : 0) | (Win ? ShortcutModifiers.Win : 0);

    /// <summary>
    /// What OK returns: the chord as shown, or <see cref="KeyCombo.Empty"/> while no key
    /// is recorded (modifiers alone are not a shortcut) — the "no shortcut" result after Clear.
    /// </summary>
    public KeyCombo Result => HasKey ? new(Modifiers, KeyCode) : KeyCombo.Empty;

    /// <summary>Exactly the text that will be stored and shown in the picker, or the "press a key" prompt.</summary>
    public string Preview => HasKey ? Result.ToString() : Loc.Current["ShortcutRecorder_PressKey"];

    // Toggle captions: key legends, the same words the chord text uses.
    public string CtrlName => KeyCombo.ModifierName(ShortcutModifiers.Ctrl);
    public string AltName => KeyCombo.ModifierName(ShortcutModifiers.Alt);
    public string ShiftName => KeyCombo.ModifierName(ShortcutModifiers.Shift);
    public string WinName => KeyCombo.ModifierName(ShortcutModifiers.Win);

    /// <summary>A key went down (<paramref name="vk"/> is its Windows virtual key). Ignored once a key is recorded.</summary>
    public void KeyDown(ushort vk)
    {
        if (!Capturing || vk == 0) return;
        if (ModifierOf(vk) is { } modifier) SetModifier(modifier, true);
        else KeyCode = vk;
    }

    /// <summary>A key went up. Only a modifier release matters, and only while capturing.</summary>
    public void KeyUp(ushort vk)
    {
        if (!Capturing) return;
        if (ModifierOf(vk) is { } modifier) SetModifier(modifier, false);
    }

    /// <summary>Back to waiting for a key: no modifiers, no key.</summary>
    public void Clear()
    {
        Ctrl = Alt = Shift = Win = false;
        KeyCode = 0;
    }

    private void SetModifier(ShortcutModifiers modifier, bool down)
    {
        switch (modifier)
        {
            case ShortcutModifiers.Shift: Shift = down; break;
            case ShortcutModifiers.Ctrl: Ctrl = down; break;
            case ShortcutModifiers.Alt: Alt = down; break;
            case ShortcutModifiers.Win: Win = down; break;
        }
    }

    /// <summary>The modifier a virtual key stands for (generic, left or right variants), else null.</summary>
    public static ShortcutModifiers? ModifierOf(ushort vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => ShortcutModifiers.Shift, // VK_SHIFT, VK_LSHIFT, VK_RSHIFT
        0x11 or 0xA2 or 0xA3 => ShortcutModifiers.Ctrl,  // VK_CONTROL, VK_LCONTROL, VK_RCONTROL
        0x12 or 0xA4 or 0xA5 => ShortcutModifiers.Alt,   // VK_MENU, VK_LMENU, VK_RMENU
        0x5B or 0x5C => ShortcutModifiers.Win,           // VK_LWIN, VK_RWIN
        _ => null,
    };
}
