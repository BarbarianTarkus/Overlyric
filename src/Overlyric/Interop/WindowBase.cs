using System.Runtime.InteropServices;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.Interop;

/// <summary>
/// Shared Win32 window plumbing. The window procedure is a static method reached through a
/// function-pointer thunk built once from <see cref="WndProcDelegate"/>. Keeping the delegate in a
/// static field roots the thunk for the lifetime of the process, and building it through
/// <see cref="System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate{TDelegate}"/>
/// keeps the interop in safe code.
/// </summary>
internal abstract class WindowBase : IDisposable
{
    private static readonly Dictionary<nint, WindowBase> Registry = [];
    private static readonly HashSet<string> RegisteredClasses = [];
    private static readonly Lock Gate = new();

    /// <summary>Roots the reverse P/Invoke thunk the window classes hand to Windows.</summary>
    private static readonly WndProcDelegate WindowProcThunk = StaticWindowProc;

    private static readonly nint WindowProcPointer =
        Marshal.GetFunctionPointerForDelegate(WindowProcThunk);

    private bool _disposed;

    public nint Handle { get; private set; }

    protected abstract string ClassName { get; }

    protected virtual int WindowStyle => WS_POPUP;

    protected virtual int WindowExtraStyle => 0;

    protected virtual IntPtr BackgroundBrush => IntPtr.Zero;

    protected void CreateWindow(string? title, int width, int height, int x = 0, int y = 0)
    {
        EnsureClassRegistered();

        Handle = CreateWindowExW(
            WindowExtraStyle,
            ClassName,
            title,
            WindowStyle,
            x,
            y,
            width,
            height,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandleW(null),
            IntPtr.Zero);

        if (Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowExW failed for '{ClassName}' (error {Marshal.GetLastWin32Error()}).");
        }

        lock (Gate)
        {
            Registry[Handle] = this;
        }

        OnCreated();
    }

    protected virtual void OnCreated()
    {
    }

    protected abstract IntPtr WindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    protected static IntPtr DefaultWindowProc(nint hWnd, uint msg, nint wParam, nint lParam) =>
        DefWindowProcW(hWnd, msg, wParam, lParam);

    /// <summary>
    /// Map a screen point onto the window's sizing border for WM_NCHITTEST.
    /// <para>
    /// These windows are frameless, so there is no non-client area for Windows to find on its own.
    /// Reporting the edges by hand is what starts the system's sizing loop - it drags, snaps and
    /// clamps against <see cref="ApplyMinimumTrackSize"/> exactly as it would for a framed window.
    /// The border is measured in DIP and rounded to pixels, so it stays a comfortable target at
    /// 200 % scaling.
    /// </para>
    /// </summary>
    protected static IntPtr ResizeFrameHitTest(nint hWnd, nint lParam, float dpi, float borderDip)
    {
        var raw = lParam.ToInt64();
        var point = new POINT
        {
            X = (short)(raw & 0xFFFF),
            Y = (short)((raw >> 16) & 0xFFFF)
        };

        if (!ScreenToClient(hWnd, ref point) || !GetClientRect(hWnd, out var client))
        {
            return new IntPtr(HTCLIENT);
        }

        if (point.X < 0 || point.Y < 0 || point.X >= client.Width || point.Y >= client.Height)
        {
            return new IntPtr(HTCLIENT);
        }

        var border = Math.Max(1, (int)MathF.Round(borderDip * dpi / 96f));
        var left = point.X < border;
        var right = point.X >= client.Width - border;
        var top = point.Y < border;
        var bottom = point.Y >= client.Height - border;

        var code = (left, right, top, bottom) switch
        {
            (true, _, true, _) => HTTOPLEFT,
            (_, true, true, _) => HTTOPRIGHT,
            (true, _, _, true) => HTBOTTOMLEFT,
            (_, true, _, true) => HTBOTTOMRIGHT,
            (true, _, _, _) => HTLEFT,
            (_, true, _, _) => HTRIGHT,
            (_, _, true, _) => HTTOP,
            (_, _, _, true) => HTBOTTOM,
            _ => HTCLIENT
        };

        return new IntPtr(code);
    }

    /// <summary>
    /// Hand the native sizing loop a minimum size. WM_GETMINMAXINFO carries physical pixels, while
    /// every layout in the application is written in DIP, so the conversion happens here once.
    /// </summary>
    protected static void ApplyMinimumTrackSize(nint lParam, float dpi, float widthDip, float heightDip)
    {
        if (lParam == nint.Zero)
        {
            return;
        }

        var scale = dpi / 96f;
        var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        info.ptMinTrackSize = new POINT
        {
            X = (int)MathF.Round(widthDip * scale),
            Y = (int)MathF.Round(heightDip * scale)
        };

        Marshal.StructureToPtr(info, lParam, false);
    }

    private void EnsureClassRegistered()
    {
        lock (Gate)
        {
            if (!RegisteredClasses.Add(ClassName))
            {
                return;
            }

            // RegisterClassExW copies the name into the atom table, so the native copy only has to
            // outlive the call. The structure carries pointers to stay blittable for LibraryImport.
            var className = Marshal.StringToHGlobalUni(ClassName);
            try
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                    style = 0,
                    lpfnWndProc = WindowProcPointer,
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = GetModuleHandleW(null),
                    // The taskbar, Alt+Tab and the window menu all fall back to these class icons;
                    // without them those surfaces show the stock application icon.
                    hIcon = AppIcon.Large,
                    hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512)),
                    hbrBackground = BackgroundBrush,
                    lpszMenuName = IntPtr.Zero,
                    lpszClassName = className,
                    hIconSm = AppIcon.Small
                };

                if (RegisterClassExW(ref windowClass) == 0)
                {
                    RegisteredClasses.Remove(ClassName);
                    throw new InvalidOperationException(
                        $"RegisterClassExW failed for '{ClassName}' (error {Marshal.GetLastWin32Error()}).");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(className);
            }
        }
    }

    private static IntPtr StaticWindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        WindowBase? target;
        lock (Gate)
        {
            Registry.TryGetValue(hWnd, out target);
        }

        if (target is null)
        {
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        try
        {
            return target.WindowProc(hWnd, msg, wParam, lParam);
        }
        catch
        {
            // A throwing window procedure would take the process down; fail safe instead.
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    protected static void Unregister(nint handle)
    {
        lock (Gate)
        {
            Registry.Remove(handle);
        }
    }

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (Handle != IntPtr.Zero)
        {
            Unregister(Handle);
            DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
