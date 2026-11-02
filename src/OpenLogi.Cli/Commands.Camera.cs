using OpenLogi.Camera;

/// <summary>
/// Webcam commands: <c>camera</c> reads and writes device-level UVC image controls
/// (the same path the GUI controls panel uses — changes land in the camera's own
/// registers, so Meet, Zoom and OBS see them too), and <c>snapshot</c> grabs one
/// frame to a PNG through the preview's capture path.
/// </summary>
static partial class Commands
{
    private static void PrintCameras(IReadOnlyList<CameraInfo> cameras)
    {
        Console.WriteLine($"Cameras ({cameras.Count} Logitech UVC)");
        for (var i = 0; i < cameras.Count; i++)
        {
            var cam = cameras[i];
            var prefix = i == cameras.Count - 1 ? "  └─" : "  ├─";
            var link = Cameras.UsbLinkFor(cam.UniqueId) is { } l ? $", {l.Speed.Label()}" : "";
            Console.WriteLine($"{prefix} ● {cam.Name} (camera, vid={cam.VendorId:x4} pid={cam.ProductId:x4}{link}, id={cam.UniqueId})");
        }
    }

    /// <summary><c>camera [get]</c> / <c>camera set &lt;name&gt; &lt;value&gt;</c>, optionally <c>--id &lt;unique id&gt;</c>.</summary>
    public static async Task<int> CameraAsync(string[] args)
    {
        var (rest, id) = TakeIdOption(args);
        if (await PickCameraAsync(id) is not { } camera) return 1;

        switch (rest.Length > 0 ? rest[0] : "get")
        {
            case "get":
            {
                var state = await Cameras.ReadStateAsync(camera.UniqueId);
                Console.WriteLine($"{camera.Name} ({camera.UniqueId})");
                if (state.Controls.Count == 0) Console.WriteLine("  (no adjustable controls)");
                foreach (var (control, r) in state.Controls)
                    Console.WriteLine($"  {control.Name(),-24} {r.Current,6}   (min {r.Min}, max {r.Max}, default {r.Default})");
                foreach (var (toggle, st) in state.Autos)
                    Console.WriteLine($"  {toggle.Name(),-24} {(st.Current ? 1 : 0),6}   (default {(st.Default ? 1 : 0)})");
                return 0;
            }
            case "set" when rest.Length >= 3 && int.TryParse(rest[2], out var value):
                try
                {
                    if (CameraControlNames.ParseControl(rest[1]) is { } control)
                        await Cameras.SetControlAsync(camera.UniqueId, control, value);
                    else if (CameraControlNames.ParseToggle(rest[1]) is { } toggle)
                        await Cameras.SetAutoAsync(camera.UniqueId, toggle, value != 0);
                    else
                    {
                        Console.Error.WriteLine($"unknown control '{rest[1]}'. Controls: "
                            + string.Join(", ", CameraControlNames.AllControls.Select(c => c.Name())
                                .Concat(CameraControlNames.AllToggles.Select(t => t.Name()))));
                        return 1;
                    }
                }
                catch (CameraControlException e)
                {
                    Console.Error.WriteLine($"camera write failed: {e.Message}");
                    return 1;
                }
                Console.WriteLine($"{rest[1]} = {value}");
                return 0;
            default:
                Console.Error.WriteLine("usage: camera [get | set <name> <value>] [--id <unique id>]");
                return 1;
        }
    }

    /// <summary><c>snapshot &lt;out.png&gt;</c>, optionally <c>--id &lt;unique id&gt;</c>.</summary>
    public static async Task<int> SnapshotAsync(string[] args)
    {
        var (rest, id) = TakeIdOption(args);
        if (rest.Length < 1)
        {
            Console.Error.WriteLine("usage: snapshot <out.png> [--id <unique id>]");
            return 1;
        }
        if (await PickCameraAsync(id) is not { } camera) return 1;
        try
        {
            var frame = await Task.Run(() => Cameras.CaptureFrame(camera.UniqueId, TimeSpan.FromSeconds(5)));
            await using (var file = File.Create(rest[0]))
                PngEncoder.Write(frame, file);
            Console.WriteLine($"{camera.Name}: {frame.Width}x{frame.Height} → {rest[0]}");
            return 0;
        }
        catch (CaptureException e)
        {
            Console.Error.WriteLine($"capture failed: {e.Message}");
            return 1;
        }
    }

    private static (string[] Remaining, string? Id) TakeIdOption(string[] args)
    {
        var at = Array.IndexOf(args, "--id");
        if (at < 0 || at + 1 >= args.Length) return (args, null);
        return ([.. args[..at], .. args[(at + 2)..]], args[at + 1]);
    }

    /// <summary>The camera with <paramref name="id"/>, or the first Logitech camera.</summary>
    private static async Task<CameraInfo?> PickCameraAsync(string? id)
    {
        var cameras = await Cameras.EnumerateAsync();
        var camera = id is null
            ? cameras.FirstOrDefault()
            : cameras.FirstOrDefault(c => string.Equals(c.UniqueId, id, StringComparison.OrdinalIgnoreCase));
        if (camera is null)
            Console.Error.WriteLine(id is null ? "No Logitech camera found." : $"No Logitech camera with id {id}.");
        return camera;
    }
}
