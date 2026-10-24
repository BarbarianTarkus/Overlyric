using System;
using System.Collections.Generic;

namespace Overlyric.Lyrics;

/// <summary>
/// A minimal reader for lrclib's <c>lyricsfile</c>: the YAML document every record carries, which is
/// the only place word-level timings live.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not a YAML library. The document has one shape - a <c>lines</c> list of
/// <c>text</c>/<c>start_ms</c>/<c>end_ms</c> items, some of which carry a nested <c>words</c> list of
/// the same fields - and NativeAOT plus a 9 MB binary is a poor place to add a general parser. A
/// hand-written reader for that shape is smaller, has no dependencies and cannot throw.
/// </para>
/// <para>
/// Anything it does not recognise is skipped rather than fatal: a record whose lyricsfile cannot be
/// understood still has its <c>syncedLyrics</c>, and that is the fallback the caller uses.
/// </para>
/// </remarks>
internal static class LyricsFile
{
    /// <summary>
    /// The lines of the document, with word timings where present. Returns an empty list when the
    /// text is not a lyricsfile this reader understands.
    /// </summary>
    public static IReadOnlyList<LyricLine> ParseLines(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return [];
        }

        try
        {
            return Parse(yaml);
        }
        catch (Exception)
        {
            // A malformed document must never cost the caller the synced lyrics it already has.
            return [];
        }
    }

    private static IReadOnlyList<LyricLine> Parse(string yaml)
    {
        var lines = new List<LyricLine>();

        var inLines = false;
        var inWords = false;
        var lineIndent = -1;

        string? text = null;
        long? startMs = null;
        long? endMs = null;
        List<LyricWord>? words = null;

        string? wordText = null;
        long? wordStartMs = null;
        long? wordEndMs = null;

        void FlushWord()
        {
            if (wordText is not null && wordStartMs is not null)
            {
                (words ??= []).Add(new LyricWord(
                    TimeSpan.FromMilliseconds(wordStartMs.Value),
                    wordEndMs is null ? null : TimeSpan.FromMilliseconds(wordEndMs.Value),
                    wordText));
            }

            wordText = null;
            wordStartMs = null;
            wordEndMs = null;
        }

        void FlushLine()
        {
            FlushWord();

            if (text is not null && startMs is not null)
            {
                lines.Add(new LyricLine(
                    TimeSpan.FromMilliseconds(startMs.Value),
                    text,
                    endMs is null ? null : TimeSpan.FromMilliseconds(endMs.Value),
                    words is { Count: > 0 } ? words : null));
            }

            text = null;
            startMs = null;
            endMs = null;
            words = null;
            inWords = false;
        }

        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var indent = 0;
            while (indent < line.Length && line[indent] == ' ')
            {
                indent++;
            }

            var content = line[indent..].TrimEnd();

            if (!inLines)
            {
                if (content.StartsWith("lines:", StringComparison.Ordinal))
                {
                    inLines = true;
                }

                continue;
            }

            if (content.StartsWith("- ", StringComparison.Ordinal))
            {
                content = content[2..].TrimStart();
            }
            else if (content == "-")
            {
                continue;
            }

            var separator = content.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = content[..separator].Trim();
            var value = content[(separator + 1)..].Trim();

            // A line item at the shallowest indent starts a new line; anything deeper inside a
            // `words:` block is a word of the current line.
            var isLineItem = lineIndent < 0 || indent <= lineIndent;

            if (isLineItem && lineIndent < 0)
            {
                lineIndent = indent;
            }

            if (key.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                if (inWords && !isLineItem)
                {
                    FlushWord();
                    wordText = Unquote(value);
                    wordStartMs = null;
                    wordEndMs = null;
                }
                else
                {
                    FlushLine();
                    text = Unquote(value);
                    startMs = null;
                    endMs = null;
                }

                continue;
            }

            if (key.Equals("words", StringComparison.OrdinalIgnoreCase))
            {
                FlushWord();
                inWords = true;
                continue;
            }

            if (key.Equals("start_ms", StringComparison.OrdinalIgnoreCase))
            {
                if (inWords && !isLineItem)
                {
                    wordStartMs = ParseMilliseconds(value);
                }
                else
                {
                    startMs = ParseMilliseconds(value);
                }

                continue;
            }

            if (key.Equals("end_ms", StringComparison.OrdinalIgnoreCase))
            {
                if (inWords && !isLineItem)
                {
                    wordEndMs = ParseMilliseconds(value);
                }
                else
                {
                    endMs = ParseMilliseconds(value);
                }
            }
        }

        FlushLine();
        return lines;
    }

    private static long? ParseMilliseconds(string value)
    {
        var text = value.Trim().Trim('"', '\'');

        if (long.TryParse(text, out var parsed))
        {
            return parsed;
        }

        if (double.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var asDouble))
        {
            return (long)Math.Round(asDouble);
        }

        return null;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            var first = value[0];
            var last = value[^1];

            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                var inner = value[1..^1];
                return first == '"'
                    ? inner.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal)
                    : inner.Replace("''", "'", StringComparison.Ordinal);
            }
        }

        return value;
    }
}
