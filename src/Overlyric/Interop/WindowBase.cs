using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.Interop;

/// <summary>
/// Shared Win32 window plumbing. The window procedure is a <c>[UnmanagedCallersOnly]</c> static
/// method reached through a function pointer, which is the NativeAOT-friendly alternative to
/// marshalling a managed delegate.
/// </summary>
internal abstract class WindowBase : IDisposable
{
    private static readonly Dictionary<nint, WindowBase> Registry = [];
    private static readonly HashSet<string> RegisteredClasses = [];
    private static readonly Lock Gate = new();

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

    private void EnsureClassRegistered()
    {
        lock (Gate)
        {
            if (!RegisteredClasses.Add(ClassName))
            {
                return;
            }

            unsafe
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                    style = 0,
                    lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&StaticWindowProc,
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = GetModuleHandleW(null),
                    hIcon = IntPtr.Zero,
                    hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512)),
                    hbrBackground = BackgroundBrush,
                    lpszMenuName = null,
                    lpszClassName = ClassName,
                    hIconSm = IntPtr.Zero
                };

                if (RegisterClassExW(ref windowClass) == 0)
                {
                    RegisteredClasses.Remove(ClassName);
                    throw new InvalidOperationException(
                        $"RegisterClassExW failed for '{ClassName}' (error {Marshal.GetLastWin32Error()}).");
                }
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
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
