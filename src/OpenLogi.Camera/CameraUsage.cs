using System.Runtime.InteropServices;
using OpenLogi.Camera.Interop;

namespace OpenLogi.Camera;

/// <summary>One process streaming from a camera, and whether it controls the camera (sets its mode) or shares it.</summary>
public readonly record struct CameraClient(uint ProcessId, bool Controlling);

/// <summary>
/// Which processes are streaming from each camera, from the Windows sensor activity
/// monitor. The Camera tab uses it to tell whether another app holds the camera,
/// which decides whether the preview may take exclusive access.
/// </summary>
public static class CameraUsage
{
    private static readonly Lock Gate = new();
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;
    private static Dictionary<string, CameraClient[]> _streaming = new(StringComparer.OrdinalIgnoreCase);
    private static IMFSensorActivityMonitor? _monitor;
    private static SensorActivityCallback? _callback;
    private static bool _started;

    /// <summary>Raised (on a Media Foundation thread) when the set of streaming processes changes.</summary>
    public static event Action? Changed;

    /// <summary>Start the monitor once for the process. False when Windows has none (it then reports nobody).</summary>
    public static Task<bool> StartAsync()
    {
        if (!Cameras.Supported) return Task.FromResult(false);
        return CameraWorker.Run(() =>
        {
            lock (Gate)
            {
                if (_started) return _monitor is not null;
                _started = true;
            }
            try
            {
                MfNative.EnsureStarted();
                var callback = new SensorActivityCallback(OnReport);
                var callbackPtr = Com.PointerFor(callback, typeof(IMFSensorActivitiesReportCallback).GUID);
                int hr;
                nint monitorPtr;
                try { hr = SensorNative.MFCreateSensorActivityMonitor(callbackPtr, out monitorPtr); }
                finally { Marshal.Release(callbackPtr); }
                if (Com.Failed(hr)) return false;
                var monitor = Com.Wrap<IMFSensorActivityMonitor>(monitorPtr);
                if (Com.Failed(monitor.Start()))
                {
                    Com.Release(monitor);
                    return false;
                }
                lock (Gate) { _monitor = monitor; _callback = callback; }
                return true;
            }
            catch (Exception e) when (e is COMException or DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        });
    }

    /// <summary>Processes other than this one streaming from the camera at <paramref name="uniqueId"/>.</summary>
    public static IReadOnlyList<CameraClient> OtherClients(string uniqueId)
    {
        lock (Gate)
            return _streaming.TryGetValue(DevicePath.DeviceInstance(uniqueId), out var clients)
                ? [.. clients.Where(c => c.ProcessId != OwnProcessId)]
                : [];
    }

    private static unsafe void OnReport(IMFSensorActivitiesReport report)
    {
        if (Com.Failed(report.GetCount(out var count))) return;
        var streaming = new Dictionary<string, CameraClient[]>(StringComparer.OrdinalIgnoreCase);
        var link = stackalloc char[1024];
        for (uint i = 0; i < count; i++)
        {
            if (Com.Failed(report.GetActivityReport(i, out var p)) || p == 0) continue;
            var device = Com.Wrap<IMFSensorActivityReport>(p);
            try
            {
                if (Com.Failed(device.GetSymbolicLink(link, 1024, out _))) continue;
                var key = DevicePath.DeviceInstance(new string(link));
                if (Com.Failed(device.GetProcessCount(out var processes))) continue;
                var clients = new List<CameraClient>();
                for (uint j = 0; j < processes; j++)
                {
                    if (Com.Failed(device.GetProcessActivity(j, out var a)) || a == 0) continue;
                    var activity = Com.Wrap<IMFSensorProcessActivity>(a);
                    try
                    {
                        if (!Com.Failed(activity.GetStreamingState(out var on)) && on != 0
                            && !Com.Failed(activity.GetProcessId(out var pid)))
                        {
                            var controlling = Com.Failed(activity.GetStreamingMode(out var mode)) || mode == 0;
                            clients.Add(new CameraClient(pid, controlling));
                        }
                    }
                    finally { Com.Release(activity); }
                }
                if (clients.Count > 0) streaming[key] = [.. clients];
            }
            finally { Com.Release(device); }
        }

        bool changed;
        lock (Gate)
        {
            changed = !SameAs(_streaming, streaming);
            _streaming = streaming;
        }
        if (changed) Changed?.Invoke();
    }

    private static bool SameAs(Dictionary<string, CameraClient[]> a, Dictionary<string, CameraClient[]> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && kv.Value.SequenceEqual(other));
}
