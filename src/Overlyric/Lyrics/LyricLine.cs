using System;
using System.Collections.Generic;

namespace Overlyric.Lyrics;

/// <summary>One word of a line, when the sheet carries word-level timings.</summary>
public readonly record struct LyricWord(TimeSpan Start, TimeSpan? End, string Text);

/// <summary>
/// A single lyric line anchored to a point in the track.
/// For unsynchronised lyrics <see cref="Start"/> is <see cref="TimeSpan.Zero"/>.
/// </summary>
/// <param name="Start">When the line begins.</param>
/// <param name="Text">The words, as displayed.</param>
/// <param name="End">
/// When the line ends, when the sheet says so. Absent for plain LRC, where the end is inferred from
/// the next line's start - which is wrong across long instrumental gaps.
/// </param>
/// <param name="Words">Word-level timings, when lrclib has them for this line.</param>
public readonly record struct LyricLine(
    TimeSpan Start,
    string Text,
    TimeSpan? End = null,
    IReadOnlyList<LyricWord>? Words = null)
{
    public bool HasWordTimings => Words is { Count: > 0 };
}
