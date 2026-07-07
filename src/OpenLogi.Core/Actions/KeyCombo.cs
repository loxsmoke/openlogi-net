namespace OpenLogi.Core.Actions;

/// <summary>
/// A modifier + virtual-key keystroke. <see cref="Modifiers"/> is a bitmask of
/// <see cref="ModCmd"/> etc.; <see cref="KeyCode"/> is the macOS virtual key
/// (kVK_*). <see cref="Display"/> is a pre-rendered label for the UI. Ported
/// from Rust <c>binding::KeyCombo</c>.
/// </summary>
public sealed record KeyCombo
{
    public const byte ModCmd = 1 << 0;
    public const byte ModShift = 1 << 1;
    public const byte ModCtrl = 1 << 2;
    public const byte ModOption = 1 << 3;

    public required byte Modifiers { get; init; }
    public required ushort KeyCode { get; init; }
    public string Display { get; init; } = "";

    /// <summary>
    /// Build the human-readable label from the modifier bitmask + key code,
    /// or return <see cref="Display"/> when set.
    /// </summary>
    public string RenderedLabel()
    {
        if (Display.Length != 0) return Display;
        var sb = new System.Text.StringBuilder();
        if ((Modifiers & ModCtrl) != 0) sb.Append('⌃');
        if ((Modifiers & ModOption) != 0) sb.Append('⌥');
        if ((Modifiers & ModShift) != 0) sb.Append('⇧');
        if ((Modifiers & ModCmd) != 0) sb.Append('⌘');
        sb.Append(KeyCode switch
        {
            0x00 => "A", 0x01 => "S", 0x02 => "D", 0x03 => "F", 0x06 => "Z", 0x07 => "X",
            0x08 => "C", 0x09 => "V", 0x0B => "B", 0x0C => "Q", 0x0D => "W", 0x0E => "E",
            0x0F => "R", 0x10 => "Y", 0x11 => "T", 0x20 => "U", 0x22 => "I", 0x1F => "O",
            0x23 => "P",
            _ => $"key 0x{KeyCode:X2}",
        });
        return sb.ToString();
    }
}
