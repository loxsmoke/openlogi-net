using OpenLogi.Core;
using OpenLogi.Core.Logging;

namespace OpenLogi.Input;

/// <summary>
/// Grows and restores the mouse pointer — the mechanism behind shake-to-locate.
/// Windows offers no "make the pointer bigger for a moment" API, so
/// <see cref="Apply"/> swaps the standard system cursors for bigger ones
/// (<c>SetSystemCursor</c>) and <see cref="Restore"/> asks the system to reload the
/// user's own (<c>SPI_SETCURSORS</c>).
///
/// <para>
/// The bigger cursor is loaded from the user's own cursor file at the size wanted,
/// not scaled up from the 32-pixel one on screen — see <see cref="LoadScaled"/>,
/// which is what keeps it crisp.
/// </para>
///
/// <para>
/// <see cref="Apply"/> takes any scale and is called once per animation frame, which
/// is what makes the pointer grow and shrink smoothly rather than jumping: a frame
/// whose scale rounds to the pixel size already on screen costs nothing.
/// </para>
///
/// <para>
/// Writing <c>CursorBaseSize</c> — what the Settings pointer-size slider does — was
/// the obvious alternative and does not work for this: the value is applied at
/// sign-in, and a reload afterwards leaves the pointer at its old size (VERIFIED on
/// Windows 11: the registry value changed, a freshly loaded arrow stayed 32 px).
/// It is still read here, since it *is* the size the user is looking at and so the
/// right base to scale from. Nothing writes it, so the user's saved setting is
/// never touched.
/// </para>
///
/// <para>
/// The swap is session-wide while it lasts, so every route back matters:
/// <see cref="Restore"/> on the way down, and a marker file that lets
/// <see cref="RecoverStaleCursors"/> put the user's cursors back on the next launch
/// if the process dies while the pointer is big.
/// </para>
/// </summary>
public static class CursorSize
{
    /// <summary>The Windows default pointer size, and the size assumed when the setting is absent.</summary>
    public const int DefaultBaseSize = 32;

    /// <summary>Largest pointer size to scale to (the top of the Windows pointer-size slider).</summary>
    public const int MaxBaseSize = 256;

    /// <summary>
    /// Frame sizes are rounded to this many pixels. Redrawing every cursor costs about
    /// 10 ms (MEASURED: 14 cursors reloaded from their files), so a step the eye cannot
    /// pick out mid-animation is worth roughly a third of the passes.
    /// </summary>
    public const int SizeStep = 4;

    private static readonly object Gate = new();

    // The pixel size currently on screen (0 = the user's own cursors), and the size
    // they are being scaled from. The base is read once, when an enlargement begins,
    // so every frame of one animation scales from the same number.
    private static int _appliedSize;
    private static int _baseSize;

    // The user's cursor files, read with the base size when an enlargement begins: the
    // scheme can change between enlargements, but not during one.
    private static Dictionary<int, string>? _schemeFiles;

    /// <summary>Whether the system cursors are currently our enlarged copies.</summary>
    public static bool IsEnlarged
    {
        get { lock (Gate) return _appliedSize != 0; }
    }

    /// <summary>
    /// The size to grow to from <paramref name="baseSize"/> at <paramref name="scale"/>×,
    /// rounded to <see cref="SizeStep"/> and capped at the largest size Windows draws.
    /// Pure.
    /// </summary>
    public static int TargetSize(int baseSize, double scale)
    {
        var from = Math.Clamp(baseSize, DefaultBaseSize, MaxBaseSize);
        var wanted = (long)(Math.Round(from * Math.Max(1, scale) / SizeStep) * SizeStep);
        return (int)Math.Clamp(wanted, from, MaxBaseSize);
    }

    /// <summary>
    /// Draw the pointer at <paramref name="scale"/>× the user's own size — one frame of
    /// the grow/shrink animation. A scale of 1 or less restores. Returns <c>false</c>
    /// when nothing is enlarged afterwards: at 1×, when the user's size is already the
    /// maximum Windows draws, or when no cursor could be scaled.
    /// </summary>
    public static bool Apply(double scale)
    {
        lock (Gate)
        {
            if (scale <= 1)
            {
                RestoreLocked();
                return false;
            }

            if (_appliedSize == 0)
            {
                _baseSize = ReadBaseSize() ?? DefaultBaseSize;
                _schemeFiles = ReadSchemeFiles();
            }
            var target = TargetSize(_baseSize, scale);
            if (target <= _baseSize) return false;
            if (target == _appliedSize) return true; // this frame lands on the size already drawn

            // The marker goes down first: a crash between here and the restore would
            // otherwise leave the session's cursors swapped with no record of it.
            if (_appliedSize == 0) WriteMarker(target);

            var replaced = 0;
            foreach (var id in Native.SystemCursorIds)
                if (Replace(id, target)) replaced++;

            if (replaced == 0)
            {
                DiagnosticLog.Warn("cursor", "no system cursor could be scaled");
                // Only the frame that started the enlargement owns the marker; a failed
                // frame mid-animation leaves the earlier one in place, still to be undone.
                if (_appliedSize == 0) ClearMarker();
                return _appliedSize != 0;
            }
            _appliedSize = target;
            return true;
        }
    }

    /// <summary>Put the user's own cursors back. A no-op unless we replaced them.</summary>
    public static void Restore()
    {
        lock (Gate) RestoreLocked();
    }

    private static void RestoreLocked()
    {
        if (_appliedSize == 0) return;
        // Drop the state even if the reload fails, so a wedged call can't make every
        // later shake a no-op; the marker stays behind for the next launch to retry.
        _appliedSize = 0;
        _baseSize = 0;
        _schemeFiles = null;
        if (ReloadUserCursors()) ClearMarker();
    }

    /// <summary>
    /// Put back cursors left enlarged by a previous run that exited without restoring.
    /// Called once at startup; a no-op when there is no marker (the normal case).
    /// </summary>
    public static void RecoverStaleCursors()
    {
        lock (Gate)
        {
            if (_appliedSize != 0) return; // this run owns the cursors
            if (!MarkerExists()) return;
            DiagnosticLog.Info("cursor", "restoring cursors left enlarged by a previous run");
            if (ReloadUserCursors()) ClearMarker();
        }
    }

    /// <summary>Replace one standard cursor with a <paramref name="size"/>-pixel copy of itself.</summary>
    private static bool Replace(int id, int size)
    {
        try
        {
            var scaled = LoadScaled(id, size);
            if (scaled == nint.Zero) return false;

            // SetSystemCursor takes ownership of the handle and destroys it itself;
            // only a failed call leaves it ours to clean up.
            if (Native.SetSystemCursor(scaled, id)) return true;
            Native.DestroyCursor(scaled);
            return false;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("cursor", $"scaling cursor {id} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// A <paramref name="size"/>-pixel cursor for <paramref name="id"/>, as crisp as the
    /// source allows.
    ///
    /// <para>
    /// The cursor's own file is asked for at the size wanted, because Windows' stock
    /// cursors are multi-resolution — aero_arrow.cur holds native 32/48/64/96/128-pixel
    /// images — and <c>LoadImage</c> picks the closest one. MEASURED on Windows 11: the
    /// 96-pixel arrow loaded this way keeps 90 hard alpha edges, where copying the
    /// on-screen 32-pixel cursor up to 96 keeps none at all — the smearing this fixes.
    /// <c>LR_COPYFROMRESOURCE</c>, which is documented to reload from the resource,
    /// measured pixel-identical to a plain stretch here, so it is not a route to the
    /// bigger images.
    /// </para>
    ///
    /// <para>
    /// A cursor the scheme leaves to a built-in resource (typically the I-beam) has no
    /// file and no size ladder — <c>LoadImage</c> of an OEM id returns 32 pixels with
    /// <c>LR_SHARED</c> and fails without it — so those fall back to the stretch.
    /// </para>
    /// </summary>
    private static nint LoadScaled(int id, int size)
    {
        if (_schemeFiles is { } files && files.TryGetValue(id, out var path))
        {
            var native = Native.LoadImageFileW(nint.Zero, path, Native.IMAGE_CURSOR, size, size, Native.LR_LOADFROMFILE);
            if (native != nint.Zero) return native;
        }

        // Shared handle: owned by the system, must not be destroyed.
        var current = Native.LoadImageW(nint.Zero, id, Native.IMAGE_CURSOR, 0, 0, Native.LR_SHARED);
        if (current == nint.Zero) return nint.Zero;
        return Native.CopyImage(current, Native.IMAGE_CURSOR, size, size, 0);
    }

    /// <summary>
    /// The cursor files of the user's current scheme, by OCR_* id. Cursors the scheme
    /// leaves empty, and paths that no longer exist, are left out — the caller falls
    /// back for those.
    /// </summary>
    private static Dictionary<int, string> ReadSchemeFiles()
    {
        var files = new Dictionary<int, string>();
        foreach (var (id, value) in Native.CursorSchemeValues)
        {
            try
            {
                var buffer = new char[512];
                var bytes = (uint)(buffer.Length * sizeof(char));
                var status = Native.RegGetValueStringW(
                    Native.HKEY_CURRENT_USER, Native.CursorsKey, value,
                    Native.RRF_RT_REG_SZ | Native.RRF_RT_REG_EXPAND_SZ, out _, buffer, ref bytes);
                if (status != Native.ERROR_SUCCESS || bytes <= sizeof(char)) continue;

                var path = new string(buffer, 0, (int)(bytes / sizeof(char)) - 1).Trim();
                if (path.Length == 0) continue;
                // RegGetValue expands REG_EXPAND_SZ itself; this covers a plain REG_SZ
                // that was written with the variable left in (schemes do both).
                path = Environment.ExpandEnvironmentVariables(path);
                if (File.Exists(path)) files[id] = path;
            }
            catch { /* one unreadable value just means that cursor falls back */ }
        }
        return files;
    }

    /// <summary>Reload every system cursor from the user's own settings.</summary>
    private static bool ReloadUserCursors()
    {
        try
        {
            if (Native.SystemParametersInfoW(Native.SPI_SETCURSORS, 0, nint.Zero, Native.SPIF_SENDCHANGE))
                return true;
            DiagnosticLog.Warn("cursor", "SPI_SETCURSORS failed");
            return false;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("cursor", $"cursor restore failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The user's configured pointer size, or <c>null</c> when the setting is absent.</summary>
    private static int? ReadBaseSize()
    {
        try
        {
            uint data = 0;
            uint size = sizeof(uint);
            var status = Native.RegGetValueW(
                Native.HKEY_CURRENT_USER, Native.CursorsKey, Native.CursorBaseSizeValue,
                Native.RRF_RT_REG_DWORD, out _, ref data, ref size);
            if (status != Native.ERROR_SUCCESS || data == 0) return null;
            return Math.Clamp((int)data, DefaultBaseSize, MaxBaseSize);
        }
        catch { return null; }
    }

    private static void WriteMarker(int size)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir());
            File.WriteAllText(Paths.CursorSizeRestorePath(), size.ToString());
        }
        catch { /* best effort: the in-memory restore still covers a clean exit */ }
    }

    private static bool MarkerExists()
    {
        try { return File.Exists(Paths.CursorSizeRestorePath()); } catch { return false; }
    }

    private static void ClearMarker()
    {
        try { File.Delete(Paths.CursorSizeRestorePath()); } catch { /* best effort */ }
    }
}
