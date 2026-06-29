using OpenLogi.HidPP.Channel;

namespace OpenLogi.HidPP.Feature;

/// <summary>
/// The `OnboardProfiles` / 0x8100 feature (G-series keyboards/mice). The device
/// runs either from onboard flash profiles or under host (software) control;
/// software lighting/remap writes are ignored — the onboard profile keeps
/// replaying — until the device is switched to host mode. Minimal port: just the
/// mode switch (function indices per the HID++ 0x8100 spec / libratbag).
/// </summary>
public sealed class OnboardProfilesFeature(FeatureEndpoint endpoint) : ICreatableFeature<OnboardProfilesFeature>
{
    public static ushort Id => 0x8100;
    public static byte StartingVersion => 0;
    public static OnboardProfilesFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(new FeatureEndpoint(channel, deviceIndex, featureIndex));

    /// <summary>Run from onboard flash profiles.</summary>
    public const byte ModeOnboard = 0x01;
    /// <summary>Run under host (software) control — required for software lighting.</summary>
    public const byte ModeHost = 0x02;

    /// <summary>Set the device mode (function 1: setOnboardMode).</summary>
    public async Task SetModeAsync(byte mode) =>
        await endpoint.CallAsync(1, [mode, 0, 0]).ConfigureAwait(false);

    /// <summary>Get the current device mode (function 2: getOnboardMode).</summary>
    public async Task<byte> GetModeAsync() =>
        (await endpoint.CallAsync(2, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload()[0];

    /// <summary>Onboard-profiles description (function 0: getOnboardProfilesInfo).</summary>
    public async Task<OnboardProfilesInfo> GetInfoAsync()
    {
        var p = (await endpoint.CallAsync(0, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload();
        // [0]=memoryModel [1]=profileFormat [2]=macroFormat [3]=profileCount [4]=profileCountOOB
        // [5]=buttonCount [6]=sectorCount [7..8]=sectorSize
        return new OnboardProfilesInfo(p[0], p[1], p[3], p[4], p[5], p[6], (ushort)((p[7] << 8) | p[8]));
    }

    /// <summary>Index (1-based) of the currently active profile (function 4: getCurrentProfile).</summary>
    public async Task<byte> GetCurrentProfileAsync() =>
        (await endpoint.CallAsync(4, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload()[1];

    /// <summary>Switch the active onboard profile (function 3: setCurrentProfile), 1-based index.</summary>
    public async Task SetCurrentProfileAsync(byte profileIndex) =>
        await endpoint.CallAsync(3, [0, profileIndex, 0]).ConfigureAwait(false);

    /// <summary>Read 16 bytes from a profile/ROM sector at <paramref name="offset"/> (function 5: readMemory).</summary>
    public async Task<byte[]> ReadMemoryAsync(ushort sectorId, ushort offset)
    {
        var args = new byte[16];
        args[0] = (byte)(sectorId >> 8);
        args[1] = (byte)(sectorId & 0xff);
        args[2] = (byte)(offset >> 8);
        args[3] = (byte)(offset & 0xff);
        return (await endpoint.CallLongAsync(5, args).ConfigureAwait(false)).ExtendPayload();
    }

    /// <summary>Begin a sector write of <paramref name="count"/> bytes (function 6: startWrite).</summary>
    public async Task StartWriteAsync(ushort sectorId, ushort subAddress, ushort count)
    {
        var args = new byte[16];
        args[0] = (byte)(sectorId >> 8);
        args[1] = (byte)(sectorId & 0xff);
        args[2] = (byte)(subAddress >> 8);
        args[3] = (byte)(subAddress & 0xff);
        args[4] = (byte)(count >> 8);
        args[5] = (byte)(count & 0xff);
        await endpoint.CallLongAsync(6, args).ConfigureAwait(false);
    }

    /// <summary>Stream the next 16 bytes of the sector being written (function 7: writeMemory).</summary>
    public async Task WriteMemoryAsync(ReadOnlyMemory<byte> chunk)
    {
        var args = new byte[16];
        chunk.Span[..Math.Min(chunk.Length, 16)].CopyTo(args);
        await endpoint.CallLongAsync(7, args).ConfigureAwait(false);
    }

    /// <summary>Commit the written sector to flash (function 8: endWrite).</summary>
    public async Task EndWriteAsync() =>
        await endpoint.CallLongAsync(8, new byte[16]).ConfigureAwait(false);
}

/// <summary>Static description of the device's onboard-profile memory.</summary>
public readonly record struct OnboardProfilesInfo(
    byte MemoryModel, byte ProfileFormat, byte ProfileCount, byte ProfileCountOob,
    byte ButtonCount, byte SectorCount, ushort SectorSize);
