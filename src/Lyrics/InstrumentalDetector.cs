using System;
using System.Collections.Generic;
using System.Text;

namespace Overlyric.Lyrics;

/// <summary>
/// Works out whether a track has nothing to sing.
/// <para>
/// Three cases look identical to a naive reader but deserve the same outcome: lrclib flagging the
/// record <c>instrumental</c>; an lrclib record whose <em>entire</em> lyric body is a placeholder
/// such as <c>Instrumental</c>, <c>[Instrumental]</c> or <c>[Solo]</c>; and a track lrclib has no
/// record for at all while the title says it is an instrumental.
/// </para>
/// <para>
/// The middle case is the one that bites. Contributors routinely write the word instead of setting
/// the flag, and parsed naively it becomes a real lyric line, so the overlay proudly displays
/// "Instrumental" in time with the music. The recorded flag cannot be relied on, so the body itself
/// has to be inspected.
/// </para>
/// <para>
/// The crucial safety property is that a verdict requires the <em>whole</em> body to be placeholder
/// text. A song with real words plus an <c>[Instrumental Break]</c> or <c>[Chorus]</c> marker in the
/// middle is unaffected, because its other lines are not placeholders.
/// </para>
/// </summary>
public static class InstrumentalDetector
{
    /// <summary>
    /// Normalised phrases that mean "there is nothing to sing here" rather than being a lyric.
    /// </summary>
    private static readonly HashSet<string> PlaceholderPhrases = new(StringComparer.Ordinal)
    {
        // The word itself, in the forms contributors actually write.
        "instrumental", "instrumentals", "instrumental track", "instrumental version",
        "instrumental music", "instrumental only", "instrumental part", "instrumental section",
        "instrumental break", "instrumental interlude", "instrumental solo", "instrumental intro",
        "instrumental outro", "instrumental passage", "instrumental jam", "inst", "instrumental chorus",

        // Explicit statements that there are no words.
        "no lyrics", "no lyric", "no vocal", "no vocals", "no singing", "no voice",
        "without lyrics", "without vocals", "lyrics not available", "lyrics unavailable",

        // Nothing to sing.
        "music", "music only", "just music", "only music",
        "silence", "tacet", "spoken", "spoken word",

        // Instrumental forms and section names. Only ever acted on when the entire body
        // consists of these, which is what keeps real songs with section markers safe.
        "solo", "break", "interlude", "intro", "outro", "segue", "jam", "riff",
        "overture", "prelude", "theme", "reprise", "coda", "orchestral", "orchestra",
        "chorus", "verse", "bridge", "pre-chorus", "prechorus", "post-chorus", "postchorus",
        "refrain", "hook", "repeat", "x2", "x3", "x4", "2x", "3x", "4x",

        // Filler punctuation that normalises down to nothing meaningful.
        "---", "***",
    };

    /// <summary>Substrings that mark a title or album as instrumental, used only as a last resort.</summary>
    private static readonly string[] TitleMarkers =
    [
        "instrumental", "off vocal", "offvocal", "no vocal"
    ];

    /// <summary>
    /// True when every line is placeholder text, so there is nothing worth displaying.
    /// </summary>
    public static bool IsPlaceholderOnly(IReadOnlyList<LyricLine> lines)
    {
        if (lines.Count == 0)
        {
            return false;
        }

        var sawContent = false;

        foreach (var line in lines)
        {
            var normalised = Normalise(line.Text);
            if (normalised.Length == 0)
            {
                continue;
            }

            sawContent = true;

            if (!PlaceholderPhrases.Contains(normalised))
            {
                return false;
            }
        }

        return sawContent;
    }

    /// <summary>
    /// True when raw lyric source is nothing but placeholders. Handles timestamps and metadata tags,
    /// so a body of <c>[Instrumental]</c> with no timestamps is still recognised.
    /// </summary>
    public static bool IsPlaceholderText(string? rawLyrics)
    {
        if (string.IsNullOrWhiteSpace(rawLyrics))
        {
            return false;
        }

        var sawContent = false;

        foreach (var rawLine in rawLyrics.Split('\n'))
        {
            var normalised = Normalise(rawLine);
            if (normalised.Length == 0)
            {
                continue;
            }

            sawContent = true;

            if (!PlaceholderPhrases.Contains(normalised))
            {
                return false;
            }
        }

        return sawContent;
    }

    /// <summary>True when a single line is placeholder text.</summary>
    public static bool IsPlaceholder(string? line)
    {
        var normalised = Normalise(line);
        return normalised.Length > 0 && PlaceholderPhrases.Contains(normalised);
    }

    /// <summary>
    /// True when the track's title or album says it is an instrumental. Only consulted when lrclib
    /// returned nothing, so it can never override real lyrics.
    /// </summary>
    public static bool TitleIndicatesInstrumental(string? title, string? album) =>
        ContainsTitleMarker(title) || ContainsTitleMarker(album);

    private static bool ContainsTitleMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var marker in TitleMarkers)
        {
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reduces a lyric line to comparable words: drops <c>[mm:ss.xx]</c> timestamps and
    /// <c>[ar:…]</c> metadata tags, discards brackets, musical symbols and punctuation, collapses
    /// whitespace and lowercases. <c>"[00:01.00] [Instrumental]"</c> becomes <c>"instrumental"</c>.
    /// </summary>
    public static string Normalise(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(line.Length);
        var index = 0;

        while (index < line.Length)
        {
            var current = line[index];

            if (current == '[')
            {
                var close = line.IndexOf(']', index + 1);

                // Only timestamps and tags are dropped wholesale; "[Instrumental]" is content.
                if (close > index && IsTimestampOrTag(line.AsSpan(index + 1, close - index - 1)))
                {
                    builder.Append(' ');
                    index = close + 1;
                    continue;
                }
            }

            // Keep letters and digits (including accented ones), turn everything else into a space.
            builder.Append(char.IsLetterOrDigit(current) ? char.ToLowerInvariant(current) : ' ');
            index++;
        }

        return CollapseSpaces(builder.ToString());
    }

    /// <summary>Distinguishes <c>[00:12.34]</c> and <c>[ar:Name]</c> from <c>[Instrumental]</c>.</summary>
    private static bool IsTimestampOrTag(ReadOnlySpan<char> inner)
    {
        if (inner.IsEmpty)
        {
            return false;
        }

        var colon = inner.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        // [ar:…], [offset:-500], [length:03:21]
        var key = inner[..colon];
        if (key.Length is >= 2 and <= 10)
        {
            var allLetters = true;

            foreach (var c in key)
            {
                if (!char.IsAsciiLetter(c))
                {
                    allLetters = false;
                    break;
                }
            }

            if (allLetters)
            {
                return true;
            }
        }

        // [00:12.34], [1:02:03.5]
        foreach (var c in inner)
        {
            if (!char.IsAsciiDigit(c) && c is not (':' or '.' or ','))
            {
                return false;
            }
        }

        return true;
    }

    private static string CollapseSpaces(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = true;

        foreach (var c in value)
        {
            if (c == ' ')
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(c);
            lastWasSpace = false;
        }

        // Trailing space, if any, is dropped here.
        return builder.ToString().TrimEnd();
    }
}
