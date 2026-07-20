using OpenLogi.Hid;

/// <summary>Onboard-profile commands: mode switching, sector dump/patch/copy, CRC checks.</summary>
static partial class Commands
{
    public static async Task ProfilesAsync(string[] args)
    {
        byte? switchTo = args.Length > 1 && byte.TryParse(args[1], out var pi) ? pi : null;
        foreach (var inv in await Scan.Async())
            foreach (var dev in inv.Paired)
            {
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                var label = dev.Codename ?? dev.Kind.ToString();
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;
                if (await session.ReadProfilesAsync() is not { } p) continue;
                Console.WriteLine($"{label}: {p.Info.ProfileCount} onboard profiles (current {p.Current}), " +
                    $"{p.Info.ButtonCount} buttons, {p.Info.SectorCount} sectors x {p.Info.SectorSize}B, fmt {p.Info.ProfileFormat}");
                if (switchTo is { } target)
                {
                    var ok = await session.SwitchProfileAsync(target);
                    Console.WriteLine($"  switch to profile {target}: {(ok ? "ok" : "failed")}");
                    await Task.Delay(6000);
                }
            }
    }

    public static async Task KbModeAsync(string mode)
    {
        var host = mode.Equals("host", StringComparison.OrdinalIgnoreCase);
        foreach (var inv in await Scan.Async())
        {
            foreach (var dev in inv.Paired)
            {
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                var label = dev.Codename ?? dev.Kind.ToString();
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) { Console.WriteLine($"{label}: unreachable (link parked/asleep — keep tapping keys and retry)"); continue; }
                var ok = await session.SetOnboardModeAsync(host);
                Console.WriteLine($"{label}: {(ok ? $"set {(host ? "host" : "onboard")} mode" : "no onboard-profiles feature or the switch was refused")}");
            }
        }
    }

    public static async Task DumpProfileAsync(ushort sectorId)
    {
        foreach (var inv in await Scan.Async())
            foreach (var dev in inv.Paired)
            {
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;
                var data = new byte[256];
                var got = 0;
                for (var off = 0; off < 256; off += 16)
                {
                    try { Array.Copy(await session.ReadMemoryRawAsync(sectorId, (ushort)off) ?? new byte[16], 0, data, off, 16); got = off + 16; }
                    catch (Exception ex) { Console.WriteLine($"  stop at offset {off}: {ex.Message}"); break; }
                }
                if (got == 0) continue;
                Console.WriteLine($"{dev.Codename}: sector {sectorId} ({got} bytes):");
                for (var row = 0; row < got; row += 16)
                {
                    var hex = Convert.ToHexString(data.AsSpan(row, 16).ToArray());
                    var ascii = new string(data.AsSpan(row, 16).ToArray().Select(b => b is >= 0x20 and < 0x7f ? (char)b : '.').ToArray());
                    Console.WriteLine($"  {row:x3}: {hex}  {ascii}");
                }
                var utf16 = System.Text.Encoding.Unicode.GetString(data, 0, got).Replace('\0', '.');
                Console.WriteLine($"  utf16-le: '{utf16}'");
            }
    }

    public static async Task CrcCheckAsync(ushort sectorId)
    {
        foreach (var inv in await Scan.Async())
            foreach (var dev in inv.Paired)
            {
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;
                var data = await session.ReadFullSectorAsync(sectorId, 255);
                if (data is null) continue;
                Console.WriteLine($"{dev.Codename}: sector {sectorId} last 6 bytes = {Convert.ToHexString(data.AsSpan(249).ToArray())}");
                var crc = OpenLogi.HidPP.Crc16.Ccitt(data.AsSpan(0, 253));
                var stored = (ushort)((data[253] << 8) | data[254]);
                Console.WriteLine($"  crc[0..253]={crc:x4}  stored[253..255]BE={stored:x4}  {(crc == stored ? "*** MATCH ***" : "MISMATCH")}");
            }
    }

    // Safest write test: read a sector and write it back UNCHANGED (CRC recomputed),
    // then read again and confirm the body is byte-identical. Validates the write path
    // with no functional change.
    public static async Task WriteProfileRoundTripAsync(ushort sectorId)
    {
        foreach (var inv in await Scan.Async())
            foreach (var dev in inv.Paired)
            {
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;
                var before = await session.ReadFullSectorAsync(sectorId, 255);
                if (before is null) continue;
                Console.WriteLine($"{dev.Codename}: sector {sectorId} read {before.Length} bytes, writing back unchanged…");
                var ok = await session.WriteProfileSectorAsync(sectorId, (byte[])before.Clone());
                var after = await session.ReadFullSectorAsync(sectorId, 255);
                var identical = after is not null && before.AsSpan(0, 253).SequenceEqual(after.AsSpan(0, 253));
                Console.WriteLine($"  write={ok}, body identical after read-back={identical}");
            }
    }

    public static async Task CopyProfileAsync(ushort src, ushort dst)
    {
        foreach (var inv in await Scan.Async())
            foreach (var dev in inv.Paired)
            {
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;
                var data = await session.ReadFullSectorAsync(src, 255);
                if (data is null) continue;
                var ok = await session.WriteProfileSectorAsync(dst, (byte[])data.Clone());
                var back = await session.ReadFullSectorAsync(dst, 255);
                var same = back is not null && data.AsSpan(0, 253).SequenceEqual(back.AsSpan(0, 253));
                Console.WriteLine($"{dev.Codename}: copied sector {src} → {dst}: write={ok}, verified={same}");
            }
    }

    public static async Task PatchProfileAsync(ushort sector, int offset, byte[] bytes)
    {
        foreach (var inv in await Scan.Async())
            foreach (var dev in inv.Paired)
            {
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;
                var data = await session.ReadFullSectorAsync(sector, 255);
                if (data is null) continue;
                bytes.CopyTo(data, offset);
                var ok = await session.WriteProfileSectorAsync(sector, data);
                Console.WriteLine($"{dev.Codename}: patched sector {sector} @0x{offset:x} with {Convert.ToHexString(bytes)} → write={ok}");
            }
    }
}
