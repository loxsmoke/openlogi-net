using System.Collections.Generic;
using System.Linq;

namespace OpenLogi.App.ViewModels;

/// <summary>How a key behaves in the picker.</summary>
public enum KeyKind { Normal, Modifier, Disabled }

/// <summary>One key on the visual keyboard.</summary>
public sealed record KeyCap(string Label, byte Usage, double Width = 1.0, KeyKind Kind = KeyKind.Normal, byte ModifierBit = 0);

/// <summary>An ANSI keyboard layout for the G-key remap picker, plus binding-label helpers.</summary>
public static class KeyboardLayout
{
    private const byte Ctrl = 0x01, Shift = 0x02, Alt = 0x04;

    /// <summary>Rows of keys, top to bottom.</summary>
    public static IReadOnlyList<IReadOnlyList<KeyCap>> Rows { get; } = Build();

    private static KeyCap K(string label, byte usage, double w = 1.0) => new(label, usage, w);
    private static KeyCap Mod(string label, byte bit, double w) => new(label, 0, w, KeyKind.Modifier, bit);
    private static KeyCap Dis(string label, double w = 1.0) => new(label, 0, w, KeyKind.Disabled);

    private static List<IReadOnlyList<KeyCap>> Build() =>
    [
        [K("Esc", 0x29), K("F1", 0x3A), K("F2", 0x3B), K("F3", 0x3C), K("F4", 0x3D), K("F5", 0x3E),
         K("F6", 0x3F), K("F7", 0x40), K("F8", 0x41), K("F9", 0x42), K("F10", 0x43), K("F11", 0x44), K("F12", 0x45)],
        [K("`", 0x35), K("1", 0x1E), K("2", 0x1F), K("3", 0x20), K("4", 0x21), K("5", 0x22), K("6", 0x23),
         K("7", 0x24), K("8", 0x25), K("9", 0x26), K("0", 0x27), K("-", 0x2D), K("=", 0x2E), K("Back", 0x2A, 1.6)],
        [K("Tab", 0x2B, 1.4), K("Q", 0x14), K("W", 0x1A), K("E", 0x08), K("R", 0x15), K("T", 0x17), K("Y", 0x1C),
         K("U", 0x18), K("I", 0x0C), K("O", 0x12), K("P", 0x13), K("[", 0x2F), K("]", 0x30), K("\\", 0x31, 1.2)],
        [K("Caps", 0x39, 1.6), K("A", 0x04), K("S", 0x16), K("D", 0x07), K("F", 0x09), K("G", 0x0A), K("H", 0x0B),
         K("J", 0x0D), K("K", 0x0E), K("L", 0x0F), K(";", 0x33), K("'", 0x34), K("Enter", 0x28, 1.8)],
        [Mod("Shift", Shift, 2.0), K("Z", 0x1D), K("X", 0x1B), K("C", 0x06), K("V", 0x19), K("B", 0x05), K("N", 0x11),
         K("M", 0x10), K(",", 0x36), K(".", 0x37), K("/", 0x38), Mod("Shift", Shift, 2.4)],
        [Mod("Ctrl", Ctrl, 1.4), Dis("Win", 1.2), Mod("Alt", Alt, 1.2), K("Space", 0x2C, 5.6), Mod("Alt", Alt, 1.2),
         Dis("Menu", 1.2), Mod("Ctrl", Ctrl, 1.4)],
        [K("Ins", 0x49), K("Home", 0x4A), K("PgUp", 0x4B), K("Del", 0x4C), K("End", 0x4D), K("PgDn", 0x4E),
         K("Left", 0x50), K("Up", 0x52), K("Down", 0x51), K("Right", 0x4F), K("PrtSc", 0x46)],
    ];

    private static readonly Dictionary<byte, string> Names =
        Rows.SelectMany(r => r).Where(k => k.Kind == KeyKind.Normal)
            .GroupBy(k => k.Usage).ToDictionary(g => g.Key, g => g.First().Label);

    /// <summary>Human label for a binding, e.g. "Ctrl + C" or "F1".</summary>
    public static string DescribeBinding(byte usage, byte modifier)
    {
        if (usage == 0 && modifier == 0) return "—";
        var parts = new List<string>();
        if ((modifier & Ctrl) != 0) parts.Add("Ctrl");
        if ((modifier & Shift) != 0) parts.Add("Shift");
        if ((modifier & Alt) != 0) parts.Add("Alt");
        parts.Add(Names.TryGetValue(usage, out var n) ? n : $"0x{usage:x2}");
        return string.Join(" + ", parts);
    }
}
