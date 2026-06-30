namespace OpenLogi.Core;

/// <summary>
/// What a paired peripheral is. Several upstream "device type" vocabularies feed
/// this one enum and do not agree on numbers, so conversion always happens at
/// the boundary (never by reinterpreting one source's raw byte with another's
/// table). Ported from Rust <c>device::DeviceKind</c>.
/// </summary>
public enum DeviceKind
{
    Mouse,
    Keyboard,
    Numpad,
    Presenter,
    Remote,
    Trackball,
    Touchpad,
    Tablet,
    Gamepad,
    Joystick,
    Headset,
    Unknown,
}

public static class DeviceKindExtensions
{
    /// <summary>
    /// Parse the OpenLogi asset registry's free-form, case-inconsistent
    /// <c>type</c> string. Unmodelled values map to <see cref="DeviceKind.Unknown"/>
    /// ("no asset opinion").
    /// </summary>
    public static DeviceKind FromRegistryType(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "mouse" => DeviceKind.Mouse,
        "keyboard" => DeviceKind.Keyboard,
        "numpad" => DeviceKind.Numpad,
        "presenter" => DeviceKind.Presenter,
        "remote" or "remotecontrol" => DeviceKind.Remote,
        "trackball" => DeviceKind.Trackball,
        "touchpad" or "trackpad" => DeviceKind.Touchpad,
        "tablet" => DeviceKind.Tablet,
        "gamepad" => DeviceKind.Gamepad,
        "joystick" => DeviceKind.Joystick,
        "headset" => DeviceKind.Headset,
        _ => DeviceKind.Unknown,
    };
}

/// <summary>
/// What a device can be <em>configured</em> to do, derived from the HID++
/// feature table it reports. The source of truth for which configuration panels
/// the UI offers — gating on capability (what firmware announced), not
/// <see cref="DeviceKind"/> (an identity guess). Ported from Rust
/// <c>device::Capabilities</c>.
/// </summary>
public sealed record Capabilities
{
    /// <summary>Reprogrammable buttons — HID++ 0x1b00–0x1b04 (ReprogControls).</summary>
    public bool Buttons { get; init; }
    /// <summary>Adjustable pointer resolution — HID++ 0x2201 / 0x2202.</summary>
    public bool Pointer { get; init; }
    /// <summary>Solid-colour RGB the lighting panel can drive — HID++ 0x8070 / 0x8080.</summary>
    public bool Lighting { get; init; }
    /// <summary>Native vertical wheel inversion — HID++ 0x2121 with has_invert.</summary>
    public bool ScrollInversion { get; init; }
    /// <summary>Programmable G-keys — HID++ 0x8010 (GKeys).</summary>
    public bool GKeys { get; init; }

    /// <summary>Derive capabilities from the set of HID++ feature IDs a device reports.</summary>
    public static Capabilities FromFeatureIds(IReadOnlyCollection<ushort> ids)
    {
        ushort[] buttons = [0x1b00, 0x1b01, 0x1b02, 0x1b03, 0x1b04];
        ushort[] pointer = [0x2201, 0x2202];
        // 0x8070/0x8080 (older) + 0x8071/0x8081 (newer G-series) + 0x8040 backlight brightness.
        ushort[] lighting = [0x8080, 0x8070, 0x8081, 0x8071, 0x8040];
        bool Has(ushort[] family) => ids.Any(family.Contains);
        return new Capabilities
        {
            Buttons = Has(buttons),
            Pointer = Has(pointer),
            Lighting = Has(lighting),
            ScrollInversion = ids.Contains((ushort)0x2121),
            GKeys = ids.Contains((ushort)0x8010),
        };
    }

    /// <summary>
    /// Best-effort capabilities for a device we could not probe, guessed from
    /// its <see cref="DeviceKind"/>. Keeps a sleeping mouse's panels visible.
    /// </summary>
    public static Capabilities PresumedFromKind(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse or DeviceKind.Trackball =>
            new Capabilities { Buttons = true, Pointer = true },
        DeviceKind.Keyboard => new Capabilities { Lighting = true },
        _ => new Capabilities(),
    };
}

/// <summary>Coarse battery bucket reported by the device firmware.</summary>
public enum BatteryLevel { Critical, Low, Good, Full, Unknown }

/// <summary>Charging state.</summary>
public enum BatteryStatus { Discharging, Charging, ChargingSlow, Full, Error, Unknown }

public sealed record BatteryInfo
{
    public required byte Percentage { get; init; }
    public required BatteryLevel Level { get; init; }
    public required BatteryStatus Status { get; init; }
}

public sealed record ReceiverInfo
{
    public required string Name { get; init; }
    public required ushort VendorId { get; init; }
    public required ushort ProductId { get; init; }
    public string? UniqueId { get; init; }
}

/// <summary>
/// Mirror of HID++ DeviceInformation's transport bitfield — one flag per
/// protocol the firmware exposes (independent, not a state machine).
/// </summary>
public sealed record DeviceTransports
{
    public bool Usb { get; init; }
    public bool Equad { get; init; }
    public bool Btle { get; init; }
    public bool Bluetooth { get; init; }
}

/// <summary>
/// HID++ DeviceInformation (feature 0x0003) snapshot used to identify a device
/// against external registries. Ported from Rust <c>device::DeviceModelInfo</c>.
/// </summary>
public sealed record DeviceModelInfo
{
    public required byte EntityCount { get; init; }
    public string? SerialNumber { get; init; }
    /// <summary>4-byte unit id.</summary>
    public required byte[] UnitId { get; init; }
    public required DeviceTransports Transports { get; init; }
    /// <summary>Per-transport PID array (3 entries).</summary>
    public required ushort[] ModelIds { get; init; }
    public required byte ExtendedModelId { get; init; }

    /// <summary>
    /// Stable identifier used to key per-device config and look up assets:
    /// <c>{ExtendedModelId:x}{ModelIds[0]:x4}</c> (e.g. <c>"2b042"</c>).
    /// </summary>
    public string ConfigKey() => $"{ExtendedModelId:x}{ModelIds[0]:x4}";

    public bool Equals(DeviceModelInfo? other) =>
        other is not null
        && EntityCount == other.EntityCount
        && SerialNumber == other.SerialNumber
        && UnitId.AsSpan().SequenceEqual(other.UnitId)
        && Transports == other.Transports
        && ModelIds.AsSpan().SequenceEqual(other.ModelIds)
        && ExtendedModelId == other.ExtendedModelId;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(EntityCount);
        hash.Add(SerialNumber);
        foreach (var b in UnitId) hash.Add(b);
        hash.Add(Transports);
        foreach (var m in ModelIds) hash.Add(m);
        hash.Add(ExtendedModelId);
        return hash.ToHashCode();
    }
}

public sealed record PairedDevice
{
    /// <summary>Receiver-assigned slot (1..=6 for Bolt).</summary>
    public required byte Slot { get; init; }
    public string? Codename { get; init; }
    /// <summary>Wireless product ID. <c>null</c> for offline / unreachable devices.</summary>
    public ushort? Wpid { get; init; }
    public required DeviceKind Kind { get; init; }
    public required bool Online { get; init; }
    public BatteryInfo? Battery { get; init; }
    public DeviceModelInfo? ModelInfo { get; init; }
    public Capabilities? Capabilities { get; init; }
}

/// <summary>One receiver and its paired devices — the unit of an inventory snapshot.</summary>
public sealed record DeviceInventory
{
    public required ReceiverInfo Receiver { get; init; }
    public required IReadOnlyList<PairedDevice> Paired { get; init; }
}
