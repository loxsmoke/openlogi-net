using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenLogi.Core.DeviceInfo;
using OpenLogi.Hid;

namespace OpenLogi.App.ViewModels;

/// <summary>Display wrapper for a paired device shown in the carousel / list.</summary>
public sealed partial class DeviceViewModel(string receiverName, PairedDevice device, DeviceRoute? route) : ObservableObject
{
    public string ReceiverName { get; } = receiverName;
    public PairedDevice Device { get; } = device;

    /// <summary>How to reach this device for live DPI/SmartShift control; <c>null</c> if unroutable.</summary>
    public DeviceRoute? Route { get; } = route;

    public string Name => Device.Codename ?? Device.Kind.ToString();
    public string Kind => Device.Kind.ToString();
    public string Status => Device.Online ? "Online" : "Asleep";

    /// <summary>
    /// True when the device is paired but not currently on its wireless link — a
    /// receiver reports a sleeping (or powered-off / out-of-range) device this way.
    /// Directly-attached USB/Bluetooth devices are always enumerated online, so this
    /// only ever flags a wireless device that's dropped off, i.e. asleep.
    /// </summary>
    public bool IsAsleep => !Device.Online;

    /// <summary>Dims the render on the gallery tile while the device is asleep.</summary>
    public double TileImageOpacity => IsAsleep ? 0.4 : 1.0;

    /// <summary>How the device is connected (dongle vs Bluetooth vs cable) — the tile's corner icon.</summary>
    public ConnectionKind Connection { get; } = ConnectionKinds.For(route, device);

    public bool HasConnectionIcon => Connection != ConnectionKind.Unknown;

    /// <summary>Tooltip for the connection icon.</summary>
    public string ConnectionLabel => Connection switch
    {
        ConnectionKind.Bluetooth => "Bluetooth",
        ConnectionKind.LightspeedDongle => "LIGHTSPEED dongle",
        ConnectionKind.UnifyingDongle => "Unifying dongle",
        ConnectionKind.BoltDongle => "Bolt dongle",
        ConnectionKind.UsbCable => "USB",
        _ => "",
    };

    // Stroke path data (24×24 box) rendered by the tile template. The three dongle
    // kinds share the USB-stick body but carry their family's mark on top:
    // LIGHTSPEED radio waves, Unifying's sun-burst, Bolt's lightning bolt.
    private const string DongleBody = "M 10,9 L 10,12 M 14,12 L 14,9 M 8,12 L 8,21 L 16,21 L 16,12 Z ";

    public string ConnectionIcon => Connection switch
    {
        ConnectionKind.Bluetooth => "M 6.5,7.5 L 17,16.5 L 12,21 L 12,3 L 17,7.5 L 6.5,16.5",
        ConnectionKind.LightspeedDongle => DongleBody + "M 8.5,6.5 A 5,5 0 0 1 15.5,6.5 M 6,4 A 8.5,8.5 0 0 1 18,4",
        ConnectionKind.UnifyingDongle => DongleBody + "M 12,2.5 L 12,9.5 M 8.5,4 L 15.5,8 M 15.5,4 L 8.5,8",
        ConnectionKind.BoltDongle => DongleBody + "M 13.5,2.5 L 10.5,7 L 13.5,7 L 10.5,11",
        ConnectionKind.UsbCable => "M 9.5,3 L 9.5,7 M 14.5,3 L 14.5,7 M 7,7 L 17,7 L 17,13 A 5,5 0 0 1 7,13 Z M 12,18 L 12,22",
        _ => "",
    };

    /// <summary>Selects the badge's Bluetooth-blue style class (other kinds use the theme's foreground badge brush).</summary>
    public bool IsBluetoothConnection => Connection == ConnectionKind.Bluetooth;

    /// <summary>Live battery from the open session (0x1004/0x1001), overriding the scan-time snapshot when present.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Battery), nameof(HasBattery), nameof(BatteryPercent), nameof(BatteryCharging))]
    private BatteryInfo? _liveBattery;

    private BatteryInfo? CurrentBattery => LiveBattery ?? Device.Battery;

    public string Battery => CurrentBattery is { } b ? $"{b.Percentage}% · {b.Status}" : "—";

    /// <summary>Whether a battery reading is available (drives the battery icon's visibility).</summary>
    public bool HasBattery => CurrentBattery is not null;

    /// <summary>Charge percent for the battery icon (0 when unknown).</summary>
    public int BatteryPercent => CurrentBattery?.Percentage ?? 0;

    /// <summary>Whether the device is currently charging (icon shows the charging colour).</summary>
    public bool BatteryCharging => CurrentBattery is { Status: BatteryStatus.Charging or BatteryStatus.ChargingSlow };

    /// <summary>HID++ config key (model id), used to resolve the device render.</summary>
    public string? ConfigKey => Device.ModelInfo?.ConfigKey();

    /// <summary>Colour/SKU variant byte for the asset registry.</summary>
    public byte Ext => Device.ModelInfo?.ExtendedModelId ?? 0;

    /// <summary>The device render, set once the asset is resolved/downloaded.</summary>
    [ObservableProperty]
    private Bitmap? _image;

    public bool HasPointer => Device.Capabilities?.Pointer ?? false;
    public bool HasButtons => Device.Capabilities?.Buttons ?? false;
    public bool HasLighting => Device.Capabilities?.Lighting ?? false;
    public bool HasGKeys => Device.Capabilities?.GKeys ?? false;

    public string Capabilities
    {
        get
        {
            var caps = new List<string>();
            if (HasButtons) caps.Add("Buttons");
            if (HasPointer) caps.Add("DPI");
            if (HasLighting) caps.Add("Lighting");
            return caps.Count > 0 ? string.Join(", ", caps) : "—";
        }
    }
}
