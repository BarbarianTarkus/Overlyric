using System.Runtime.InteropServices;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.Media;

/// <summary>
/// The foreground-process and fullscreen facts the Presence rules need. Every call fails soft: a
/// protected process, a vanishing window or a shell that does not answer means "unknown", and an
/// unknown answer never hides the overlay or raises a notice.
/// </summary>
internal static class Foreground
{
    /// <summary>The foreground window, or zero when Windows is between windows.</summary>
    public static nint Handle() => GetForegroundWindow();

    /// <summary>The full image path of the foreground process, or null when it cannot be read.</summary>
    public static unsafe string? ProcessPath()
    {
        var handle = GetForegroundWindow();
        if (handle == nint.Zero)
        {
            return null;
        }

        GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return null;
        }

        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == nint.Zero)
        {
            // A protected or elevated process: unknown, and unknown must not hide anything.
            return null;
        }

        try
        {
            const int capacity = 1024;
            var buffer = stackalloc char[capacity];
            var size = (uint)capacity;

            return QueryFullProcessImageNameW(process, 0, buffer, ref size)
                ? new string(buffer, 0, (int)size)
                : null;
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    /// <summary>
    /// Whether the window belongs to the shell rather than to a program the listener is using. The
    /// desktop and its worker windows are always full-screen; counting them as a game would raise a
    /// notice on every desktop click.
    /// </summary>
    public static unsafe bool IsShellWindow(nint window)
    {
        if (window == nint.Zero)
        {
            return false;
        }

        const int capacity = 64;
        var buffer = stackalloc char[capacity];
        var length = GetClassNameW(window, buffer, capacity);

        if (length <= 0)
        {
            return false;
        }

        var name = new string(buffer, 0, length);

        return name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>SHQueryUserNotificationState, or 0 when the shell cannot answer.</summary>
    public static int NotificationState() =>
        SHQueryUserNotificationState(out var state) >= 0 ? state : 0;

    /// <summary>The monitor a window is on, or zero.</summary>
    public static nint MonitorOf(nint window) =>
        window == nint.Zero ? nint.Zero : MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);

    /// <summary>
    /// Whether the window covers its monitor's full bounds. The extended frame bounds are used
    /// because <see cref="GetWindowRect"/> includes invisible resize borders, which would make a
    /// maximised window look smaller than the screen.
    /// </summary>
    public static bool CoversMonitor(nint window, nint monitor)
    {
        if (window == nint.Zero || monitor == nint.Zero)
        {
            return false;
        }

        var info = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };

        if (!GetMonitorInfoExW(monitor, ref info))
        {
            return false;
        }

        var bounds = new RECT();

        if (DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out bounds, Marshal.SizeOf<RECT>()) < 0
            && !GetWindowRect(window, out bounds))
        {
            return false;
        }

        return bounds.Left <= info.rcMonitor.Left
               && bounds.Top <= info.rcMonitor.Top
               && bounds.Right >= info.rcMonitor.Right
               && bounds.Bottom >= info.rcMonitor.Bottom;
    }
}
