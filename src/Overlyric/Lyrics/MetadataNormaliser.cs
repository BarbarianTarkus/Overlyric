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
    /// Strips trailing release-noise qualifiers and leading movement listings from a track name.
    /// <c>"A Song For All Seasons (2019 Remaster)"</c> becomes <c>"A Song For All Seasons"</c>, and
    /// <c>"Song of Scheherazade / I. Fanfare - II. / The Betrayal"</c> becomes
    /// <c>"Song of Scheherazade"</c>. Returns the input unchanged when nothing is recognised, so
    /// over-cleaning degrades to a no-op.
    /// </summary>
    public static string CleanTitle(string? title) => CleanTitle(title, artist: null);

    /// <summary>
    /// Strips the release-edition noise a player can append to an album name, as in
    /// <c>"The Lamb Lies Down on Broadway (2007 Stereo Mix)"</c> or
    /// <c>"Scheherazade and Other Stories (Remastered)"</c>.
    /// </summary>
    /// <remarks>
    /// This exists for the same reason <see cref="CleanTitle(string?)"/> does, but albums need their
    /// own pass. The suffix is part of the player's edition name, which lrclib frequently files under
    /// the plain album, and lrclib matches <c>album_name</c> exactly: asking for the suffixed name
    /// returns either nothing (so the album-scoped search finds no records at all) or a different
    /// family of records. Asking for the plain name asks the question the data can answer.
    /// <para>
    /// Only trailing qualifiers are removed. Movement listings are a property of track titles, and a
    /// dash inside an album name is ordinary punctuation, so neither is touched here. Qualifiers that
    /// name a genuinely different performance - <c>(Live)</c>, <c>(Acoustic)</c> - are not release
    /// noise and are deliberately kept, exactly as they are for titles.
    /// </para>
    /// </remarks>
    public static string CleanAlbum(string? album)
    {
        var original = (album ?? string.Empty).Trim();
        if (original.Length == 0)
        {
            return original;
        }

        var cleaned = StripQualifiers(original);
        return cleaned.Length == 0 ? original : cleaned;
    }

    /// <summary>
    /// The same cleaning, plus the one case a title cannot fix on its own: players that put the artist
    /// in the title. YouTube reports <c>King Crimson - Two Hands</c> while naming the artist
    /// <c>King Crimson</c>, and lrclib matches <c>Two Hands</c> - asking for
    /// <c>King Crimson - Two Hands</c> returns no records at all. The prefix is removed only when it
    /// actually equals the reported artist, so a title such as <c>Simon &amp; Garfunkel - The Boxer</c>
    /// played by somebody else is left alone.
    /// </summary>
    public static string CleanTitle(string? title, string? artist)
    {
        var original = (title ?? string.Empty).Trim();
        if (original.Length == 0)
        {
            return original;
        }

        // Movement listings are removed first, so that "Song (Remastered) / I. Fanfare" reduces all
        // the way down to "Song" when the qualifier pass runs afterwards.
        var current = StripQualifiers(StripMovementListings(original));

        var withoutArtist = StripArtistPrefix(current, artist);
        if (!string.Equals(withoutArtist, current, StringComparison.Ordinal))
        {
            // Removing "Artist - " can expose the end of the title to the same two passes, as in
            // "Artist - Song (Remastered)" or "Artist - Song / I. Fanfare".
            current = StripQualifiers(StripMovementListings(withoutArtist));
        }

        return current.Length == 0 ? original : current;
    }

    /// <summary>Repeats the movement-listing pass until the title stops changing.</summary>
    private static string StripMovementListings(string value)
    {
        while (true)
        {
            var trimmed = StripMovementListing(value);
            if (trimmed is null || trimmed.Length == 0)
            {
                return value;
            }

            value = trimmed;
        }
    }

    /// <summary>Qualifiers stack: "Song (Remastered) (Deluxe Edition)".</summary>
    private static string StripQualifiers(string value)
    {
        while (true)
        {
            var stripped = StripTrailingQualifier(value);
            if (stripped is null || stripped.Length == 0)
            {
                return value;
            }

            value = stripped;
        }
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
    /// Removes a leading <c>Artist - </c> when the reported artist is exactly what precedes the dash.
    /// Repeated prefixes are removed too, because some metadata pipelines add the artist twice.
    /// </summary>
    private static string StripArtistPrefix(string title, string? artist)
    {
        var wanted = ForComparison(artist);
        if (wanted.Length < 2)
        {
            return title;
        }

        var current = title;

        while (true)
        {
            var stripped = TryStripArtistPrefix(current, wanted);
            if (stripped is null)
            {
                return current;
            }

            current = stripped;
        }
    }

    private static string? TryStripArtistPrefix(string title, string wanted)
    {
        foreach (var separator in DashSeparators)
        {
            var index = title.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0)
            {
                continue;
            }

            if (!string.Equals(ForComparison(title[..index]), wanted, StringComparison.Ordinal))
            {
                continue;
            }

            var tail = title[(index + separator.Length)..].Trim();
            if (tail.Length > 0)
            {
                return tail;
            }
        }

        return null;
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
