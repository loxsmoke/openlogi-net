using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OpenLogi.Camera.Interop;

// The Media Foundation sensor activity monitor (mfidl.h, Windows 10 1809+): which
// processes are streaming from each camera, and in which Frame Server mode.

[GeneratedComInterface]
[Guid("39DC7F4A-B141-4719-813C-A7F46162A2B8")]
internal partial interface IMFSensorProcessActivity
{
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int GetStreamingState(out int streaming);
    /// <summary>MFSensorDeviceMode: 0 = controller, 1 = shared.</summary>
    [PreserveSig] int GetStreamingMode(out int mode);
    [PreserveSig] int GetReportTime(out long fileTime);
}

[GeneratedComInterface]
[Guid("3E8C4BE1-A8C2-4528-90DE-2851BDE5FEAD")]
internal unsafe partial interface IMFSensorActivityReport
{
    [PreserveSig] int GetFriendlyName(char* name, uint capacity, out uint written);
    [PreserveSig] int GetSymbolicLink(char* link, uint capacity, out uint written);
    [PreserveSig] int GetProcessCount(out uint count);
    [PreserveSig] int GetProcessActivity(uint index, out nint activity);
}

[GeneratedComInterface]
[Guid("683F7A5E-4A19-43CD-B1A9-DBF4AB3F7777")]
internal partial interface IMFSensorActivitiesReport
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetActivityReport(uint index, out nint report);
    [PreserveSig] int GetActivityReportByDeviceName(nint symbolicName, out nint report);
}

[GeneratedComInterface]
[Guid("DE5072EE-DBE3-46DC-8A87-B6F631194751")]
internal partial interface IMFSensorActivitiesReportCallback
{
    [PreserveSig] int OnActivitiesReport(nint report);
}

[GeneratedComInterface]
[Guid("D0CEF145-B3F4-4340-A2E5-7A5080CA05CB")]
internal partial interface IMFSensorActivityMonitor
{
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
}

/// <summary>The managed side of the monitor's callback; Windows calls it on a Media Foundation thread.</summary>
[GeneratedComClass]
internal sealed partial class SensorActivityCallback(Action<IMFSensorActivitiesReport> onReport)
    : IMFSensorActivitiesReportCallback
{
    public int OnActivitiesReport(nint report)
    {
        if (report == 0) return 0;
        try
        {
            // The report is borrowed for the call: take our own reference for the wrapper.
            Marshal.AddRef(report);
            var wrapper = Com.Wrap<IMFSensorActivitiesReport>(report);
            try { onReport(wrapper); }
            finally { Com.Release(wrapper); }
        }
        catch
        {
            // A callback must never throw back into Media Foundation.
        }
        return 0;
    }
}

internal static partial class SensorNative
{
    [LibraryImport("mfsensorgroup.dll")]
    public static partial int MFCreateSensorActivityMonitor(nint callback, out nint monitor);
}
