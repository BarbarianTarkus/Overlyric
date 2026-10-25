using System.Globalization;
using Overlyric.Lyrics;

namespace Overlyric.App;

/// <summary>
/// Text shared by the overlay controls and the two lyric windows, so the same offset, length or
/// timestamp is never spelled three different ways. Nothing here touches a window, which is what lets
/// the probe project check it without a display.
/// </summary>
internal static class LyricText
{
    /// <summary>
    /// The floor of the band within which two lengths count as the same performance. The ranking
    /// widens it to 0.5 % of the track on long pieces, and the selector says "matches" on exactly the
    /// same band so its words cannot contradict the pick the application made.
    /// </summary>
    public const double MatchToleranceSeconds = 2.0;

    /// <summary>The band for a particular track, matching <c>LrclibClient</c>'s ranking rule.</summary>
    public static double MatchTolerance(double? trackSeconds) =>
        trackSeconds is > 0 ? Math.Max(MatchToleranceSeconds, trackSeconds.Value * 0.005) : MatchToleranceSeconds;

    /// <summary>"0.00 s", "+0.25 s", "-1.50 s". The sign says which way the words were moved.</summary>
    public static string FormatOffset(int milliseconds) => milliseconds switch
    {
        0 => "0.00 s",
        _ => (milliseconds / 1000.0).ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + " s"
    };

    /// <summary>LRC-style timestamp for the viewer: "03:07.42".</summary>
    public static string FormatTimestamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var minutes = (int)value.TotalMinutes;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{minutes:00}:{value.Seconds:00}.{value.Milliseconds / 10:00}");
    }

    /// <summary>A record's length, or an em dash when lrclib did not send one.</summary>
    public static string FormatDuration(double? seconds)
    {
        if (seconds is not > 0)
        {
            return "\u2014";
        }

        var value = TimeSpan.FromSeconds(seconds.Value);
        return value.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalMinutes}:{value.Seconds:00}");
    }

    /// <summary>
    /// How far a record's length is from the playing track. "Matches" is the band the ranking trusts,
    /// so the selector's words agree with the choice the application would have made.
    /// </summary>
    public static string FormatDurationDelta(double? candidateSeconds, double? trackSeconds)
    {
        if (candidateSeconds is not > 0 || trackSeconds is not > 0)
        {
            return "\u2014";
        }

        var delta = candidateSeconds.Value - trackSeconds.Value;
        if (Math.Abs(delta) <= MatchTolerance(trackSeconds))
        {
            return "matches";
        }

        return delta.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + " s";
    }

    /// <summary>True when the two lengths are within the tolerance the ranking calls a match.</summary>
    public static bool DurationMatches(double? candidateSeconds, double? trackSeconds) =>
        candidateSeconds is > 0
        && trackSeconds is > 0
        && Math.Abs(candidateSeconds.Value - trackSeconds.Value) <= MatchTolerance(trackSeconds);

    /// <summary>"42 lines", "1 line", or nothing when the count is unknown.</summary>
    public static string FormatLineCount(int count) => count switch
    {
        <= 0 => "",
        1 => "1 line",
        _ => $"{count} lines"
    };

    /// <summary>A short label that cannot draw outside the box it was measured for.</summary>
    public static string Fit(string? text, int limit)
    {
        text ??= "";
        return text.Length <= limit ? text : text[..(limit - 1)] + "\u2026";
    }

    /// <summary>The last line whose start is at or before <paramref name="position"/>, or -1.</summary>
    public static int ActiveLine(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        var low = 0;
        var high = lines.Count - 1;
        var result = -1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (lines[mid].Start <= position)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }
}
