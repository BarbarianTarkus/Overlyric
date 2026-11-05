using System;
using System.Collections.Generic;

namespace Overlyric.Lyrics;

/// <summary>
/// How much of a line has been sung at a given position.
/// <para>
/// This is the whole karaoke decision, kept away from the renderer so it can be tested without a
/// window. The result is a character count, not a pixel position: the renderer turns it into a
/// clip with DirectWrite, which respects kerning, proportional widths and CJK.
/// </para>
/// <para>
/// When the sheet carries word timings they are followed exactly. When it does not, the line's own
/// span is shared out over its singable characters - letters and digits, not punctuation or spaces -
/// which is an estimate, and the reason a sheet with word timings is worth preferring.
/// </para>
/// </summary>
public static class KaraokeSweep
{
    /// <summary>Guessed duration of the last word when the sheet gives it no end.</summary>
    private static readonly TimeSpan LastWordFallback = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Characters of <paramref name="line"/> already sung at <paramref name="position"/>, from 0 to
    /// the line's length.
    /// </summary>
    /// <param name="line">The line being sung.</param>
    /// <param name="lineEnd">
    /// When the line ends: its own <see cref="LyricLine.End"/> when known, otherwise the next line's
    /// start. Ignored when the line carries word timings.
    /// </param>
    /// <param name="position">Playback position.</param>
    public static int SungCharacters(LyricLine line, TimeSpan lineEnd, TimeSpan position)
    {
        var text = line.Text ?? string.Empty;

        if (text.Length == 0 || position <= line.Start)
        {
            return 0;
        }

        if (line.HasWordTimings && WordsMatchText(line))
        {
            return FromWords(line, position);
        }

        var end = lineEnd > line.Start ? lineEnd : line.Start;

        if (position >= end)
        {
            return text.Length;
        }

        var span = (end - line.Start).TotalMilliseconds;
        if (span <= 0)
        {
            return text.Length;
        }

        return AtFraction(text, (position - line.Start).TotalMilliseconds / span);
    }

    /// <summary>
    /// Whether the word timings line up with the displayed text closely enough to be trusted. A sheet
    /// whose words do not reconstruct its own line is worse than no word timings at all.
    /// </summary>
    private static bool WordsMatchText(LyricLine line)
    {
        var words = line.Words!;
        var text = line.Text ?? string.Empty;

        if (words.Count == 0 || text.Length == 0)
        {
            return false;
        }

        var total = 0;
        foreach (var word in words)
        {
            total += word.Text.Length;
        }

        return Math.Abs(total - text.Length) <= Math.Max(2, text.Length / 4);
    }

    private static int FromWords(LyricLine line, TimeSpan position)
    {
        var words = line.Words!;
        var offset = 0;

        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var length = word.Text.Length;

            if (length == 0)
            {
                continue;
            }

            var start = word.Start;
            var end = word.End
                      ?? (i + 1 < words.Count ? words[i + 1].Start : start + LastWordFallback);

            if (position >= end)
            {
                offset += length;
                continue;
            }

            if (position <= start)
            {
                return Clamp(offset, line.Text.Length);
            }

            var span = (end - start).TotalMilliseconds;
            var fraction = span > 0 ? (position - start).TotalMilliseconds / span : 1.0;

            return Clamp(offset + AtFraction(word.Text, fraction), line.Text.Length);
        }

        return Clamp(offset, line.Text.Length);
    }

    /// <summary>
    /// Maps a 0..1 progress onto a character index, counting only characters that are actually sung.
    /// Sharing the span out over every character instead would make the sweep race through the end
    /// of a line that ends in punctuation.
    /// </summary>
    private static int AtFraction(string text, double fraction)
    {
        fraction = Math.Clamp(fraction, 0.0, 1.0);

        var singable = 0;
        foreach (var c in text)
        {
            if (IsSingable(c))
            {
                singable++;
            }
        }

        if (singable == 0)
        {
            return (int)Math.Round(fraction * text.Length, MidpointRounding.AwayFromZero);
        }

        var target = (int)Math.Round(fraction * singable, MidpointRounding.AwayFromZero);
        if (target <= 0)
        {
            return 0;
        }

        var seen = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsSingable(text[i]))
            {
                continue;
            }

            seen++;
            if (seen == target)
            {
                return i + 1;
            }
        }

        return text.Length;
    }

    /// <summary>Letters and digits are sung; spaces, punctuation and symbols are not.</summary>
    public static bool IsSingable(char c) => char.IsLetterOrDigit(c);

    private static int Clamp(int value, int length) => Math.Clamp(value, 0, length);
}
