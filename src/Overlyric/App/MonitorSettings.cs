namespace Overlyric.App;

/// <summary>Which display the overlay prefers.</summary>
public enum MonitorChoice
{
    /// <summary>Whatever monitor the pointer is on when the app starts.</summary>
    Cursor = 0,

    /// <summary>The primary display, whatever its number.</summary>
    Primary = 1,

    /// <summary>A named display, remembered by its GDI device name.</summary>
    Specific = 2
}

/// <summary>Where the overlay sits on one display, in pixels from its anchor edge.</summary>
public sealed class MonitorPlacement
{
    public int X { get; set; }

    public int Y { get; set; }
}

/// <summary>The rules for the hand-editable parts of the monitor preference.</summary>
internal static class MonitorSettings
{
    /// <summary>No machine has more displays than this; a hand-edited map is trimmed to it.</summary>
    public const int MaxDisplays = 16;

    /// <summary>The same clamp the global offsets use, so the two cannot disagree.</summary>
    public const int MaxOffset = 4000;

    /// <summary>
    /// Repairs the per-monitor placement map: blank device names and null entries are dropped, the
    /// values are clamped, and the map is capped. A null map is normal for a file written before the
    /// feature existed.
    /// </summary>
    public static Dictionary<string, MonitorPlacement> Normalise(Dictionary<string, MonitorPlacement>? source)
    {
        var clean = new Dictionary<string, MonitorPlacement>(StringComparer.OrdinalIgnoreCase);

        if (source is null)
        {
            return clean;
        }

        foreach (var (device, placement) in source)
        {
            if (string.IsNullOrWhiteSpace(device) || placement is null || clean.Count >= MaxDisplays)
            {
                continue;
            }

            clean[device.Trim()] = new MonitorPlacement
            {
                X = Math.Clamp(placement.X, -MaxOffset, MaxOffset),
                Y = Math.Clamp(placement.Y, -MaxOffset, MaxOffset)
            };
        }

        return clean;
    }
}
