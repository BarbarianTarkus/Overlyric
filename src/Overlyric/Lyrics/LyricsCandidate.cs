using System;
using System.Collections.Generic;
using System.Globalization;

namespace Overlyric.Lyrics;

/// <summary>
/// One lrclib record that could serve as the lyrics for the track that is playing.
/// <para>
/// lrclib commonly holds many records for the same song - studio, live, remastered, various
/// compilations - and they differ in whether they carry timings at all. Overlyric picks the best
/// automatically, but exposes the alternatives so the listener can override the choice.
/// </para>
/// </summary>
public sealed record LyricsCandidate
{
    public required long Id { get; init; }

    public string? TrackName { get; init; }

    public string? ArtistName { get; init; }

    public string? AlbumName { get; init; }

    public double? DurationSeconds { get; init; }

    /// <summary>True when the record carries a synced body, including a word-timed lyricsfile.</summary>
    public bool HasSynced { get; init; }

    public bool HasPlain { get; init; }

    /// <summary>
    /// True when the record's <c>lyricsfile</c> carries real word-level timings, which is what the
    /// karaoke sweep follows exactly. This is the distinction the selector's badge names.
    /// </summary>
    public bool HasWordSync { get; init; }

    public bool IsInstrumental { get; init; }

    /// <summary>Lines in the synced body, or 0 when there is none.</summary>
    public int SyncedLineCount { get; init; }

    /// <summary>Lines in the plain body, or 0 when there is none.</summary>
    public int PlainLineCount { get; init; }

    /// <summary>Short human label: "11:00 · synced · A Song For All Seasons".</summary>
    public string Describe(bool includeAlbum = true)
    {
        var parts = new List<string>(3);

        if (DurationSeconds is > 0)
        {
            parts.Add(TimeSpan.FromSeconds(DurationSeconds.Value).ToString(@"m\:ss", CultureInfo.InvariantCulture));
        }

        parts.Add(IsInstrumental
            ? "instrumental"
            : HasWordSync
                ? "word-synced"
                : HasSynced
                    ? "synced"
                    : HasPlain
                        ? "plain"
                        : "no lyrics");

        if (includeAlbum && !string.IsNullOrWhiteSpace(AlbumName))
        {
            parts.Add(AlbumName!);
        }

        return string.Join(" \u00b7 ", parts);
    }
}
