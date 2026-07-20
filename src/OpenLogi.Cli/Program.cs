// Minimal CLI entry point (a richer System.CommandLine surface is a later polish).
// Command handlers live in the Commands.*.cs partials; Scan.cs holds the shared
// enumeration entry.
// `--rx` anywhere in the args scopes every enumeration to receiver nodes only,
// skipping the ping timeouts of direct devices (Yeti, BLE mice) — the sweep drops
// from ~8-10 s to ~1-2 s, which matters when racing a parked keyboard's wake window.
if (args.Contains("--rx"))
{
    Scan.ReceiversOnly = true;
    args = [.. args.Where(a => a != "--rx")];
}
var command = args.Length > 0 ? args[0] : "list";

switch (command)
{
    case "list":
        await Commands.ListAsync();
        break;
    case "diag":
        await Commands.DiagAsync();
        break;
    case "controls":
        await Commands.ControlsAsync();
        break;
    case "assets":
        await Commands.AssetsAsync();
        break;
    case "light":
        await Commands.LightAsync(args.Length > 1 ? args[1] : "ffffff");
        break;
    case "hosts":
        await Commands.HostsAsync();
        break;
    case "kbinfo":
        await Commands.KbInfoAsync();
        break;
    case "bright":
        await Commands.BrightAsync(args);
        break;
    case "kbmode":
        await Commands.KbModeAsync(args.Length > 1 ? args[1] : "onboard");
        break;
    case "profiles":
        await Commands.ProfilesAsync(args);
        break;
    case "dumpprofile":
        await Commands.DumpProfileAsync(args.Length > 1 && ushort.TryParse(args[1], out var sid) ? sid : (ushort)1);
        break;
    case "crccheck":
        await Commands.CrcCheckAsync(args.Length > 1 && ushort.TryParse(args[1], out var cs) ? cs : (ushort)1);
        break;
    case "writeprofile":
        await Commands.WriteProfileRoundTripAsync(args.Length > 1 && ushort.TryParse(args[1], out var ws) ? ws : (ushort)3);
        break;
    case "copyprofile":
        await Commands.CopyProfileAsync(ushort.Parse(args[1]), ushort.Parse(args[2]));
        break;
    case "patchprofile":
        await Commands.PatchProfileAsync(ushort.Parse(args[1]), Convert.ToInt32(args[2], 16),
            args[3..].Select(h => Convert.ToByte(h, 16)).ToArray());
        break;
    case "effect":
        await Commands.EffectAsync(args);
        break;
    case "perkey":
        await Commands.PerKeyAsync(args.Length > 1 ? args[1] : "ff0000");
        break;
    case "setkey":
        await Commands.SetKeyAsync(Convert.ToByte(args[1], 16), args.Length > 2 ? args[2] : "ff0000");
        break;
    case "zoneprobe":
        await Commands.ZoneProbeAsync(args.Length > 1 ? Convert.ToByte(args[1], 16) : (byte)0);
        break;
    case "anim":
        await Commands.AnimAsync(args.Length > 1 ? args[1] : "breathing", args.Length > 2 ? args[2] : "00ff00");
        break;
    case "pair":
        await Commands.PairAsync(args.Length > 1 && byte.TryParse(args[1], out var pt) ? pt : (byte)60);
        break;
    case "powerprobe":
        await Commands.PowerProbeAsync();
        break;
    case "powerset":
        await Commands.PowerSetAsync(args.Length > 1 ? Convert.ToByte(args[1], 16) : (byte)0);
        break;
    case "rawfeat":
        await Commands.RawFeatureAsync(Convert.ToUInt16(args[1], 16),
            args.Length > 2 ? Convert.ToByte(args[2], 16) : (byte)0,
            args[3..].Select(h => Convert.ToByte(h, 16)).ToArray());
        break;
    default:
        Console.Error.WriteLine($"unknown command '{command}'. Available: list, diag, controls, assets, light <RRGGBB>, hosts, kbinfo, bright <0-100>, kbmode <onboard|host>, effect <idx> [params hex...]");
        return 1;
}

return 0;
