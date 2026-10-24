using System;
using System.Collections.Generic;

namespace Overlyric.Lyrics;

/// <summary>
/// Cleans release-metadata noise out of track metadata before it is used to query lrclib.
/// <para>
/// This exists because of a concrete failure. TIDAL reports
/// <c>A Song For All Seasons (2019 Remaster)</c>, and lrclib holds a record whose track name carries
/// that exact suffix - but with <em>plain</em> lyrics only. Searching for the suffixed name returns
/// just that family of records, so the app concluded the track had no timings. Searching for
/// <c>A Song For All Seasons</c> instead returns twenty candidates including a synced record with the
/// same album and an identical duration. The suffix was not merely untidy: it selected the wrong record.
/// </para>
/// <para>
/// Qualifiers that genuinely identify a different performance - <c>(Live)</c>, <c>(Acoustic)</c> -
/// are deliberately <em>not</em> stripped, so a live take never silently resolves to the studio cut.
/// Duration ranking handles the rest.
/// </para>
/// </summary>
public static class MetadataNormaliser
{
    /// <summary>
    /// Substrings that mark a bracketed or dashed suffix as release noise rather than a performance.
    /// </summary>
    private static readonly string[] NoiseKeywords =
    [
        "remaster", "remastered", "remastering", "expanded", "deluxe", "edition",
        "anniversary", "reissue", "remix", "radio edit", "single version", "album version",
        "bonus track", "special edition", "collector", "hi-res", "digitally remastered",
        "mono", "stereo", "digitally remastered",
    ];

    private static readonly string[] DashSeparators = [" - ", " – ", " — "];

    private static readonly string[] RomanNumerals =
        ["i", "ii", "iii", "iv", "v", "vi", "vii", "viii", "ix", "x", "xi", "xii"];

    /// <summary>
    /// Strips trailing release-noise qualifiers and leading movement listings from a track or album
    /// name. <c>"A Song For All Seasons (2019 Remaster)"</c> becomes <c>"A Song For All Seasons"</c>,
    /// and <c>"Song of Scheherazade / I. Fanfare - II. / The Betrayal"</c> becomes
    /// <c>"Song of Scheherazade"</c>. Returns the input unchanged when nothing is recognised, so
    /// over-cleaning degrades to a no-op.
    /// </summary>
    public static string CleanTitle(string? title)
    {
        var original = (title ?? string.Empty).Trim();
        if (original.Length == 0)
        {
            return original;
        }

        var current = original;

        // Movement listings are removed first, so that "Song (Remastered) / I. Fanfare" reduces all
        // the way down to "Song" when the qualifier pass runs afterwards.
        while (true)
        {
            var trimmed = StripMovementListing(current);
            if (trimmed is null || trimmed.Length == 0)
            {
                break;
            }

            current = trimmed;
        }

        // Qualifiers stack: "Song (Remastered) (Deluxe Edition)".
        while (true)
        {
            var stripped = StripTrailingQualifier(current);
            if (stripped is null || stripped.Length == 0)
            {
                break;
            }

            current = stripped;
        }

        return current.Length == 0 ? original : current;
    }

    /// <summary>
    /// Reduces a value to lowercase letters and digits for equality comparisons, so
    /// <c>"A Song For All Seasons"</c> and <c>"A song for all seasons"</c> compare equal.
    /// </summary>
    public static string ForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var buffer = new char[value.Length];
        var length = 0;

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }

        return new string(buffer, 0, length);
    }

    /// <summary>
    /// Cuts a movement listing down to the work it belongs to: some players report a multi-part piece
    /// as one enormous title, e.g. <c>"Song of Scheherazade / I. Fanfare - II. / The Betrayal / …"</c>.
    /// No lrclib record carries such a title, so searching the whole thing finds nothing.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative: the separator must be a spaced slash <em>and</em> what follows must
    /// look like a movement list, so a title that legitimately contains a slash such as
    /// <c>"Love / Hate"</c> is left alone.
    /// </remarks>
    private static string? StripMovementListing(string value)
    {
        var index = value.IndexOf(" / ", StringComparison.Ordinal);
        if (index <= 0)
        {
            return null;
        }

        var head = value[..index].TrimEnd();
        if (head.Length < 2)
        {
            return null;
        }

        return LooksLikeMovementList(value[(index + 3)..]) ? head : null;
    }

    private static bool LooksLikeMovementList(string tail)
    {
        var trimmed = tail.TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (HasRomanNumeralPrefix(trimmed) || char.IsAsciiDigit(trimmed[0]))
        {
            return true;
        }

        // Several parts listed together, or parts joined by dashes.
        return trimmed.Contains(" / ", StringComparison.Ordinal)
               || trimmed.Contains(" - ", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the text opens with a roman numeral acting as a movement number, as in
    /// <c>"I. Fanfare"</c> but not <c>"I Will Always Love You"</c>.
    /// </summary>
    private static bool HasRomanNumeralPrefix(string value)
    {
        var end = 0;
        while (end < value.Length && char.IsAsciiLetter(value[end]))
        {
            end++;
        }

        if (end == 0)
        {
            return false;
        }

        var token = value[..end].ToLowerInvariant();
        if (Array.IndexOf(RomanNumerals, token) < 0)
        {
            return false;
        }

        var rest = value[end..].TrimStart();

        return rest.Length == 0 || rest[0] is '.' or ')' or ':' or '-';
    }

    private static string? StripTrailingQualifier(string value)
    {
        var trimmed = value.TrimEnd();
        if (trimmed.Length < 2)
        {
            return null;
        }

        // Trailing bracketed qualifier: "Song (2019 Remaster)", "Song [Remastered]".
        var last = trimmed[^1];
        var openChar = last switch
        {
            ')' => '(',
            ']' => '[',
            _ => '\0'
        };

        if (openChar != '\0')
        {
            var openIndex = trimmed.LastIndexOf(openChar);
            if (openIndex > 0)
            {
                var inner = trimmed[(openIndex + 1)..^1];
                if (ContainsNoise(inner))
                {
                    return trimmed[..openIndex].TrimEnd();
                }
            }
        }

        // Trailing dashed qualifier: "Song - Remastered 2005", "Song – 2012 Remaster".
        var dashIndex = LastDashIndex(trimmed);
        if (dashIndex > 0)
        {
            var tail = trimmed[(dashIndex + 1)..].Trim();
            if (ContainsNoise(tail))
            {
                return trimmed[..dashIndex].TrimEnd();
            }
        }

        return null;
    }

    private static int LastDashIndex(string value)
    {
        var best = -1;

        foreach (var separator in DashSeparators)
        {
            var index = value.LastIndexOf(separator, StringComparison.Ordinal);
            if (index > best)
            {
                best = index;
            }
        }

        return best;
    }

    private static bool ContainsNoise(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var keyword in NoiseKeywords)
        {
            if (value.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
