using Overlyric.Lyrics;

namespace Overlyric.App;

/// <summary>What a lyric sheet promises, in the one form the windows and the overlay agree on.</summary>
internal enum LyricBadgeKind
{
    WordSynced,
    Synced,
    Plain,
    Instrumental,
    NoLyrics,
    LookingUp,

    /// <summary>A sheet that came from a file the listener imported, not from the network.</summary>
    Local
}

/// <summary>Colour role only; the actual ARGB value belongs to the window that draws it.</summary>
internal enum LyricBadgeTone
{
    Accent,
    Good,
    Warm,
    Muted,
    Warning
}

internal readonly record struct LyricBadge(LyricBadgeKind Kind, string Label, LyricBadgeTone Tone);

/// <summary>
/// Turns the three facts a record can carry - word timings, line timings, words without timings -
/// into the badges the selector and the viewer show. Precedence follows the renderer: word timings
/// beat line timings, line timings beat plain text, and an instrumental answer outranks all three.
/// </summary>
internal static class LyricBadges
{
    public static LyricBadge WordSynced { get; } = new(LyricBadgeKind.WordSynced, "Word-synced", LyricBadgeTone.Accent);

    public static LyricBadge Synced { get; } = new(LyricBadgeKind.Synced, "Synced", LyricBadgeTone.Good);

    public static LyricBadge Plain { get; } = new(LyricBadgeKind.Plain, "Plain", LyricBadgeTone.Warm);

    public static LyricBadge Instrumental { get; } = new(LyricBadgeKind.Instrumental, "Instrumental", LyricBadgeTone.Muted);

    public static LyricBadge NoLyrics { get; } = new(LyricBadgeKind.NoLyrics, "No lyrics", LyricBadgeTone.Warning);

    public static LyricBadge LookingUp { get; } = new(LyricBadgeKind.LookingUp, "Looking up", LyricBadgeTone.Muted);

    /// <summary>The source badge for an imported sheet; the quality badge still says how it is timed.</summary>
    public static LyricBadge Local { get; } = new(LyricBadgeKind.Local, "Local", LyricBadgeTone.Accent);

    /// <summary>The best thing a candidate can promise, in one badge.</summary>
    public static LyricBadge ForCandidate(LyricsCandidate candidate)
    {
        if (candidate.IsInstrumental)
        {
            return Instrumental;
        }

        if (candidate.HasWordSync)
        {
            return WordSynced;
        }

        if (candidate.HasSynced)
        {
            return Synced;
        }

        return candidate.HasPlain ? Plain : NoLyrics;
    }

    /// <summary>The state of the sheet the overlay is following right now.</summary>
    public static LyricBadge ForSheet(bool lookupComplete, bool instrumental, bool hasLines, bool synced, bool hasWordTimings)
    {
        if (!lookupComplete)
        {
            return LookingUp;
        }

        if (instrumental)
        {
            return Instrumental;
        }

        if (!hasLines)
        {
            return NoLyrics;
        }

        if (!synced)
        {
            return Plain;
        }

        return hasWordTimings ? WordSynced : Synced;
    }
}
