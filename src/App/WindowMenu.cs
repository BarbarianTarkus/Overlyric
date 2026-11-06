using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Overlyric.App;

/// <summary>The four windows the launcher and the title bar can move between, in menu order.</summary>
internal enum WindowDestination
{
    Lyrics = 0,
    Versions = 1,
    Diagnostics = 2,
    Settings = 3
}

/// <summary>
/// The host's answer to "open that window" and "is it already on screen". Every panel window and
/// the settings window hold the same instance, which is what keeps the launcher and the four title
/// bars from disagreeing about which windows exist.
/// </summary>
internal sealed class WindowNavigation
{
    public WindowNavigation(Action<WindowDestination> open, Func<WindowDestination, bool> isOpen)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(isOpen);

        Open = open;
        IsOpen = isOpen;
    }

    public Action<WindowDestination> Open { get; }

    public Func<WindowDestination, bool> IsOpen { get; }
}

/// <summary>
/// The Window menu drawn into the 44 DIP title bar every window shares: the app mark and the four
/// destinations, in one fixed order.
/// <para>
/// This class owns the geometry and the state colours, not the drawing primitives. Each window
/// already has its own brush and text-format caches, so the caller passes three small delegates and
/// the menu stays in one place - the layout, the hit test and the colours cannot drift between the
/// settings window, the three panels and the launcher.
/// </para>
/// </summary>
internal static class WindowMenu
{
    public const float BarHeight = 44f;

    private const float MarkLeft = 16f;
    private const float MarkTop = 12f;
    private const float MarkSize = 20f;

    private const float TabTop = 9f;
    private const float TabHeight = 26f;
    private const float TabPadding = 14f;
    private const float LabelTop = 14.7f;
    private const float LabelHeight = 16f;
    private const float LabelSize = 12.5f;

    private const uint ColorMarkBorder = 0xFF26313F;
    private const uint ColorMarkFill = 0xFF18212B;
    private const uint ColorAccent = 0xFF7CC4FF;
    private const uint ColorCurrentFill = 0xFF23303F;
    private const uint ColorCurrentText = 0xFFE9EDF5;
    private const uint ColorOpenRail = 0xFF46566C;
    private const uint ColorOpenText = 0xFF8A93A6;
    private const uint ColorClosedText = 0xFF6E7788;
    private const uint ColorHoverFill = 0xFF232A35;
    private const uint ColorHoverText = 0xFFC9D1DF;

    private static readonly (WindowDestination Destination, string Label, string Hint, float Left, float Width)[] Tabs =
    [
        (WindowDestination.Lyrics, "Lyrics", "Full sheet for the current track", 46f, 68f),
        (WindowDestination.Versions, "Versions", "Pick another match from LRCLIB", 118f, 86f),
        (WindowDestination.Diagnostics, "Diagnostics", "What Overlyric sees right now", 208f, 108f),
        (WindowDestination.Settings, "Settings", "Appearance, timing, behaviour", 320f, 80f)
    ];

    /// <summary>The destinations in the only order the UI ever shows them; the launcher iterates this.</summary>
    public static IReadOnlyList<WindowDestination> Destinations { get; } = [.. Tabs.Select(tab => tab.Destination)];

    public static string Label(WindowDestination destination) => Tabs[(int)destination].Label;

    public static string Hint(WindowDestination destination) => Tabs[(int)destination].Hint;

    /// <summary>True when the point is on one of the four destinations (not the mark, not close).</summary>
    public static bool TryHit(float x, float y, out WindowDestination destination)
    {
        foreach (var tab in Tabs)
        {
            if (x >= tab.Left && x < tab.Left + tab.Width && y >= TabTop && y < TabTop + TabHeight)
            {
                destination = tab.Destination;
                return true;
            }
        }

        destination = WindowDestination.Lyrics;
        return false;
    }

    /// <summary>Draw the mark and the four tabs; the caller draws its own close control afterwards.</summary>
    public static void Draw(
        WindowDestination current,
        Func<WindowDestination, bool>? isOpen,
        WindowDestination? hover,
        Action<float, float, float, float, float, uint> fillRound,
        Action<string, float, float, float, float, float, FontWeight, uint, TextAlignment> label)
    {
        // The mark is the same three bars as the tray icon; a 1 DIP border is drawn as two rounded
        // rectangles because the windows only expose a fill helper.
        fillRound(MarkLeft, MarkTop, MarkSize, MarkSize, 6f, ColorMarkBorder);
        fillRound(MarkLeft + 1f, MarkTop + 1f, MarkSize - 2f, MarkSize - 2f, 5f, ColorMarkFill);

        fillRound(21.5f, 18f, 2f, 8f, 1f, ColorAccent);
        fillRound(25.5f, 16f, 2f, 12f, 1f, ColorAccent);
        fillRound(29.5f, 17.5f, 2f, 9f, 1f, ColorAccent);

        foreach (var tab in Tabs)
        {
            var selected = tab.Destination == current;
            var open = isOpen?.Invoke(tab.Destination) ?? false;
            var hot = hover == tab.Destination && !selected;

            if (selected)
            {
                fillRound(tab.Left, TabTop, tab.Width, TabHeight, 6f, ColorCurrentFill);
                fillRound(tab.Left, BarHeight - 2f, tab.Width, 2f, 0f, ColorAccent);
            }
            else
            {
                if (hot)
                {
                    fillRound(tab.Left, TabTop, tab.Width, TabHeight, 6f, ColorHoverFill);
                }

                if (open)
                {
                    fillRound(tab.Left, BarHeight - 2f, tab.Width, 2f, 0f, ColorOpenRail);
                }
            }

            var color = selected ? ColorCurrentText : open ? ColorOpenText : ColorClosedText;

            if (hot)
            {
                color = ColorHoverText;
            }

            label(
                tab.Label,
                tab.Left + TabPadding,
                LabelTop,
                tab.Width - (2 * TabPadding),
                LabelHeight,
                LabelSize,
                FontWeight.SemiBold,
                color,
                TextAlignment.Leading);
        }
    }
}