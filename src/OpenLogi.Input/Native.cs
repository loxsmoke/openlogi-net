using System.Runtime.InteropServices;

namespace OpenLogi.Input;

/// <summary>Win32 P/Invoke surface for the mouse hook, input injection, and pointer size.</summary>
public static class Native
{
    // ── Hook constants ───────────────────────────────────────────────────────
    public const int WH_MOUSE_LL = 14;
    public const int HC_ACTION = 0;
    public const uint LLMHF_INJECTED = 0x00000001;

    public const uint WM_QUIT = 0x0012;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    public const uint WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    public const uint WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    public const uint WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
    public const ushort XBUTTON1 = 0x0001, XBUTTON2 = 0x0002;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    public delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandleW(string? lpModuleName);

    // Win+L cannot be synthesized via SendInput (the OS reserves the lock hotkey),
    // so locking goes through this API instead.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageNameW(nint hProcess, uint dwFlags, [Out] char[] lpExeName, ref uint lpdwSize);

    // ── SendInput (injection) ────────────────────────────────────────────────
    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100;
    public const uint MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;

    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    // ── Pointer size ─────────────────────────────────────────────────────────
    // Enlarging the pointer means swapping the system cursors for scaled copies
    // (SetSystemCursor) and later asking the system to reload the user's own
    // (SPI_SETCURSORS). Writing CursorBaseSize — what the Settings slider does —
    // was tried first and does not take effect until the next sign-in.
    public const uint SPI_SETCURSORS = 0x0057;
    public const uint SPIF_SENDCHANGE = 0x0002;

    public const uint IMAGE_CURSOR = 2;
    public const uint LR_SHARED = 0x8000;
    public const uint LR_COPYFROMRESOURCE = 0x4000;
    public const uint LR_LOADFROMFILE = 0x0010;

    /// <summary>The standard system cursors (OCR_*), all scaled together so the pointer stays big whatever it is over.</summary>
    public static readonly int[] SystemCursorIds =
    [
        32512, // OCR_NORMAL (arrow)
        32513, // OCR_IBEAM
        32514, // OCR_WAIT
        32515, // OCR_CROSS
        32516, // OCR_UP
        32642, // OCR_SIZENWSE
        32643, // OCR_SIZENESW
        32644, // OCR_SIZEWE
        32645, // OCR_SIZENS
        32646, // OCR_SIZEALL
        32648, // OCR_NO
        32649, // OCR_HAND
        32650, // OCR_APPSTARTING
        32651, // OCR_HELP
    ];

    /// <summary>
    /// The <c>Control Panel\Cursors</c> value naming each OCR_* cursor's file. The
    /// value is the source the pointer can be redrawn from at any size: the stock
    /// .cur/.ani files carry native 32/48/64/96/128-pixel images, so asking for a big
    /// one is a resolution the file already has rather than a blur. An empty value
    /// means the cursor comes from a built-in resource, which has no such ladder.
    /// </summary>
    public static readonly (int Id, string Value)[] CursorSchemeValues =
    [
        (32512, "Arrow"),
        (32513, "IBeam"),
        (32514, "Wait"),
        (32515, "Crosshair"),
        (32516, "UpArrow"),
        (32642, "SizeNWSE"),
        (32643, "SizeNESW"),
        (32644, "SizeWE"),
        (32645, "SizeNS"),
        (32646, "SizeAll"),
        (32648, "No"),
        (32649, "Hand"),
        (32650, "AppStarting"),
        (32651, "Help"),
    ];

    public static readonly nint HKEY_CURRENT_USER = unchecked((nint)(long)0x80000001);
    public const string CursorsKey = @"Control Panel\Cursors";
    public const string CursorBaseSizeValue = "CursorBaseSize";

    public const uint RRF_RT_REG_DWORD = 0x00000010;
    public const uint RRF_RT_REG_SZ = 0x00000002;
    public const uint RRF_RT_REG_EXPAND_SZ = 0x00000004;
    public const int ERROR_SUCCESS = 0;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, nint pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint LoadImageW(nint hInst, nint name, uint type, int cx, int cy, uint fuLoad);

    /// <summary>
    /// <see cref="LoadImageW(nint,nint,uint,int,int,uint)"/> naming a file instead of a
    /// resource id — with <c>LR_LOADFROMFILE</c> and a size, this is what picks the
    /// closest native image out of a multi-resolution .cur/.ani.
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint LoadImageFileW(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint CopyImage(nint h, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetSystemCursor(nint hcur, int id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyCursor(nint hcur);

    // Read-only: the user's configured pointer size is the base the scaling starts from.
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegGetValueW(
        nint hkey, string lpSubKey, string lpValue, uint dwFlags, out uint pdwType, ref uint pvData, ref uint pcbData);

    /// <summary>Read-only string form, for the cursor scheme's file paths (REG_EXPAND_SZ is expanded for us).</summary>
    [DllImport("advapi32.dll", EntryPoint = "RegGetValueW", CharSet = CharSet.Unicode)]
    public static extern int RegGetValueStringW(
        nint hkey, string lpSubKey, string lpValue, uint dwFlags, out uint pdwType,
        [Out] char[]? pvData, ref uint pcbData);
}
