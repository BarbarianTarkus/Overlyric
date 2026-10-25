using System.Diagnostics;
using System.Text;
using static FullscreenProbe.NativeMethods;

namespace FullscreenProbe;

internal sealed record ForegroundInfo(
    IntPtr Hwnd,
    uint Pid,
    string ProcessName,
    string ClassName,
    string Title,
    long Style,
    long ExStyle,
    RECT WindowRect,
    bool ExtendedFrameOk,
    RECT ExtendedFrame,
    bool Cloaked,
    RECT Monitor,
    bool CoversMonitor,
    bool IsOurProcess);

internal sealed record StateSnapshot(
    string Label,
    int NotificationState,
    bool DwmEnabled,
    ForegroundInfo Foreground,
    RECT? OverlayRect,
    bool OverlayCloaked);

/// <summary>
/// The runtime state a product can actually read: shell notification state, DWM composition,
/// the foreground window's geometry/style, and whether Desktop Duplication is available.
/// Every value is printed raw so the report can quote exact numbers.
/// </summary>
internal static class Detect
{
    public static string NotificationName(int state) => state switch
    {
        1 => "QUNS_NOT_PRESENT",
        2 => "QUNS_BUSY",
        3 => "QUNS_RUNNING_D3D_FULL_SCREEN",
        4 => "QUNS_PRESENTATION_MODE",
        5 => "QUNS_ACCEPTS_NOTIFICATIONS",
        6 => "QUNS_QUIET_TIME",
        7 => "QUNS_APP",
        _ => $"QUNS_UNKNOWN({state})"
    };

    public static StateSnapshot Snapshot(string label, IntPtr overlayHwnd, DesktopCapture? capture)
    {
        var notifyHr = SHQueryUserNotificationState(out var notification);

        var dwmHr = DwmIsCompositionEnabled(out var dwmEnabled);

        var foregroundHwnd = GetForegroundWindow();
        var foreground = Describe(foregroundHwnd);

        RECT? overlayRect = null;
        var overlayCloaked = false;
        if (overlayHwnd != IntPtr.Zero)
        {
            if (GetWindowRect(overlayHwnd, out var rect))
            {
                overlayRect = rect;
            }

            overlayCloaked = DwmGetWindowAttributeInt(overlayHwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 &&
                             cloaked != 0;
        }

        Console.WriteLine($"  [{label}]");
        Console.WriteLine($"    SHQueryUserNotificationState hr=0x{notifyHr:X8} -> {notification} ({NotificationName(notification)})");
        Console.WriteLine($"    DwmIsCompositionEnabled hr=0x{dwmHr:X8} -> {dwmEnabled}");
        Console.WriteLine($"    foreground hwnd=0x{foreground.Hwnd:X} pid={foreground.Pid} proc={foreground.ProcessName} ourProcess={foreground.IsOurProcess}");
        Console.WriteLine($"      class='{foreground.ClassName}' title='{Truncate(foreground.Title, 48)}'");
        Console.WriteLine($"      style=0x{foreground.Style:X8} (popup={(foreground.Style & WS_POPUP) != 0} caption={(foreground.Style & WS_CAPTION) != 0}) " +
                          $"exstyle=0x{foreground.ExStyle:X8} (topmost={(foreground.ExStyle & WS_EX_TOPMOST) != 0} layered={(foreground.ExStyle & WS_EX_LAYERED) != 0})");
        Console.WriteLine($"      rect={foreground.WindowRect} extFrame={(foreground.ExtendedFrameOk ? foreground.ExtendedFrame.ToString() : "unavailable")} cloaked={foreground.Cloaked}");
        Console.WriteLine($"      monitor={foreground.Monitor} coversMonitor={foreground.CoversMonitor}");
        if (overlayRect is not null)
        {
            Console.WriteLine($"      overlayRect={overlayRect} overlayCloaked={overlayCloaked}");
        }

        if (capture is not null)
        {
            Console.WriteLine($"    DesktopDuplication: available ({capture.AdapterName} / {capture.OutputName}) {capture.DuplicationDescription}");
        }
        else
        {
            try
            {
                using var fresh = DesktopCapture.Create();
                Console.WriteLine($"    DesktopDuplication: available ({fresh.AdapterName} / {fresh.OutputName}) {fresh.DuplicationDescription}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    DesktopDuplication: UNAVAILABLE {Describe(ex)}");
            }
        }

        return new StateSnapshot(label, notification, dwmEnabled, foreground, overlayRect, overlayCloaked);
    }

    public static ForegroundInfo Describe(IntPtr hwnd)
    {
        var className = new StringBuilder(256);
        var title = new StringBuilder(256);

        uint pid = 0;
        var processName = "?";
        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out pid);
            GetClassNameW(hwnd, className, className.Capacity);
            GetWindowTextW(hwnd, title, title.Capacity);
            try
            {
                processName = Process.GetProcessById((int)pid).ProcessName;
            }
            catch
            {
                processName = "?";
            }
        }

        var style = hwnd == IntPtr.Zero ? 0 : GetWindowLongPtrW(hwnd, GWL_STYLE).ToInt64();
        var exStyle = hwnd == IntPtr.Zero ? 0 : GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();

        GetWindowRect(hwnd, out var windowRect);

        var extendedOk = DwmGetWindowAttribute(
            hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT extendedFrame, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0;
        if (!extendedOk)
        {
            extendedFrame = windowRect;
        }

        var cloaked = DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out var cloakedValue, sizeof(int)) == 0 && cloakedValue != 0;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref monitorInfo);

        var coversMonitor = Math.Abs(extendedFrame.Left - monitorInfo.rcMonitor.Left) <= 1 &&
                            Math.Abs(extendedFrame.Top - monitorInfo.rcMonitor.Top) <= 1 &&
                            Math.Abs(extendedFrame.Right - monitorInfo.rcMonitor.Right) <= 1 &&
                            Math.Abs(extendedFrame.Bottom - monitorInfo.rcMonitor.Bottom) <= 1;

        var ourPid = (uint)Environment.ProcessId;

        return new ForegroundInfo(
            hwnd,
            pid,
            processName,
            className.ToString(),
            title.ToString(),
            style,
            exStyle,
            windowRect,
            extendedOk,
            extendedFrame,
            cloaked,
            monitorInfo.rcMonitor,
            coversMonitor,
            pid == ourPid);
    }

    public static string Describe(Exception ex) =>
        ex is SharpGen.Runtime.SharpGenException ? $"hr=0x{ex.HResult:X8} {ex.Message}" : $"{ex.GetType().Name}: {ex.Message}";

    private static string Truncate(string text, int length) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= length ? text : text[..length] + "...";
}
