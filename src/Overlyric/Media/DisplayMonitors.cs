using System.Runtime.InteropServices;
using Overlyric.App;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.Media;

/// <summary>One connected display, as the placement setting needs to see it.</summary>
internal sealed record DisplayMonitor(nint Handle, string DeviceName, string Label, bool Primary)
{
    public override string ToString() => Label;
}

/// <summary>
/// Enumerates the connected displays and translates the monitor choice into an HMONITOR.
/// </summary>
/// <remarks>
/// <c>MONITORINFOEXW.szDevice</c> is the stable identity to remember: display numbers are reassigned
/// when a monitor is unplugged, so "Display 2" is not a fact about the hardware. The friendly label
/// comes from <c>EnumDisplayDevicesW</c> for the settings row; the device name stays the key.
/// </remarks>
internal static class DisplayMonitors
{
    /// <summary>Every monitor, primary first, then in the order Windows enumerates them.</summary>
    public static IReadOnlyList<DisplayMonitor> Enumerate()
    {
        var monitors = new List<DisplayMonitor>();
        var state = GCHandle.Alloc(monitors);

        try
        {
            EnumDisplayMonitors(nint.Zero, nint.Zero, OnMonitor, GCHandle.ToIntPtr(state));
        }
        catch (Exception)
        {
            // A display change mid-enumeration is not worth failing startup for.
        }
        finally
        {
            state.Free();
        }

        monitors.Sort(static (a, b) => a.Primary == b.Primary ? 0 : a.Primary ? -1 : 1);
        return monitors;
    }

    /// <summary>The device name for an HMONITOR, or empty when it is not a current display.</summary>
    public static string DeviceNameFor(nint monitor)
    {
        if (monitor == nint.Zero)
        {
            return string.Empty;
        }

        var info = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };

        return GetMonitorInfoExW(monitor, ref info) ? ReadDeviceName(ref info) : string.Empty;
    }

    /// <summary>Finds a connected display by its device name, case-insensitively.</summary>
    public static DisplayMonitor? Find(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        foreach (var monitor in Enumerate())
        {
            if (string.Equals(monitor.DeviceName, deviceName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return monitor;
            }
        }

        return null;
    }

    public static DisplayMonitor? Primary()
    {
        foreach (var monitor in Enumerate())
        {
            if (monitor.Primary)
            {
                return monitor;
            }
        }

        return null;
    }

    private static bool OnMonitor(nint monitor, nint hdc, nint rect, nint data)
    {
        try
        {
            var info = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };

            if (!GetMonitorInfoExW(monitor, ref info))
            {
                return true;
            }

            var name = ReadDeviceName(ref info);
            var primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
            var label = FriendlyName(name);

            if (GCHandle.FromIntPtr(data).Target is List<DisplayMonitor> list)
            {
                list.Add(new DisplayMonitor(monitor, name, label, primary));
            }
        }
        catch (Exception)
        {
            // Skip a monitor that disappeared between enumeration and inspection.
        }

        return true;
    }

    private static unsafe string ReadDeviceName(ref MONITORINFOEXW info)
    {
        fixed (char* name = info.szDevice)
        {
            return new string(name);
        }
    }

    private static unsafe string FriendlyName(string deviceName)
    {
        var namePointer = Marshal.StringToHGlobalUni(deviceName);

        try
        {
            var device = new DISPLAY_DEVICEW { cb = sizeof(DISPLAY_DEVICEW) };

            if (EnumDisplayDevicesW(namePointer, 0, ref device, 0))
            {
                var label = new string(device.DeviceString);

                if (!string.IsNullOrWhiteSpace(label))
                {
                    return label;
                }
            }
        }
        catch (Exception)
        {
            // Fall back to the device name; the setting still works.
        }
        finally
        {
            Marshal.FreeHGlobal(namePointer);
        }

        return deviceName;
    }
}
