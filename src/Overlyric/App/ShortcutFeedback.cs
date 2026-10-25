using System.Globalization;
using Overlyric.Overlay;

namespace Overlyric.App;

/// <summary>
/// Wording for the toast that confirms a global shortcut. The caller reads the state after the
/// command has run, so these methods only phrase the answer; the offset is the total in force, not
/// the step that was just taken.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="AppHost"/> and free of windows so the exact text, and the rule that
/// a timing nudge reports the resulting offset rather than repeating the key, can be checked without
/// a desktop. <see cref="OverlayNotice"/> is the shared shape because the same pill renderer draws
/// both this toast and the track-info notice.
/// </remarks>
internal static class ShortcutFeedback
{
    /// <summary>
    /// "On" and "visible" are not the same thing: the overlay can be enabled while nothing is
    /// playing, while the player has no lyrics, or while the pause rule is hiding it. The toast names
    /// the state the shortcut actually changed and does not claim lyrics are on screen when they are
    /// not.
    /// </summary>
    public static OverlayNotice ForOverlay(bool enabled, bool visible)
    {
        if (!enabled)
        {
            return new OverlayNotice
            {
                Title = "Overlay hidden",
                Detail = "Lyrics are out of the way"
            };
        }

        return visible
            ? new OverlayNotice
            {
                Title = "Overlay shown",
                Detail = "Lyrics are on screen"
            }
            : new OverlayNotice
            {
                Title = "Overlay on",
                Detail = "It will appear when there is a song to show"
            };
    }

    public static OverlayNotice ForLayout(bool multiLine) => new()
    {
        Title = multiLine ? "Multi-line layout" : "Single-line layout",
        Detail = multiLine ? "Several lines at a time" : "One line at a time"
    };

    /// <summary>The middle layout, kept separate so the wording tests need no theme types.</summary>
    public static OverlayNotice ForPeek() => new()
    {
        Title = "Single line + next",
        Detail = "The next line is previewed underneath"
    };

    public static OverlayNotice ForLock(bool locked) => new()
    {
        Title = locked ? "Overlay locked" : "Overlay unlocked",
        Detail = locked ? "Clicks pass through to the app below" : "Drag to move, wheel to change opacity"
    };

    /// <summary>
    /// A timing nudge reports the offset now in force - the user's example, "Offset -0.25s" - and
    /// uses the second line to say which way it moved. A press at the clamp produces no delta, which
    /// is stated as such instead of pretending the offset moved.
    /// </summary>
    public static OverlayNotice ForOffset(int offsetMs, int deltaMs, bool reset)
    {
        var detail = reset
            ? "Timing reset to zero"
            : deltaMs < 0
                ? $"Lyrics earlier by {FormatStep(-deltaMs)}"
                : deltaMs > 0
                    ? $"Lyrics later by {FormatStep(deltaMs)}"
                    : "Offset unchanged";

        return new OverlayNotice
        {
            Title = $"Offset {FormatOffset(offsetMs)}",
            Detail = detail
        };
    }

    public static OverlayNotice ForSettings() => new()
    {
        Title = "Settings",
        Detail = "Opening the settings window"
    };

    /// <summary>Signed total offset, in seconds, with the sign that says which way the lyrics moved.</summary>
    public static string FormatOffset(int offsetMs) =>
        (offsetMs / 1000.0).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "s";

    private static string FormatStep(int deltaMs) =>
        (deltaMs / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + "s";
}
