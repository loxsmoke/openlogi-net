using OpenLogi.Hid;

/// <summary>Lighting commands: solid colours, per-key, effects, brightness, animations.</summary>
static partial class Commands
{
    public static async Task LightAsync(string hex)
    {
        if (hex.Length != 6
            || !byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            Console.Error.WriteLine("usage: light <RRGGBB>  (e.g. light 00ff00)");
            return;
        }

        foreach (var inv in await Scan.Async())
        {
            foreach (var dev in inv.Paired)
            {
                if (dev.Capabilities?.Lighting != true) continue;
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;

                var label = dev.Codename ?? dev.Kind.ToString();
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) { Console.WriteLine($"{label}: could not open"); continue; }
                var ok = await session.ApplyLightingAsync(r, g, b);
                Console.WriteLine($"{label}: {(ok ? $"set to #{hex}" : "no supported lighting feature")}");
            }
        }
    }

    public static async Task BrightAsync(string[] args)
    {
        var target = args.Length > 1 && ushort.TryParse(args[1], out var v) ? v : (ushort)100;
        foreach (var inv in await Scan.Async())
        {
            foreach (var dev in inv.Paired)
            {
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                var label = dev.Codename ?? dev.Kind.ToString();
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;
                if (await session.ReadBrightnessAsync() is not { } before) continue;
                await session.ApplyBrightnessAsync(target);
                var after = await session.ReadBrightnessAsync();
                Console.WriteLine($"{label}: brightness {before.Current} → set {target} → read {after?.Current} (max {before.Info.MaxBrightness})");
            }
        }
    }

    public static async Task AnimAsync(string mode, string hex)
    {
        byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var br);
        byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var bg);
        byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var bb);
        var cycle = mode.Equals("cycle", StringComparison.OrdinalIgnoreCase);

        (byte, byte, byte) Frame(double tsec)
        {
            if (cycle)
            {
                var h = tsec / 6.0 * 360.0 % 360.0;
                var x = 1 - Math.Abs(h / 60.0 % 2 - 1);
                var (r, g, b) = (h / 60) switch
                { < 1 => (1.0, x, 0.0), < 2 => (x, 1.0, 0.0), < 3 => (0.0, 1.0, x), < 4 => (0.0, x, 1.0), < 5 => (x, 0.0, 1.0), _ => (1.0, 0.0, x) };
                return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
            }
            var k = 0.08 + 0.92 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * tsec / 3.0));
            return ((byte)(br * k), (byte)(bg * k), (byte)(bb * k));
        }

        foreach (var inv in await Scan.Async())
            foreach (var dev in inv.Paired)
            {
                if (dev.Capabilities?.Lighting != true) continue;
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;
                var (r0, g0, b0) = Frame(0);
                if (!await session.ApplyPerKeyColorAsync(r0, g0, b0)) { Console.WriteLine("no 0x8081"); continue; }
                Console.WriteLine($"{dev.Codename}: {mode} for 12s — watch the keyboard");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var frames = 0;
                while (sw.Elapsed.TotalSeconds < 12)
                {
                    var (r, g, b) = Frame(sw.Elapsed.TotalSeconds);
                    await session.ApplyPerKeyColorFastAsync(r, g, b);
                    frames++;
                    await Task.Delay(45);
                }
                Console.WriteLine($"  done ({frames} frames, {frames / 12.0:0.0} fps)");
            }
    }

    public static async Task ZoneProbeAsync(byte start)
    {
        // 8 distinct, namable colors for zones start..start+7.
        var colors = new (string Name, byte R, byte G, byte B)[]
        {
            ("RED", 255, 0, 0), ("GREEN", 0, 255, 0), ("BLUE", 0, 0, 255), ("YELLOW", 255, 255, 0),
            ("CYAN", 0, 255, 255), ("MAGENTA", 255, 0, 255), ("WHITE", 255, 255, 255), ("ORANGE", 255, 100, 0),
        };
        var map = new Dictionary<byte, (byte, byte, byte)>();
        for (byte i = 0; i < 8; i++) map[(byte)(start + i)] = (colors[i].R, colors[i].G, colors[i].B);

        foreach (var inv in await Scan.Async())
            foreach (var dev in inv.Paired)
            {
                if (dev.Capabilities?.Lighting != true) continue;
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;
                if (!await session.ApplyPerKeyMapAsync(map)) continue;
                for (byte i = 0; i < 8; i++) Console.WriteLine($"  zone 0x{start + i:x2} = {colors[i].Name}");
                Console.WriteLine("  (holding 12s)");
                await Task.Delay(12000);
            }
    }

    public static async Task SetKeyAsync(byte zoneId, string hex)
    {
        byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r);
        byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g);
        byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b);
        foreach (var inv in await Scan.Async())
            foreach (var dev in inv.Paired)
            {
                if (dev.Capabilities?.Lighting != true) continue;
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;
                var ok = await session.SetKeyZoneAsync(zoneId, r, g, b);
                Console.WriteLine($"{dev.Codename}: zone 0x{zoneId:x2} = #{hex} → {(ok ? "set (holding 8s)" : "no 0x8081")}");
                if (ok) await Task.Delay(8000);
            }
    }

    public static async Task PerKeyAsync(string hex)
    {
        if (hex.Length != 6
            || !byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            Console.Error.WriteLine("usage: perkey <RRGGBB>");
            return;
        }
        foreach (var inv in await Scan.Async())
        {
            foreach (var dev in inv.Paired)
            {
                if (dev.Capabilities?.Lighting != true) continue;
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                var label = dev.Codename ?? dev.Kind.ToString();
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;
                var ok = await session.ApplyPerKeyColorAsync(r, g, b);
                Console.WriteLine($"{label}: per-key #{hex} → {(ok ? "applied (holding 8s)" : "no 0x8081")}");
                if (ok) await Task.Delay(8000);
                Console.WriteLine("  released.");
            }
        }
    }

    public static async Task EffectAsync(string[] args)
    {
        // Diagnostic: apply an RgbEffects (0x8071) cluster-effect by index. Note that
        // on G-series keyboards with an onboard profile this is overridden unless the
        // device is in host mode (see `kbmode`) — kept as a low-level probe.
        if (args.Length < 2 || !byte.TryParse(args[1], out var idx))
        {
            Console.Error.WriteLine("usage: effect <index> [paramByteHex ...]   e.g. effect 1 ff 00 00");
            return;
        }
        byte persistence = 1; // OPENLOGI_PERSIST=2 (non-volatile) / 3 (both) to write the onboard profile
        if (byte.TryParse(Environment.GetEnvironmentVariable("OPENLOGI_PERSIST"), out var pv)) persistence = pv;

        var pars = new byte[10];
        for (var i = 2; i < args.Length && i - 2 < 10; i++)
            pars[i - 2] = Convert.ToByte(args[i], 16);

        foreach (var inv in await Scan.Async())
        {
            foreach (var dev in inv.Paired)
            {
                if (dev.Capabilities?.Lighting != true) continue;
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                var label = dev.Codename ?? dev.Kind.ToString();
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;
                var ok = await session.ApplyEffectAsync(idx, pars, persistence);
                Console.WriteLine($"{label}: effect {idx} [{Convert.ToHexString(pars)}] persist={persistence} → {(ok ? "applied (host mode, holding 12s)" : "no 0x8071")}");
                if (ok) await Task.Delay(12000);
                Console.WriteLine("  released.");
            }
        }
    }
}
