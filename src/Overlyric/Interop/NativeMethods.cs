using System.Runtime.InteropServices;

namespace Overlyric.Interop;

/// <summary>
/// Minimal, hand-picked Win32 surface. Only the entry points Overlyric actually
/// uses, so the NativeAOT image stays small and there is no wrapper dependency.
/// </summary>
internal static partial class NativeMethods
{
    internal const int GWL_EXSTYLE = -20;
    internal const int GWLP_USERDATA = -21;

    internal const int WS_EX_TOPMOST = 0x00000008;
    internal const int WS_EX_TRANSPARENT = 0x00000020;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_APPWINDOW = 0x00040000;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    internal const int WS_EX_LAYERED = 0x00080000;

    internal const int WS_POPUP = unchecked((int)0x80000000);
    internal const int WS_VISIBLE = 0x10000000;
    internal const int WS_CLIPSIBLINGS = 0x04000000;
    internal const int WS_CAPTION = 0x00C00000;
    internal const int WS_SYSMENU = 0x00080000;
    internal const int WS_MINIMIZEBOX = 0x00020000;

    internal const int WM_DESTROY = 0x0002;
    internal const int WM_SIZE = 0x0005;
    internal const int WM_CLOSE = 0x0010;
    internal const int WM_PAINT = 0x000F;
    internal const int WM_GETMINMAXINFO = 0x0024;
    internal const int WM_ERASEBKGND = 0x0014;
    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_CHAR = 0x0102;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int WM_MOUSELEAVE = 0x02A3;
    internal const int WM_TIMER = 0x0113;
    internal const int WM_HOTKEY = 0x0312;
    internal const int WM_NCHITTEST = 0x0084;
    internal const int WM_LBUTTONDOWN = 0x0201;
    internal const int WM_LBUTTONUP = 0x0202;
    internal const int WM_MOUSEMOVE = 0x0200;
    internal const int WM_MOUSEWHEEL = 0x020A;
    internal const int WM_DISPLAYCHANGE = 0x007E;
    internal const int WM_DPICHANGED = 0x02E0;
    internal const int WM_SETTINGCHANGE = 0x001A;
    internal const int WM_APP = 0x8000;
    internal const int WM_RBUTTONUP = 0x0205;
    internal const int WM_CONTEXTMENU = 0x007B;

    internal const uint CF_UNICODETEXT = 13;

    internal const int HTTRANSPARENT = -1;
    internal const int HTCLIENT = 1;
    internal const int HTCAPTION = 2;
    internal const int HTLEFT = 10;
    internal const int HTRIGHT = 11;
    internal const int HTTOP = 12;
    internal const int HTTOPLEFT = 13;
    internal const int HTTOPRIGHT = 14;
    internal const int HTBOTTOM = 15;
    internal const int HTBOTTOMLEFT = 16;
    internal const int HTBOTTOMRIGHT = 17;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;
    internal const int SW_SHOWNA = 8;

    internal const int VK_ESCAPE = 0x1B;
    internal const int VK_TAB = 0x09;
    internal const int VK_RETURN = 0x0D;
    internal const int VK_SPACE = 0x20;
    internal const int VK_PRIOR = 0x21;
    internal const int VK_NEXT = 0x22;
    internal const int VK_END = 0x23;
    internal const int VK_HOME = 0x24;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_SHIFT = 0x10;
    internal const int VK_MENU = 0x12;
    internal const int VK_LWIN = 0x5B;
    internal const int VK_RWIN = 0x5C;

    internal const uint TME_LEAVE = 0x00000002;

    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE; 19 on Windows 10 builds before 20H1.</summary>
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;
    internal const int SM_CXICON = 11;
    internal const int SM_CYICON = 12;
    internal const int SM_CXSMICON = 49;
    internal const int SM_CYSMICON = 50;
    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;

    internal const int MONITOR_DEFAULTTONEAREST = 2;

    internal const uint NIF_MESSAGE = 0x00000001;
    internal const uint NIF_ICON = 0x00000002;
    internal const uint NIF_TIP = 0x00000004;
    internal const uint NIF_INFO = 0x00000010;
    internal const uint NIF_SHOWTIP = 0x00000080;
    internal const uint NIIF_INFO = 0x00000001;
    internal const uint NIM_ADD = 0x00000000;
    internal const uint NIM_MODIFY = 0x00000001;
    internal const uint NIM_DELETE = 0x00000002;
    internal const uint NIM_SETVERSION = 0x00000004;
    internal const uint NOTIFYICON_VERSION_4 = 4;

    internal const uint LWA_ALPHA = 0x00000002;

    internal const uint MF_STRING = 0x00000000;
    internal const uint MF_SEPARATOR = 0x00000800;
    internal const uint MF_CHECKED = 0x00000008;
    internal const uint MF_UNCHECKED = 0x00000000;
    internal const uint MF_POPUP = 0x00000010;
    internal const uint MF_GRAYED = 0x00000001;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint TPM_NONOTIFY = 0x0080;

    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;
    internal const uint MOD_NOREPEAT = 0x4000;

    internal const int SPI_GETWORKAREA = 0x0030;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    /// <summary>
    /// The sizing limits a window reports for WM_GETMINMAXINFO. Only the minimum tracking size is
    /// written; the remaining fields are left exactly as Windows supplied them.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNDCLASSEXW
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;

        /// <summary>LPWSTR, or null. Held as a pointer so the struct stays blittable.</summary>
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    /// <summary>
    /// Sized to the native PAINTSTRUCT (68 bytes of payload, 72 with padding on x64).
    /// The reserved tail is never read, so an opaque block of the right size is enough.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 72)]
    internal struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustWindowRectEx(
        ref RECT lpRect, uint dwStyle, [MarshalAs(UnmanagedType.Bool)] bool bMenu, uint dwExStyle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr SetCapture(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReleaseCapture();

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateSolidBrush(uint crColor);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string? lpWindowName,
        int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial void PostQuitMessage(int nExitCode);

    [LibraryImport("user32.dll")]
    internal static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr DispatchMessageW(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial ulong SetTimer(IntPtr hWnd, ulong nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool KillTimer(IntPtr hWnd, ulong uIDEvent);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr SetCursor(IntPtr hCursor);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial short GetKeyState(int nVirtKey);

    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr GetClipboardData(uint uFormat);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDpiAwarenessContext(IntPtr value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(IntPtr hMenu);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int TrackPopupMenu(
        IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// Shell_NotifyIcon, with its structure marshalled by hand. <see cref="NOTIFYICONDATAW"/> holds
    /// fixed-size <c>WCHAR</c> fields, which the <see cref="LibraryImportAttribute"/> source generator
    /// cannot marshal, so the entry point itself takes a pointer and the copy happens here once rather
    /// than at every call site.
    /// </summary>
    internal static bool ShellNotifyIcon(uint message, in NOTIFYICONDATAW data)
    {
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NOTIFYICONDATAW>());
        try
        {
            Marshal.StructureToPtr(data, buffer, fDeleteOld: false);
            return Shell_NotifyIconW(message, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("shell32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint dwMessage, IntPtr lpData);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    internal const uint IMAGE_ICON = 1;

    /// <summary>LR_DEFAULTCOLOR: no flags. Without LR_SHARED the icon belongs to the caller.</summary>
    internal const uint LR_DEFAULTCOLOR = 0x00000000;

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr LoadImageW(
        IntPtr hInstance, IntPtr lpName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    /// <summary>CLSCTX_INPROC_SERVER | CLSCTX_LOCAL_SERVER | CLSCTX_REMOTE_SERVER.</summary>
    internal const uint CLSCTX_ALL = 0x17;

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(
        in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    /// <summary>STGM_READ: a property store opened read-only.</summary>
    internal const uint STGM_READ = 0;

    /// <summary>VT_LPWSTR: the PROPVARIANT holds a null-terminated Unicode string.</summary>
    internal const ushort VT_LPWSTR = 31;

    /// <summary>A PROPERTYKEY: the property-set GUID plus the property id inside it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;

        public int PropertyId;

        public PropertyKey(Guid formatId, int propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    /// <summary>
    /// The native PROPVARIANT shape (24 bytes on x64): a type word, three reserved words, and the
    /// pointer-sized value. Only VT_LPWSTR is read; the other variants are cleared and ignored.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public IntPtr Padding;
    }

    [LibraryImport("ole32.dll")]
    internal static partial int PropVariantClear(ref PropVariant value);

    [LibraryImport("ole32.dll")]
    internal static partial void CoTaskMemFree(IntPtr memory);

    /// <summary>The native OPENFILENAMEW, in the shape comdlg32 writes back into.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct OpenFileName
    {
        public uint StructSize;
        public nint Owner;
        public nint Instance;
        public nint Filter;
        public nint CustomFilter;
        public uint MaxCustomFilter;
        public uint FilterIndex;
        public nint File;
        public uint MaxFile;
        public nint FileTitle;
        public uint MaxFileTitle;
        public nint InitialDirectory;
        public nint Title;
        public uint Flags;
        public ushort FileOffset;
        public ushort FileExtension;
        public nint DefaultExtension;
        public nint CustomData;
        public nint Hook;
        public nint TemplateName;
        public nint ReservedPointer;
        public uint ReservedValue;
        public uint FlagsEx;
    }

    [LibraryImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetOpenFileNameW(ref OpenFileName dialog);

    [LibraryImport("comdlg32.dll", EntryPoint = "GetSaveFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSaveFileNameW(ref OpenFileName dialog);
}
