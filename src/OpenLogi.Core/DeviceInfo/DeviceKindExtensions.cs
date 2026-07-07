namespace OpenLogi.Core.DeviceInfo;

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
