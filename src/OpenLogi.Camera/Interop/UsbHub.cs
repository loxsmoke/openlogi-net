using System.Runtime.InteropServices;

namespace OpenLogi.Camera.Interop;

/// <summary>
/// Reads the USB link a camera runs at from the hub it hangs off: the
/// Configuration Manager walks from the camera's interface path to its USB device
/// (port number) and that device's hub, and two hub IOCTLs answer the speed and
/// the SuperSpeed flags — the calls the SDK's USBView sample makes. Nothing is
/// sent to the camera. Every step can fail; a failure leaves the camera with no
/// link, never a wrong one. Ported from Rust <c>usb_windows.rs</c>.
/// </summary>
internal static partial class UsbHub
{
    private const uint CrSuccess = 0;
    private const uint CmDrpAddress = 0x1D;
    private const uint CmLocateDevNodeNormal = 0;
    private const uint CmGetDeviceInterfaceListPresent = 0;
    private const uint IoctlUsbGetNodeConnectionInformationEx = 0x220448;
    private const uint IoctlUsbGetNodeConnectionInformationExV2 = 0x22045C;
    private const uint GenericWrite = 0x4000_0000;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    /// <summary>USB_PROTOCOLS.Usb300: the caller understands the V2 answer.</summary>
    private const uint Usb300 = 1 << 2;
    /// <summary>USB_CONNECTION_STATUS.DeviceConnected.</summary>
    private const int DeviceConnected = 1;

    private static readonly Guid GuidDevInterfaceUsbHub = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    // USB_NODE_CONNECTION_INFORMATION_EX is packed: ConnectionIndex (4),
    // USB_DEVICE_DESCRIPTOR (18), CurrentConfigurationValue (1), Speed (1),
    // DeviceIsHub (1), DeviceAddress (2), NumberOfOpenPipes (4),
    // ConnectionStatus (4), then one 11-byte USB_PIPE_INFO per open pipe.
    private const int SpeedOffset = 23;
    private const int ConnectionStatusOffset = 31;
    /// <summary>The record plus room for 30 pipes, as generous as USBView.</summary>
    private const int ConnectionBufferSize = 35 + 30 * 11;

    /// <summary>The link of the camera at <paramref name="interfacePath"/>, or null when any step fails.</summary>
    public static UsbLink? LinkOf(string interfacePath)
    {
        try
        {
            if (UsbLinks.InstanceId(interfacePath) is not { } id) return null;
            if (DevNode(id) is not { } node) return null;
            var device = UsbLinks.IsFunction(id) ? Parent(node) : node;
            if (device is not { } dev || PortNumber(dev) is not { } port) return null;
            if (Parent(dev) is not { } hubNode || HubInterface(hubNode) is not { } hubPath) return null;

            using var hub = CreateFileW(hubPath, GenericWrite, FileShareWrite, 0, OpenExisting, 0, 0);
            if (hub.IsInvalid) return null;
            return Speed(hub, port) is { } speed ? UsbLinks.LinkFrom(speed, SuperSpeedFlags(hub, port)) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static uint? DevNode(string id) =>
        CM_Locate_DevNodeW(out var node, id, CmLocateDevNodeNormal) == CrSuccess ? node : null;

    private static uint? Parent(uint node) =>
        CM_Get_Parent(out var above, node, 0) == CrSuccess ? above : null;

    private static unsafe uint? PortNumber(uint node)
    {
        uint port = 0, len = sizeof(uint);
        return CM_Get_DevNode_Registry_PropertyW(node, CmDrpAddress, 0, (nint)(&port), ref len, 0) == CrSuccess
            ? port
            : null;
    }

    private static unsafe string? DeviceId(uint node)
    {
        if (CM_Get_Device_ID_Size(out var len, node, 0) != CrSuccess) return null;
        var buffer = new char[len + 1];
        fixed (char* p = buffer)
            if (CM_Get_Device_IDW(node, p, (uint)buffer.Length, 0) != CrSuccess)
                return null;
        return new string(buffer, 0, (int)len);
    }

    /// <summary>The device-interface path that opens the hub at <paramref name="node"/> (its first interface).</summary>
    private static unsafe string? HubInterface(uint node)
    {
        if (DeviceId(node) is not { } id) return null;
        if (CM_Get_Device_Interface_List_SizeW(out var len, GuidDevInterfaceUsbHub, id, CmGetDeviceInterfaceListPresent) != CrSuccess
            || len == 0)
            return null;
        var list = new char[len];
        fixed (char* p = list)
            if (CM_Get_Device_Interface_ListW(GuidDevInterfaceUsbHub, id, p, len, CmGetDeviceInterfaceListPresent) != CrSuccess)
                return null;
        var end = Array.IndexOf(list, '\0');
        return end > 0 ? new string(list, 0, end) : null;
    }

    /// <summary>The USB_DEVICE_SPEED of the device on <paramref name="port"/>, when one is connected there.</summary>
    private static unsafe byte? Speed(SafeFileHandle hub, uint port)
    {
        var buffer = new byte[ConnectionBufferSize];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), port);
        fixed (byte* p = buffer)
            if (!DeviceIoControl(hub, IoctlUsbGetNodeConnectionInformationEx, (nint)p, (uint)buffer.Length,
                    (nint)p, (uint)buffer.Length, out _, 0))
                return null;
        return BitConverter.ToInt32(buffer, ConnectionStatusOffset) == DeviceConnected ? buffer[SpeedOffset] : null;
    }

    /// <summary>The V2 flags of the device on <paramref name="port"/>; null when the hub does not answer V2.</summary>
    private static unsafe uint? SuperSpeedFlags(SafeFileHandle hub, uint port)
    {
        // ConnectionIndex, Length, SupportedUsbProtocols, Flags — four ULONGs.
        var record = stackalloc uint[4];
        record[0] = port;
        record[1] = 16;
        record[2] = Usb300;
        record[3] = 0;
        return DeviceIoControl(hub, IoctlUsbGetNodeConnectionInformationExV2, (nint)record, 16, (nint)record, 16, out _, 0)
            ? record[3]
            : null;
    }

    internal sealed class SafeFileHandle() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Parent(out uint parent, uint node, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_Registry_PropertyW(uint node, uint property, nint regDataType, nint buffer, ref uint length, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Device_ID_Size(out uint length, uint node, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static unsafe partial uint CM_Get_Device_IDW(uint node, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_List_SizeW(out uint length, in Guid interfaceClass, string? deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint CM_Get_Device_Interface_ListW(in Guid interfaceClass, string? deviceId, char* buffer, uint length, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, nint inBuffer, uint inSize, nint outBuffer, uint outSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
