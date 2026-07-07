using OpenLogi.Core.DeviceInfo;

namespace OpenLogi.Core.Config;

/// <summary>
/// Last-known identity of a device, captured while online so the UI can render
/// the right panels before any live probe (or while the device sleeps). Every
/// field is a static property of the <em>model</em>, so it never goes stale.
/// Ported from Rust <c>config::DeviceIdentity</c>.
/// </summary>
public sealed record DeviceIdentity
{
    public required string DisplayName { get; init; }
    public DeviceModelInfo? ModelInfo { get; init; }
    public string? Codename { get; init; }
    public required DeviceKind Kind { get; init; }
    public required Capabilities Capabilities { get; init; }
}
