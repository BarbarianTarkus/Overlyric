using System.Globalization;
using System.Text;

namespace Overlyric.Lyrics;

/// <summary>
/// Reads and writes lyric files: encoding detection for the files a listener supplies, and the LRC
/// text an export produces.
/// </summary>
/// <remarks>
/// <para>
/// Import is deliberately forgiving about encoding and strict about not crashing: a BOM wins, then
/// strict UTF-8, then a UTF-16 without-BOM guess, then Windows-1252. The last one is implemented as
/// a table instead of a <c>CodePagesEncodingProvider</c> because the application ships with
/// <c>InvariantGlobalization</c>, and mapping the 27 non-Latin-1 bytes of CP1252 is a handful of
/// characters rather than a provider dependency. Other ANSI code pages are therefore not decoded
/// correctly - the common Western European case is - and the limitation is documented.
/// </para>
/// <para>
/// Export writes <c>[mm:ss.fff]</c> timestamps so a re-import reproduces the same instants exactly,
/// not merely to the centisecond. The round-trip is checked by <c>probe/LocalLyricsTests</c>.
/// </para>
/// </remarks>
public static class LyricTextCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Decodes a lyric file that may have been saved by anything. Never throws.</summary>
    public static string Decode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            // BOM-less UTF-16 is checked before UTF-8: ASCII-heavy UTF-16LE is technically valid
            // UTF-8 as well (the high bytes are NUL), so a strict UTF-8 attempt would "succeed" and
            // return text with a NUL between every character. The NUL ratio is the decisive signal.
            var utf16 = TryDecodeUtf16WithoutBom(bytes);
            if (utf16 is not null)
            {
                return utf16;
            }

            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // Not UTF-8; fall back to the ANSI code page.
            }

            return DecodeWindows1252(bytes);
        }
        catch (Exception)
        {
            return DecodeWindows1252(bytes);
        }
    }

    /// <summary>
    /// Builds an LRC document with the metadata header and one timestamped line per lyric. Text is
    /// written with CRLF, which is what Windows lyric players expect.
    /// </summary>
    public static string EncodeLrc(
        string artist,
        string title,
        string? album,
        double? durationSeconds,
        IReadOnlyList<LyricLine> lines)
    {
        var builder = new StringBuilder(1024);

        builder.Append("[ar:").Append(Clean(artist)).Append("]\r\n");
        builder.Append("[ti:").Append(Clean(title)).Append("]\r\n");
        builder.Append("[al:").Append(Clean(album ?? string.Empty)).Append("]\r\n");

        if (durationSeconds is > 0)
        {
            builder.Append("[length:").Append(FormatLength(TimeSpan.FromSeconds(durationSeconds.Value))).Append("]\r\n");
        }

        foreach (var line in lines.OrderBy(static line => line.Start))
        {
            builder.Append('[')
                .Append(FormatTimestamp(line.Start))
                .Append(']')
                .Append(Clean(line.Text))
                .Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>UTF-8 without a BOM, the form the importer's first branch reads back.</summary>
    public static byte[] EncodeUtf8(string text) => Utf8NoBom.GetBytes(text);

    /// <summary><c>mm:ss.fff</c>, with minutes allowed past 59 so long tracks keep their timing.</summary>
    public static string FormatTimestamp(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        var minutes = (long)Math.Floor(position.TotalMinutes);
        var seconds = position.Seconds;
        var milliseconds = position.Milliseconds;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{minutes:00}:{seconds:00}.{milliseconds:000}");
    }

    private static string FormatLength(TimeSpan duration)
    {
        var minutes = (long)Math.Floor(duration.TotalMinutes);
        return string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{duration.Seconds:00}");
    }

    private static string Clean(string? value) =>
        value is null ? string.Empty : value.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);

    private static string? TryDecodeUtf16WithoutBom(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes.Length % 2 != 0)
        {
            return null;
        }

        var evenZeros = 0;
        var oddZeros = 0;

        for (var i = 0; i < bytes.Length; i += 2)
        {
            if (bytes[i] == 0)
            {
                evenZeros++;
            }

            if (bytes[i + 1] == 0)
            {
                oddZeros++;
            }
        }

        var pairs = bytes.Length / 2;
        var quarter = Math.Max(1, pairs / 4);

        // ASCII-heavy UTF-16LE has zero high bytes at the odd positions; BE the other way round.
        if (oddZeros >= quarter && evenZeros < oddZeros)
        {
            return Encoding.Unicode.GetString(bytes);
        }

        if (evenZeros >= quarter && oddZeros < evenZeros)
        {
            return Encoding.BigEndianUnicode.GetString(bytes);
        }

        return null;
    }

    private static string DecodeWindows1252(byte[] bytes)
    {
        var chars = new char[bytes.Length];

        for (var i = 0; i < bytes.Length; i++)
        {
            var value = bytes[i];
            chars[i] = value is >= 0x80 and <= 0x9F ? Cp1252High[value - 0x80] : (char)value;
        }

        return new string(chars);
    }

    /// <summary>The 0x80-0x9F block where Windows-1252 differs from Latin-1.</summary>
    private static readonly char[] Cp1252High =
    [
        '\u20AC', '\u0081', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
        '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\u008D', '\u017D', '\u008F',
        '\u0090', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
        '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\u009D', '\u017E', '\u0178'
    ];
}
