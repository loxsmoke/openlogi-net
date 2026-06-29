using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenLogi.Core;
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
    public string Status => Device.Online ? "Online" : "Offline";

    public string Battery => Device.Battery is { } b ? $"{b.Percentage}% · {b.Status}" : "—";

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
