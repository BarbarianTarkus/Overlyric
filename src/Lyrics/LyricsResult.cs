using System;
using System.Collections.Generic;

namespace Overlyric.Lyrics;

/// <summary>Which source supplied a resolved sheet.</summary>
public enum LyricSource
{
    /// <summary>An automatic LRCLIB answer, including one served from the cache.</summary>
    Lrclib = 0,

    /// <summary>A file the listener imported; it outranks the network sources.</summary>
    Local = 1,

    /// <summary>An LRCLIB record the listener picked by hand.</summary>
    Pinned = 2
}

/// <summary>
/// The outcome of a lyrics lookup.
/// </summary>
public sealed class LyricsResult
{
    /// <summary>
    /// Lyric lines sorted ascending by <see cref="LyricLine.Start"/>.
    /// Empty when the track is instrumental or when lrclib has no lyrics for it.
    /// </summary>
    public IReadOnlyList<LyricLine> Lines { get; init; } = Array.Empty<LyricLine>();

    /// <summary>True when <see cref="Lines"/> carry real timestamps (LRC source).</summary>
    public bool IsSynced { get; init; }

    /// <summary>
    /// True when there is nothing to sing. Set either because lrclib flags the record as
    /// instrumental, or because its lyric body turned out to be nothing but placeholder text such as
    /// <c>[Instrumental]</c> - see <see cref="InstrumentalDetector"/>.
    /// </summary>
    public bool IsInstrumental { get; init; }

    /// <summary>Unsynchronised lyrics exactly as returned by lrclib, when available.</summary>
    public string? PlainLyrics { get; init; }

    /// <summary>lrclib record id, when the response carried one.</summary>
    public long? LrclibId { get; init; }

    /// <summary>Where this answer came from; the windows badge a local sheet differently.</summary>
    public LyricSource Source { get; init; }

    public string? TrackName { get; init; }

    public string? ArtistName { get; init; }

    public double? DurationSeconds { get; init; }

    /// <summary>
    /// True when at least one line carries word-level timings, which is what the karaoke sweep
    /// follows exactly instead of estimating.
    /// </summary>
    public bool HasWordTimings
    {
        get
        {
            foreach (var line in Lines)
            {
                if (line.HasWordTimings)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>A copy tagged with the source that supplied it; the cache itself stores no source.</summary>
    public LyricsResult WithSource(LyricSource source) => new()
    {
        Lines = Lines,
        IsSynced = IsSynced,
        IsInstrumental = IsInstrumental,
        PlainLyrics = PlainLyrics,
        LrclibId = LrclibId,
        TrackName = TrackName,
        ArtistName = ArtistName,
        DurationSeconds = DurationSeconds,
        Source = source
    };
}
