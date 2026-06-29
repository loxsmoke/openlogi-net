namespace OpenLogi.HidPP;

/// <summary>An unsigned 4-bit value (nibble) encoded in a byte. Ported from Rust <c>nibble::U4</c>.</summary>
public readonly struct U4 : IEquatable<U4>
{
    private readonly byte _value;

    private U4(byte value) => _value = (byte)(value & 0x0f);

    /// <summary>Construct from the 4 low/rightmost bits of a byte.</summary>
    public static U4 FromLo(byte raw) => new((byte)(raw & 0x0f));

    /// <summary>Construct from the 4 high/leftmost bits of a byte.</summary>
    public static U4 FromHi(byte raw) => new((byte)(raw >> 4));

    /// <summary>The nibble as the 4 low/rightmost bits of a byte.</summary>
    public byte ToLo() => _value;

    /// <summary>The nibble as the 4 high/leftmost bits of a byte.</summary>
    public byte ToHi() => (byte)(_value << 4);

    /// <summary>Combine two nibbles: <paramref name="a"/> into the high bits, <paramref name="b"/> into the low.</summary>
    public static byte Combine(U4 a, U4 b) => (byte)(a.ToHi() | b.ToLo());

    public bool Equals(U4 other) => _value == other._value;
    public override bool Equals(object? obj) => obj is U4 u && Equals(u);
    public override int GetHashCode() => _value;
    public static bool operator ==(U4 a, U4 b) => a.Equals(b);
    public static bool operator !=(U4 a, U4 b) => !a.Equals(b);
}
