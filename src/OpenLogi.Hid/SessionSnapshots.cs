namespace OpenLogi.Hid;

/// <summary>Current sensor DPI plus the values the device supports.</summary>
public sealed record DpiSnapshot(ushort Current, IReadOnlyList<ushort> Supported);

/// <summary>Current SmartShift wheel state.</summary>
public sealed record SmartShiftSnapshot(bool Ratchet, byte AutoDisengage, byte TunableTorque, bool TorqueSupported);

/// <summary>One host slot's detail.</summary>
public sealed record HostDetail(int Index, bool IsCurrent, bool Paired, string BusType, string? Name);

/// <summary>A profile's stored lighting descriptor (first LED zone): effect byte, colour, and Breathing/Cycle period+brightness.</summary>
public readonly record struct ProfileLighting(byte Effect, byte R, byte G, byte B, ushort PeriodMs, byte Brightness);

/// <summary>Host (EasySwitch) state: count, current slot (0-based), per-slot detail, and whether slots can be cleared.</summary>
public sealed record HostSnapshot(byte HostCount, byte CurrentHost, IReadOnlyList<HostDetail> Hosts, bool SupportsDelete);
