using System;

namespace Overlyric.Lyrics;

/// <summary>
/// A single lyric line anchored to a point in the track.
/// For unsynchronised lyrics <see cref="Start"/> is <see cref="TimeSpan.Zero"/>.
/// </summary>
public readonly record struct LyricLine(TimeSpan Start, string Text);
