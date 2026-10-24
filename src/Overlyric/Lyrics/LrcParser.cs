using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Overlyric.Lyrics;

/// <summary>
/// Parser for LRC ("lyrics with timestamps") text.
/// </summary>
/// <remarks>
/// Deliberately hand-written instead of regex based so that it stays allocation light and
/// NativeAOT friendly, and so that malformed input can never throw.
/// <para>
/// Supported timestamp forms: <c>[mm:ss.xx]</c>, <c>[mm:ss.xxx]</c>, <c>[mm:ss]</c>, <c>[m:ss.xx]</c>.
/// Several timestamps may prefix one line (<c>[00:12.00][01:30.00]Chorus</c>) and each emits its own
/// <see cref="LyricLine"/>.
/// </para>
/// <para>
/// The <c>[offset:+/-ms]</c> tag is honoured with the LRC convention:
/// <c>timestamp = raw - offset/1000</c>, i.e. a positive offset moves lyrics earlier and a negative
/// offset moves them later. The tag may appear anywhere in the document.
/// </para>
/// </remarks>
public static class LrcParser
{
    /// <summary>Largest accepted minute field; guards against overflow and nonsense values.</summary>
    private const int MaxMinutes = 5999;

    /// <summary>Largest accepted second field, so <c>[99:99.99]</c> is treated as malformed.</summary>
    private const int MaxSeconds = 59;

    /// <summary>
    /// Parses LRC text. Never throws: lines that cannot be understood are skipped.
    /// </summary>
    /// <param name="lrc">LRC document; may contain a UTF-8 BOM and CRLF line endings.</param>
    /// <returns>Lyric lines sorted ascending by <see cref="LyricLine.Start"/>.</returns>
    public static IReadOnlyList<LyricLine> Parse(string lrc)
    {
        if (string.IsNullOrEmpty(lrc))
        {
            return Array.Empty<LyricLine>();
        }

        var text = lrc.AsSpan();
        if (!text.IsEmpty && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        var lines = new List<LyricLine>();
        var offsetMs = 0;
        var hasOffset = false;

        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var relativeEnd = text[lineStart..].IndexOf('\n');
            ReadOnlySpan<char> line;
            var isLast = relativeEnd < 0;
            if (isLast)
            {
                line = text[lineStart..];
            }
            else
            {
                line = text.Slice(lineStart, relativeEnd);
            }

            if (!line.IsEmpty && line[^1] == '\r')
            {
                line = line[..^1];
            }

            ParseLine(line, lines, ref offsetMs, ref hasOffset);

            if (isLast)
            {
                break;
            }

            lineStart += relativeEnd + 1;
        }

        if (lines.Count == 0)
        {
            return Array.Empty<LyricLine>();
        }

        if (hasOffset && offsetMs != 0)
        {
            // LRC convention: a positive offset shifts the lyrics earlier, so it is subtracted.
            var shift = TimeSpan.FromMilliseconds(offsetMs);
            for (var i = 0; i < lines.Count; i++)
            {
                lines[i] = lines[i] with { Start = lines[i].Start - shift };
            }
        }

        // OrderBy is a stable sort, so lines sharing a timestamp keep their document order.
        return lines.OrderBy(static line => line.Start).ToArray();
    }

    /// <summary>
    /// Extracts the leading timestamp/metadata tags of one line and, when at least one valid
    /// timestamp was found and text remains, appends the resulting lyric lines.
    /// </summary>
    private static void ParseLine(ReadOnlySpan<char> line, List<LyricLine> lines, ref int offsetMs, ref bool hasOffset)
    {
        var position = 0;
        List<TimeSpan>? timestamps = null;

        while (position < line.Length && line[position] == '[')
        {
            if (TryParseTimestamp(line, position, out var afterTimestamp, out var start))
            {
                (timestamps ??= new List<TimeSpan>(1)).Add(start);
                position = afterTimestamp;
                continue;
            }

            if (TryParseTag(line, position, out var afterTag, out var key, out var value))
            {
                if (key.Equals("offset", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
                    {
                        offsetMs = parsed;
                        hasOffset = true;
                    }
                }

                // [ar:], [ti:], [al:], [by:], [re:], [ve:], [length:] and friends are ignored.
                position = afterTag;
                continue;
            }

            // Not a timestamp and not a tag: everything from here on is the lyric text.
            break;
        }

        if (timestamps is null || timestamps.Count == 0)
        {
            // Lines without a timestamp are metadata or plain text, never synced lyrics.
            return;
        }

        var text = line[position..].Trim();
        if (text.IsEmpty)
        {
            return;
        }

        var lyric = text.ToString();
        foreach (var timestamp in timestamps)
        {
            lines.Add(new LyricLine(timestamp, lyric));
        }
    }

    /// <summary>
    /// Parses <c>[mm:ss]</c>, <c>[mm:ss.ff]</c> or <c>[mm:ss.fff]</c> starting at <paramref name="start"/>.
    /// </summary>
    private static bool TryParseTimestamp(ReadOnlySpan<char> line, int start, out int end, out TimeSpan value)
    {
        end = start;
        value = default;

        if (start >= line.Length || line[start] != '[')
        {
            return false;
        }

        var index = start + 1;

        var minutes = ReadDigits(line, ref index, 1, 4);
        if (minutes < 0 || index >= line.Length || line[index] != ':')
        {
            return false;
        }

        index++;
        var seconds = ReadDigits(line, ref index, 1, 2);
        if (seconds < 0)
        {
            return false;
        }

        var milliseconds = 0;
        if (index < line.Length && line[index] == '.')
        {
            index++;
            var fractionStart = index;
            var fraction = ReadDigits(line, ref index, 1, 3);
            if (fraction < 0)
            {
                return false;
            }

            milliseconds = (index - fractionStart) switch
            {
                1 => fraction * 100,
                2 => fraction * 10,
                _ => fraction,
            };
        }

        if (index >= line.Length || line[index] != ']')
        {
            return false;
        }

        if (minutes > MaxMinutes || seconds > MaxSeconds)
        {
            return false;
        }

        end = index + 1;
        value = new TimeSpan(0, 0, minutes, seconds, milliseconds);
        return true;
    }

    /// <summary>Parses a <c>[key:value]</c> metadata tag such as <c>[ar:Artist]</c> or <c>[offset:-500]</c>.</summary>
    private static bool TryParseTag(ReadOnlySpan<char> line, int start, out int end, out string key, out string value)
    {
        end = start;
        key = string.Empty;
        value = string.Empty;

        if (start >= line.Length || line[start] != '[')
        {
            return false;
        }

        var inner = line[(start + 1)..];
        var close = inner.IndexOf(']');
        if (close < 0)
        {
            return false;
        }

        var body = inner[..close];
        var colon = body.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        var name = body[..colon];
        if (name.Length > 16)
        {
            return false;
        }

        foreach (var ch in name)
        {
            if (!char.IsAsciiLetter(ch))
            {
                return false;
            }
        }

        end = start + 1 + close + 1;
        key = name.ToString();
        value = body[(colon + 1)..].ToString();
        return true;
    }

    /// <summary>Reads between <paramref name="min"/> and <paramref name="max"/> ASCII digits, or returns -1.</summary>
    private static int ReadDigits(ReadOnlySpan<char> span, ref int index, int min, int max)
    {
        var cursor = index;
        var value = 0;
        var count = 0;

        while (count < max && cursor < span.Length && (uint)(span[cursor] - '0') <= 9)
        {
            value = (value * 10) + (span[cursor] - '0');
            cursor++;
            count++;
        }

        if (count < min)
        {
            return -1;
        }

        index = cursor;
        return value;
    }
}
