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
/// <para>
/// <c>MONITORINFOEXW.szDevice</c> is the stable identity to remember: display numbers are reassigned
/// when a monitor is unplugged, so "Display 2" is not a fact about the hardware. The friendly label
/// comes from <c>EnumDisplayDevicesW</c> for the settings row; the device name stays the key.
/// </para>
/// <para>
/// The monitor structs contain fixed-size character buffers, which the LibraryImport source
/// generator cannot marshal and which would otherwise force unsafe code. They are therefore read by
/// offset out of a caller-owned block; the offsets are the documented Windows layout and are
/// commented where they are used.
/// </para>
/// </remarks>
internal static class DisplayMonitors
{
    /// <summary>MONITORINFOEXW on x64: cbSize 4, rcMonitor 16, rcWork 16, dwFlags 4, szDevice 64.</summary>
    private const int MonitorInfoSize = 168;

    private const int MonitorRectOffset = 4;
    private const int MonitorWorkOffset = 20;
    private const int MonitorFlagsOffset = 36;
    private const int MonitorDeviceOffset = 40;

    /// <summary>DISPLAY_DEVICEW on x64: cb 4, DeviceName 64, DeviceString 256, StateFlags 4, ...</summary>
    private const int DisplayDeviceSize = 840;

    private const int DisplayDeviceStringOffset = 68;

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

        var buffer = Marshal.AllocHGlobal(MonitorInfoSize);

        try
        {
            Marshal.WriteInt32(buffer, MonitorInfoSize);

            return GetMonitorInfoPtr(monitor, buffer)
                ? ReadFixedString(buffer, MonitorDeviceOffset, CCHDEVICENAME)
                : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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
        var buffer = Marshal.AllocHGlobal(MonitorInfoSize);

        try
        {
            Marshal.WriteInt32(buffer, MonitorInfoSize);

            if (!GetMonitorInfoPtr(monitor, buffer))
            {
                return true;
            }

            var name = ReadFixedString(buffer, MonitorDeviceOffset, CCHDEVICENAME);
            var flags = Marshal.ReadInt32(buffer, MonitorFlagsOffset);
            var primary = (flags & MONITORINFOF_PRIMARY) != 0;
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
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return true;
    }

    private static string FriendlyName(string deviceName)
    {
        var namePointer = Marshal.StringToHGlobalUni(deviceName);
        var device = Marshal.AllocHGlobal(DisplayDeviceSize);

        try
        {
            Marshal.WriteInt32(device, DisplayDeviceSize);

            if (EnumDisplayDevicesPtr(namePointer, 0, device, 0))
            {
                var label = ReadFixedString(device, DisplayDeviceStringOffset, 128);

                if (label.Length > 0)
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
            Marshal.FreeHGlobal(device);
        }

        return deviceName;
    }

    /// <summary>Reads a null-terminated UTF-16 buffer inside a caller-owned block.</summary>
    private static string ReadFixedString(nint block, int offset, int capacity)
    {
        var pointer = block + offset;

        for (var i = 0; i < capacity; i++)
        {
            if (Marshal.ReadInt16(pointer, i * sizeof(char)) == 0)
            {
                return i == 0 ? string.Empty : Marshal.PtrToStringUni(pointer, i)!;
            }
        }

        return Marshal.PtrToStringUni(pointer, capacity)!;
    }
}
