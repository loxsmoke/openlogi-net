using System.Runtime.InteropServices;

namespace OpenLogi.Input;

/// <summary>One key transition seen by the low-level keyboard hook.</summary>
public sealed record KeyboardHookEvent(ushort VirtualKey, bool Pressed);

/// <summary>
/// A running <c>WH_KEYBOARD_LL</c> low-level keyboard hook, the keyboard twin of
/// <see cref="MouseHook"/>: a dedicated thread owns the hook and pumps its message
/// loop; <see cref="Dispose"/> posts <c>WM_QUIT</c> and joins it. A low-level hook
/// sees (and may suppress) keys before the system acts on them, which is what lets
/// the shortcut recorder capture Alt+Tab, Win+key, Esc and the like.
///
/// HARDWARE-UNVERIFIED: installing the hook + suppression behaviour needs an
/// interactive desktop session to exercise. <see cref="TranslateEvent"/> is pure
/// and unit-tested.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    // Only one keyboard hook may be installed at a time.
    private static readonly object Gate = new();
    private static Func<KeyboardHookEvent, EventDisposition>? _callback;
    private static Native.HookProc? _proc; // kept alive against GC while installed

    private Thread? _thread;
    private uint _threadId;

    private KeyboardHook() { }

    /// <summary>Install the hook and deliver events to <paramref name="callback"/> (called on the hook thread).</summary>
    public static KeyboardHook Start(Func<KeyboardHookEvent, EventDisposition> callback)
    {
        lock (Gate)
        {
            if (_callback is not null)
                throw new HookException("another keyboard hook is already installed");
            _callback = callback;
        }

        var hook = new KeyboardHook();
        using var ready = new SemaphoreSlim(0, 1);
        Exception? startError = null;

        hook._thread = new Thread(() =>
        {
            _proc = HookCallback;
            var hMod = Native.GetModuleHandleW(null);
            var handle = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _proc, hMod, 0);
            if (handle == nint.Zero)
            {
                startError = new HookException($"SetWindowsHookExW failed: {Marshal.GetLastWin32Error()}");
                ready.Release();
                return;
            }
            hook._threadId = Native.GetCurrentThreadId();
            ready.Release();

            while (Native.GetMessageW(out var msg, nint.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }

            Native.UnhookWindowsHookEx(handle);
        })
        { IsBackground = true, Name = "openlogi-keyboard-hook" };

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

        var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
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
    /// Translate a raw <c>WH_KEYBOARD_LL</c> message into a <see cref="KeyboardHookEvent"/>,
    /// dropping injected input (our own <c>SendInput</c> must never be captured). Pure.
    /// </summary>
    public static KeyboardHookEvent? TranslateEvent(uint message, Native.KBDLLHOOKSTRUCT data)
    {
        if ((data.flags & Native.LLKHF_INJECTED) != 0) return null;
        return message switch
        {
            Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN => new KeyboardHookEvent((ushort)data.vkCode, Pressed: true),
            Native.WM_KEYUP or Native.WM_SYSKEYUP => new KeyboardHookEvent((ushort)data.vkCode, Pressed: false),
            _ => null,
        };
    }
}
