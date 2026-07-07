namespace OpenLogi.Core.DeviceInfo;

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
