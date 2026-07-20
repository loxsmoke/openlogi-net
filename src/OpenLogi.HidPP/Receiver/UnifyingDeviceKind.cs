namespace OpenLogi.HidPP.Receiver;

/// <summary>
/// The kind of a device paired to a Unifying receiver. Matches Bolt for 1–4;
/// from 5 it uses a shifted table. Ported from Rust <c>receiver::unifying::DeviceKind</c>.
/// </summary>
public enum UnifyingDeviceKind : byte
{
    Unknown = 0x00, Keyboard = 0x01, Mouse = 0x02, Numpad = 0x03, Presenter = 0x04,
    Remote = 0x05, Trackball = 0x06, Touchpad = 0x07,
}
