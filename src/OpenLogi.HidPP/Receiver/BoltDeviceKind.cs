namespace OpenLogi.HidPP.Receiver;

/// <summary>The kind of a device paired to a Bolt receiver. Ported from Rust <c>receiver::bolt::DeviceKind</c>.</summary>
public enum BoltDeviceKind : byte
{
    Unknown = 0x00, Keyboard = 0x01, Mouse = 0x02, Numpad = 0x03, Presenter = 0x04,
    Remote = 0x07, Trackball = 0x08, Touchpad = 0x09, Tablet = 0x0a, Gamepad = 0x0b,
    Joystick = 0x0c, Headset = 0x0d,
}
