using Overlyric.Lyrics;

namespace Overlyric.KaraokeTests;

/// <summary>
/// Checks the two pieces of karaoke that a listener would notice immediately if they were wrong:
/// how much of a line is considered sung at a given moment, and whether lrclib's lyricsfile is read
/// correctly - including the case where its word timings do not line up with its own line text.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        var start = TimeSpan.Zero;
        var end = TimeSpan.FromSeconds(4);
        var line = new LyricLine(start, "Hello world");

        // ---- The plain case: no word timings, so the span is shared out ----
        Check("before the line starts nothing is sung",
            KaraokeSweep.SungCharacters(line, end, TimeSpan.FromSeconds(-1)) == 0);

        Check("at the start nothing is sung",
            KaraokeSweep.SungCharacters(line, end, start) == 0);

        Check("past the end the whole line is sung",
            KaraokeSweep.SungCharacters(line, end, TimeSpan.FromSeconds(5)) == line.Text.Length);

        var half = KaraokeSweep.SungCharacters(line, end, TimeSpan.FromSeconds(2));
        Check("halfway is about half the singable characters", half is >= 4 and <= 8, half);

        var monotonic = true;
        var previous = -1;
        for (var ms = 0; ms <= 4000; ms += 100)
        {
            var sung = KaraokeSweep.SungCharacters(line, end, TimeSpan.FromMilliseconds(ms));
            if (sung < previous)
            {
                monotonic = false;
                break;
            }

            previous = sung;
        }

        Check("the sweep never goes backwards within a line", monotonic);

        // ---- Punctuation must not be sung: "Hello!!!" should not race to the end ----
        var excited = new LyricLine(start, "Hello!!!");
        var atHalf = KaraokeSweep.SungCharacters(excited, end, TimeSpan.FromSeconds(2));
        Check("trailing punctuation is not sung ahead of the words", atHalf is >= 2 and <= 4, atHalf);

        // ---- Word timings, when they line up ----
        var worded = new LyricLine(
            start,
            "Hello world",
            end,
            [
                new LyricWord(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hello "),
                new LyricWord(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "world")
            ]);

        var atQuarter = KaraokeSweep.SungCharacters(worded, end, TimeSpan.FromMilliseconds(500));
        Check("halfway through the first word is halfway through it", atQuarter is >= 2 and <= 4, atQuarter);

        Check("the first word is complete at its end",
            KaraokeSweep.SungCharacters(worded, end, TimeSpan.FromSeconds(1)) == 6);

        Check("the second word starts where the first ends",
            KaraokeSweep.SungCharacters(worded, end, TimeSpan.FromMilliseconds(1001)) is >= 6 and <= 7,
            KaraokeSweep.SungCharacters(worded, end, TimeSpan.FromMilliseconds(1001)));

        Check("the whole line is sung at the end",
            KaraokeSweep.SungCharacters(worded, end, TimeSpan.FromSeconds(2)) == 11);

        // ---- Word timings that do not reconstruct the line are not trusted ----
        var mismatched = new LyricLine(
            start,
            "Completely different words",
            end,
            [new LyricWord(TimeSpan.Zero, TimeSpan.FromSeconds(1), "x")]);

        var fromMismatch = KaraokeSweep.SungCharacters(mismatched, end, TimeSpan.FromSeconds(2));
        Check("mismatched word timings fall back to the line span",
            fromMismatch > 0 && fromMismatch < mismatched.Text.Length, fromMismatch);

        // ---- Degenerate inputs ----
        Check("an empty line is never sung",
            KaraokeSweep.SungCharacters(new LyricLine(start, string.Empty), end, TimeSpan.FromSeconds(1)) == 0);

        Check("a zero-length span completes the line",
            KaraokeSweep.SungCharacters(line, start, TimeSpan.FromMilliseconds(1)) == line.Text.Length);

        // ---- The lyricsfile reader ----
        const string yaml = """
            version: '1.0'
            metadata:
              title: Creep
              artist: Radiohead
              duration_ms: 239000
              instrumental: false
            lines:
              - text: "When you were here before"
                start_ms: 0
                end_ms: 4000
                words:
                  - text: "When"
                    start_ms: 0
                    end_ms: 400
                  - text: " you"
                    start_ms: 400
                    end_ms: 700
              - text: "Couldn't look you in the eye: no"
                start_ms: 4000
                end_ms: 8000
            """;

        var parsed = LyricsFile.ParseLines(yaml);
        Check("the lyricsfile reader finds both lines", parsed.Count == 2, parsed.Count);
        Check("the first line keeps its text",
            parsed.Count == 2 && parsed[0].Text == "When you were here before",
            parsed.Count == 2 ? parsed[0].Text : "");
        Check("the first line keeps its end",
            parsed.Count == 2 && parsed[0].End == TimeSpan.FromSeconds(4),
            parsed.Count == 2 ? parsed[0].End : null);
        Check("the first line keeps its words",
            parsed.Count == 2 && parsed[0].Words is { Count: 2 },
            parsed.Count == 2 ? parsed[0].Words?.Count : null);
        Check("the second line has no words",
            parsed.Count == 2 && parsed[1].Words is null,
            parsed.Count == 2 ? parsed[1].Words?.Count : null);
        Check("a colon inside the text is not a field separator",
            parsed.Count == 2 && parsed[1].Text == "Couldn't look you in the eye: no",
            parsed.Count == 2 ? parsed[1].Text : "");

        Check("garbage is refused without throwing", LyricsFile.ParseLines("not: [a, lyricsfile").Count == 0);
        Check("empty text is refused", LyricsFile.ParseLines("").Count == 0);
        Check("null is refused", LyricsFile.ParseLines(null).Count == 0);

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");

        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string what, bool ok, object? detail = null)
    {
        if (ok)
        {
            Console.WriteLine($"PASS | {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"FAIL | {what}   (got: {detail})");
    }
}
