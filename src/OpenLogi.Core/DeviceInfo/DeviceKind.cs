namespace OpenLogi.Core.DeviceInfo;

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
