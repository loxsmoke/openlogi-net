using System.Runtime.InteropServices;
using OpenLogi.Core;

namespace OpenLogi.Input;

/// <summary>
/// A running <c>WH_MOUSE_LL</c> low-level mouse hook. Ported from Rust
/// <c>openlogi-hook::windows</c>. A dedicated thread owns the hook and pumps its
/// message loop; <see cref="Dispose"/> posts <c>WM_QUIT</c> and joins it.
///
/// HARDWARE-UNVERIFIED: installing the hook + suppression behaviour needs an
/// interactive desktop session to exercise. <see cref="TranslateEvent"/> is pure
/// and unit-tested.
/// </summary>
public sealed class MouseHook : IDisposable
{
    private const float WheelDelta = 120.0f;

    // Only one hook may be installed at a time (matches the Rust static CALLBACK).
    private static readonly object Gate = new();
    private static Func<MouseEvent, EventDisposition>? _callback;
    private static Native.HookProc? _proc; // kept alive against GC while installed

    private Thread? _thread;
    private uint _threadId;

    private MouseHook() { }

    /// <summary>Install the hook and deliver events to <paramref name="callback"/> (called on the hook thread).</summary>
    public static MouseHook Start(Func<MouseEvent, EventDisposition> callback)
    {
        lock (Gate)
        {
            if (_callback is not null)
                throw new HookException("another mouse hook is already installed");
            _callback = callback;
        }

        var hook = new MouseHook();
        using var ready = new SemaphoreSlim(0, 1);
        Exception? startError = null;

        hook._thread = new Thread(() =>
        {
            _proc = HookCallback;
            var handle = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _proc, nint.Zero, 0);
            if (handle == nint.Zero)
            {
                startError = new HookException($"SetWindowsHookExW failed: {Marshal.GetLastWin32Error()}");
                ready.Release();
                return;
            }
            hook._threadId = Native.GetCurrentThreadId();
            ready.Release();

            // Message loop: GetMessage returns <= 0 on WM_QUIT / error.
            while (Native.GetMessageW(out var msg, nint.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }

            Native.UnhookWindowsHookEx(handle);
        })
        { IsBackground = true, Name = "openlogi-windows-hook" };

        hook._thread.Start();
        ready.Wait();

        if (startError is not null)
        {
            lock (Gate) { _callback = null; _proc = null; }
            throw startError;
        }
        return hook;
    }

    public void Dispose()
    {
        if (_thread is null) return;
        if (_threadId != 0)
            Native.PostThreadMessageW(_threadId, Native.WM_QUIT, nint.Zero, nint.Zero);
        _thread.Join();
        _thread = null;
        lock (Gate) { _callback = null; _proc = null; }
    }

    private static nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code != Native.HC_ACTION || lParam == nint.Zero)
            return Native.CallNextHookEx(nint.Zero, code, wParam, lParam);

        var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
        var ev = TranslateEvent((uint)wParam, data);
        if (ev is null)
            return Native.CallNextHookEx(nint.Zero, code, wParam, lParam);

        var callback = _callback;
        var disposition = callback?.Invoke(ev) ?? EventDisposition.PassThrough;
        return disposition == EventDisposition.Suppress
            ? 1
            : Native.CallNextHookEx(nint.Zero, code, wParam, lParam);
    }

    /// <summary>
    /// Translate a raw <c>WH_MOUSE_LL</c> message into a <see cref="MouseEvent"/>,
    /// dropping injected input. Pure; ported from Rust <c>translate_event</c>.
    /// </summary>
    public static MouseEvent? TranslateEvent(uint wParam, Native.MSLLHOOKSTRUCT data)
    {
        if ((data.flags & Native.LLMHF_INJECTED) != 0)
            return null;

        bool? pressed = wParam switch
        {
            Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN or Native.WM_XBUTTONDOWN => true,
            Native.WM_LBUTTONUP or Native.WM_RBUTTONUP or Native.WM_MBUTTONUP or Native.WM_XBUTTONUP => false,
            _ => null,
        };
        if (pressed is { } p)
        {
            ButtonId? id = wParam switch
            {
                Native.WM_LBUTTONDOWN or Native.WM_LBUTTONUP => ButtonId.LeftClick,
                Native.WM_RBUTTONDOWN or Native.WM_RBUTTONUP => ButtonId.RightClick,
                Native.WM_MBUTTONDOWN or Native.WM_MBUTTONUP => ButtonId.MiddleClick,
                Native.WM_XBUTTONDOWN or Native.WM_XBUTTONUP => HighWord(data.mouseData) switch
                {
                    Native.XBUTTON1 => ButtonId.Back,
                    Native.XBUTTON2 => ButtonId.Forward,
                    _ => (ButtonId?)null,
                },
                _ => null,
            };
            return id is { } b ? new MouseEvent.Button(b, p) : null;
        }

        return wParam switch
        {
            Native.WM_MOUSEWHEEL => new MouseEvent.Scroll(0.0f, SignedHighWord(data.mouseData) / WheelDelta),
            Native.WM_MOUSEHWHEEL => new MouseEvent.Scroll(SignedHighWord(data.mouseData) / WheelDelta, 0.0f),
            _ => null,
        };
    }

    private static ushort HighWord(uint value) => (ushort)(value >> 16);
    private static short SignedHighWord(uint value) => (short)HighWord(value);

    /// <summary>The lower-cased executable path of the foreground process, or <c>null</c>.</summary>
    public static string? FrontmostProcessPath()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == nint.Zero) return null;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;

        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == nint.Zero) return null;
        try
        {
            var buf = new char[32768];
            uint len = (uint)buf.Length;
            if (!Native.QueryFullProcessImageNameW(process, 0, buf, ref len) || len == 0)
                return null;
            return new string(buf, 0, (int)len).ToLowerInvariant();
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }
}
