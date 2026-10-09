using System.Collections.Frozen;
using System.Globalization;
using OpenLogi.Core.Localization;

namespace OpenLogi.Core.Actions;

/// <summary>
/// Modifier keys of a <see cref="KeyCombo"/>. The bit values equal Avalonia's
/// <c>KeyModifiers</c> (Alt, Control, Shift, Meta) so the UI can cast between the
/// two without a mapping; Core has its own type because it does not reference Avalonia.
/// </summary>
[Flags]
public enum ShortcutModifiers : byte
{
    None = 0,
    Alt = 1 << 0,
    Ctrl = 1 << 1,
    Shift = 1 << 2,
    Win = 1 << 3,
}

/// <summary>
/// A modifier + key chord for <see cref="ActionKind.CustomShortcut"/>. <see cref="KeyCode"/>
/// is a Windows virtual-key code (what <c>SendInput</c> takes); <see cref="Empty"/>
/// (key 0) is the cleared state and injects nothing. The text form, used both in the
/// config file and in the picker, is <c>Ctrl+Alt+Shift+Win+Key</c> with friendly key
/// names from <see cref="KeyNames"/>: <c>"Ctrl+Shift+P"</c>, <c>"Win+Left"</c>, <c>""</c>.
/// </summary>
public sealed record KeyCombo(ShortcutModifiers Modifiers, ushort KeyCode)
{
    public static readonly KeyCombo Empty = new(ShortcutModifiers.None, 0);

    /// <summary>No key recorded — nothing is injected.</summary>
    public bool IsEmpty => KeyCode == 0;

    /// <summary>Picker text: the combo, or the localized "None" when empty.</summary>
    public string Label() => IsEmpty ? Loc.Current["Shortcut_None"] : ToString();

    /// <summary>The canonical text form, also the on-disk form. Empty combo → <c>""</c>.</summary>
    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ShortcutModifiers.Ctrl)) parts.Add(CtrlName);
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add(AltName);
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add(ShiftName);
        if (Modifiers.HasFlag(ShortcutModifiers.Win)) parts.Add(WinName);
        parts.Add(KeyName(KeyCode));
        return string.Join(Separator, parts);
    }

    /// <summary>
    /// Parse the text form. Case-insensitive; every token but the last must be a
    /// modifier, the last a key name (or <c>VK0x..</c>); whitespace/empty → <see cref="Empty"/>.
    /// </summary>
    /// <exception cref="FormatException">An unknown modifier or key name, or no key.</exception>
    public static KeyCombo Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;
        var tokens = text.Split(Separator, StringSplitOptions.TrimEntries);
        var modifiers = ShortcutModifiers.None;
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (!ModifierByName.TryGetValue(tokens[i], out var m))
                throw new FormatException($"'{tokens[i]}' in '{text}'");
            modifiers |= m;
        }
        var key = tokens[^1];
        if (ModifierByName.ContainsKey(key) || key.Length == 0)
            throw new FormatException($"'{text}': no key");
        if (!TryKeyCode(key, out var vk))
            throw new FormatException($"'{key}' in '{text}'");
        return new KeyCombo(modifiers, vk);
    }

    public static bool TryParse(string text, out KeyCombo combo)
    {
        try { combo = Parse(text); return true; }
        catch (FormatException) { combo = Empty; return false; }
    }

    /// <summary>Friendly name of a virtual key, or <c>VK0x..</c> for one outside the table.</summary>
    public static string KeyName(ushort vk) =>
        KeyNames.TryGetValue(vk, out var name) ? name : $"{VkPrefix}{vk:X2}";

    /// <summary>The name of one modifier as written in the chord text ("Ctrl", "Alt", "Shift", "Win").</summary>
    public static string ModifierName(ShortcutModifiers modifier) => modifier switch
    {
        ShortcutModifiers.Ctrl => CtrlName,
        ShortcutModifiers.Alt => AltName,
        ShortcutModifiers.Shift => ShiftName,
        ShortcutModifiers.Win => WinName,
        _ => throw new ArgumentOutOfRangeException(nameof(modifier), modifier, null),
    };

    private static bool TryKeyCode(string name, out ushort vk)
    {
        if (KeyByName.TryGetValue(name, out vk)) return true;
        if (name.StartsWith(VkPrefix, StringComparison.OrdinalIgnoreCase)
            && ushort.TryParse(name.AsSpan(VkPrefix.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vk)
            && vk != 0)
            return true;
        vk = 0;
        return false;
    }

    // ── Names ─────────────────────────────────────────────────────────────────
    // These are key legends, not UI prose: they follow the keyboard, not the UI
    // language, and they are the config-file vocabulary.

    private const string Separator = "+";
    private const string VkPrefix = "VK0x";
    private const string CtrlName = "Ctrl", AltName = "Alt", ShiftName = "Shift", WinName = "Win";

    private static readonly FrozenDictionary<string, ShortcutModifiers> ModifierByName = new Dictionary<string, ShortcutModifiers>
    {
        [CtrlName] = ShortcutModifiers.Ctrl, ["Control"] = ShortcutModifiers.Ctrl,
        [AltName] = ShortcutModifiers.Alt,
        [ShiftName] = ShortcutModifiers.Shift,
        [WinName] = ShortcutModifiers.Win, ["Meta"] = ShortcutModifiers.Win, ["Windows"] = ShortcutModifiers.Win,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Canonical name per Windows virtual-key code.</summary>
    public static readonly FrozenDictionary<ushort, string> KeyNames = BuildKeyNames();

    private static readonly FrozenDictionary<string, ushort> KeyByName = BuildKeyByName();

    private static FrozenDictionary<ushort, string> BuildKeyNames()
    {
        var names = new Dictionary<ushort, string>();
        for (var c = 'A'; c <= 'Z'; c++) names[(ushort)c] = c.ToString();
        for (var d = '0'; d <= '9'; d++) names[(ushort)d] = d.ToString();
        for (var f = 1; f <= 24; f++) names[(ushort)(0x6F + f)] = $"F{f}";
        for (var n = 0; n <= 9; n++) names[(ushort)(0x60 + n)] = $"Num{n}";

        names[0x0D] = "Enter"; names[0x1B] = "Esc"; names[0x09] = "Tab"; names[0x20] = "Space"; names[0x08] = "Backspace";
        names[0x2D] = "Insert"; names[0x2E] = "Delete"; names[0x24] = "Home"; names[0x23] = "End";
        names[0x21] = "PageUp"; names[0x22] = "PageDown";
        names[0x26] = "Up"; names[0x28] = "Down"; names[0x25] = "Left"; names[0x27] = "Right";
        names[0x6A] = "NumMultiply"; names[0x6B] = "NumPlus"; names[0x6D] = "NumMinus"; names[0x6E] = "NumDecimal"; names[0x6F] = "NumDivide";
        names[0xBC] = "Comma"; names[0xBE] = "Period"; names[0xBD] = "Minus"; names[0xBB] = "Plus";
        names[0xBA] = "Semicolon"; names[0xDE] = "Quote"; names[0xBF] = "Slash"; names[0xDC] = "Backslash";
        names[0xDB] = "LeftBracket"; names[0xDD] = "RightBracket"; names[0xC0] = "Backquote";
        names[0x2C] = "PrintScreen"; names[0x91] = "ScrollLock"; names[0x13] = "Pause"; names[0x14] = "CapsLock";
        names[0x90] = "NumLock"; names[0x5D] = "Apps";
        names[0xA6] = "BrowserBack"; names[0xA7] = "BrowserForward";
        names[0xAD] = "VolumeMute"; names[0xAE] = "VolumeDown"; names[0xAF] = "VolumeUp";
        names[0xB0] = "MediaNext"; names[0xB1] = "MediaPrev"; names[0xB3] = "MediaPlayPause";
        return names.ToFrozenDictionary();
    }

    private static FrozenDictionary<string, ushort> BuildKeyByName()
    {
        var byName = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        foreach (var (vk, name) in KeyNames) byName[name] = vk;
        // Aliases accepted on parse only; ToString always writes the canonical name.
        byName["Escape"] = 0x1B; byName["Return"] = 0x0D; byName["Del"] = 0x2E; byName["Ins"] = 0x2D;
        byName["PgUp"] = 0x21; byName["PgDn"] = 0x22; byName["PgDown"] = 0x22;
        return byName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
