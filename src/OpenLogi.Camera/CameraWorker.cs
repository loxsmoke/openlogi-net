using System.Collections.Concurrent;
using OpenLogi.Camera.Interop;

namespace OpenLogi.Camera;

/// <summary>
/// One dedicated MTA thread that runs every enumeration and control call in turn.
/// COM calls need an apartment the UI thread (an STA) must not lend them, and one
/// thread serializing device opens means a control write can never race a scan
/// that would otherwise see the camera mid-open.
/// </summary>
internal static class CameraWorker
{
    private static readonly BlockingCollection<Action> Queue = new();
    private static readonly Lazy<Thread> Thread = new(Start);

    public static Task<T> Run<T>(Func<T> work)
    {
        _ = Thread.Value;
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    public static Task Run(Action work) => Run(() => { work(); return true; });

    private static Thread Start()
    {
        var thread = new Thread(Loop) { IsBackground = true, Name = "openlogi-camera-worker" };
        thread.Start();
        return thread;
    }

    private static void Loop()
    {
        _ = MfNative.CoInitializeEx(0, MfNative.CoInitMultithreaded);
        foreach (var work in Queue.GetConsumingEnumerable())
            work();
    }
}
