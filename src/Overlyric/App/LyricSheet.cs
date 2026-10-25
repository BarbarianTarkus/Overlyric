using Overlyric.Lyrics;

namespace Overlyric.App;

/// <summary>
/// Everything the complete-lyrics window needs, captured from the host on the UI thread so the window
/// never reaches back into playback state while it is drawing.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately a document rather than a list of strings: the viewer already needs the line
/// timings to show them and to find the line being sung, and editing and uploading to LRCLIB are the
/// next things it is meant to grow. Keeping the parsed lines and the record identity in one immutable
/// value is the seam those features will use.
/// </para>
/// </remarks>
internal sealed record LyricSheet
{
    /// <summary>Changes when the words, the chosen record or the track change; used to keep scroll position.</summary>
    public string Key { get; init; } = "";

    public string Title { get; init; } = "";

    public string Artist { get; init; } = "";

    public string? Album { get; init; }

    public string? SourceApp { get; init; }

    public double? DurationSeconds { get; init; }

    public IReadOnlyList<LyricLine> Lines { get; init; } = [];

    public bool IsSynced { get; init; }

    public bool HasWordTimings { get; init; }

    public bool IsInstrumental { get; init; }

    /// <summary>False while a lookup is still running, which is different from "found nothing".</summary>
    public bool IsComplete { get; init; } = true;

    /// <summary>The host's own status sentence, for example "No lyrics found".</summary>
    public string? Status { get; init; }

    public long? RecordId { get; init; }

    /// <summary>Playback position when the snapshot was taken, so the viewer can open on the current line.</summary>
    public TimeSpan Position { get; init; }

    public bool HasTrack => Title.Length > 0 || Artist.Length > 0;

    public int LineCount => Lines.Count;
}

/// <summary>
/// The selector's view of the track, its current sheet and every lrclib record that could replace it.
/// Pulled fresh on every draw: the picker stays open while the lookup finishes, the track changes, or
/// the listener picks another version.
/// </summary>
internal sealed record LyricPickerModel
{
    public bool HasTrack { get; init; }

    /// <summary>Demo mode has canned lyrics and deliberately makes no lrclib request.</summary>
    public bool IsDemo { get; init; }

    public string Title { get; init; } = "";

    public string Artist { get; init; } = "";

    public string? Album { get; init; }

    public string? SourceApp { get; init; }

    public double? DurationSeconds { get; init; }

    public long Revision { get; init; }

    public IReadOnlyList<LyricsCandidate> Versions { get; init; } = [];

    /// <summary>The record chosen by hand, or null when the automatic pick is in force.</summary>
    public long? PinnedId { get; init; }

    public bool Loading { get; init; }

    public bool LookupComplete { get; init; }

    public bool IsInstrumental { get; init; }

    public bool IsSynced { get; init; }

    public bool HasWordTimings { get; init; }

    public int LineCount { get; init; }

    public long? CurrentRecordId { get; init; }

    public string? Status { get; init; }
}
