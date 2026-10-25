using Overlyric.App;
using Overlyric.Lyrics;

namespace Overlyric.LyricWindowsTests;

/// <summary>
/// Checks the wording and badge rules shared by the lyric selector and the complete lyric viewer.
/// These are the decisions a screenshot cannot prove: which promise a record makes, how a length
/// delta is classified, and where the current line is.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        Offsets();
        Durations();
        TimestampsAndLines();
        Badges();
        Candidates();
        ActiveLine();
        Sheets();

        Console.WriteLine(_failures == 0 ? "All lyric window checks passed." : $"{_failures} check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    private static void Offsets()
    {
        Check("zero offset is neutral", LyricText.FormatOffset(0) == "0.00 s", LyricText.FormatOffset(0));
        Check("later timing carries a plus", LyricText.FormatOffset(250) == "+0.25 s", LyricText.FormatOffset(250));
        Check("earlier timing carries a minus", LyricText.FormatOffset(-250) == "-0.25 s", LyricText.FormatOffset(-250));
        Check("the clamp reads in whole seconds", LyricText.FormatOffset(10_000) == "+10.00 s", LyricText.FormatOffset(10_000));
    }

    private static void Durations()
    {
        Check("a missing length is an em dash", LyricText.FormatDuration(null) == "\u2014", LyricText.FormatDuration(null));
        Check("a zero length is an em dash", LyricText.FormatDuration(0) == "\u2014", LyricText.FormatDuration(0));
        Check("four minutes six seconds", LyricText.FormatDuration(246.4) == "4:06", LyricText.FormatDuration(246.4));
        Check("an hour keeps the hour", LyricText.FormatDuration(3725) == "1:02:05", LyricText.FormatDuration(3725));

        Check("a 1.4 second difference matches", LyricText.FormatDurationDelta(246.4, 245.0) == "matches",
            LyricText.FormatDurationDelta(246.4, 245.0));
        Check("a 5 second difference is shown with a sign", LyricText.FormatDurationDelta(250, 245) == "+5.0 s",
            LyricText.FormatDurationDelta(250, 245));
        Check("a shorter record is negative", LyricText.FormatDurationDelta(240, 245) == "-5.0 s",
            LyricText.FormatDurationDelta(240, 245));
        Check("a long track's delta uses the same wider band", LyricText.FormatDurationDelta(412, 410) == "matches",
            LyricText.FormatDurationDelta(412, 410));
        Check("an unknown track length cannot be matched", LyricText.FormatDurationDelta(240, null) == "\u2014",
            LyricText.FormatDurationDelta(240, null));

        Check("the match band accepts 1.9 seconds", LyricText.DurationMatches(246.9, 245.0));
        Check("the match band refuses 2.1 seconds", !LyricText.DurationMatches(247.1, 245.0));
        Check("the band widens to 0.5 % on a long track", LyricText.DurationMatches(412.0, 410.0));
        Check("a long track still refuses three seconds", !LyricText.DurationMatches(413.0, 410.0));
    }

    private static void TimestampsAndLines()
    {
        Check("zero is a zero timestamp", LyricText.FormatTimestamp(TimeSpan.Zero) == "00:00.00",
            LyricText.FormatTimestamp(TimeSpan.Zero));
        Check("centiseconds survive the format",
            LyricText.FormatTimestamp(TimeSpan.FromSeconds(67.42)) == "01:07.42",
            LyricText.FormatTimestamp(TimeSpan.FromSeconds(67.42)));
        Check("a negative start is clamped", LyricText.FormatTimestamp(TimeSpan.FromSeconds(-1)) == "00:00.00",
            LyricText.FormatTimestamp(TimeSpan.FromSeconds(-1)));

        Check("no lines has no label", LyricText.FormatLineCount(0) == "", LyricText.FormatLineCount(0));
        Check("one line is singular", LyricText.FormatLineCount(1) == "1 line", LyricText.FormatLineCount(1));
        Check("many lines are plural", LyricText.FormatLineCount(20) == "20 lines", LyricText.FormatLineCount(20));

        Check("a short label is untouched", LyricText.Fit("Hello", 10) == "Hello");
        Check("a long label is ellipsised", LyricText.Fit("Hello there", 8) == "Hello t\u2026", LyricText.Fit("Hello there", 8));
    }

    private static void Badges()
    {
        Check("word timings outrank line timings",
            LyricBadges.ForCandidate(new LyricsCandidate { Id = 1, HasWordSync = true, HasSynced = true }).Kind == LyricBadgeKind.WordSynced);
        Check("line timings beat plain",
            LyricBadges.ForCandidate(new LyricsCandidate { Id = 1, HasSynced = true, HasPlain = true }).Kind == LyricBadgeKind.Synced);
        Check("plain is the last non-empty answer",
            LyricBadges.ForCandidate(new LyricsCandidate { Id = 1, HasPlain = true }).Kind == LyricBadgeKind.Plain);
        Check("an instrumental outranks an empty body",
            LyricBadges.ForCandidate(new LyricsCandidate { Id = 1, IsInstrumental = true }).Kind == LyricBadgeKind.Instrumental);
        Check("no body means no lyrics",
            LyricBadges.ForCandidate(new LyricsCandidate { Id = 1 }).Kind == LyricBadgeKind.NoLyrics);

        Check("a running lookup says so",
            LyricBadges.ForSheet(false, false, false, false, false).Kind == LyricBadgeKind.LookingUp);
        Check("an instrumental sheet says so",
            LyricBadges.ForSheet(true, true, false, false, false).Kind == LyricBadgeKind.Instrumental);
        Check("an empty sheet says no lyrics",
            LyricBadges.ForSheet(true, false, false, false, false).Kind == LyricBadgeKind.NoLyrics);
        Check("untimed words are plain",
            LyricBadges.ForSheet(true, false, true, false, false).Kind == LyricBadgeKind.Plain);
        Check("timed words are synced",
            LyricBadges.ForSheet(true, false, true, true, false).Kind == LyricBadgeKind.Synced);
        Check("word-timed words are word-synced",
            LyricBadges.ForSheet(true, false, true, true, true).Kind == LyricBadgeKind.WordSynced);
    }

    private static void Candidates()
    {
        var word = new LyricsCandidate
        {
            Id = 7,
            TrackName = "Winning",
            AlbumName = "The Missing Piece",
            DurationSeconds = 257,
            HasWordSync = true
        };

        Check("a word-synced record is named as such", word.Describe().Contains("word-synced", StringComparison.Ordinal),
            word.Describe());
        Check("the album is included when asked", word.Describe().Contains("The Missing Piece", StringComparison.Ordinal),
            word.Describe());
        Check("the album can be left out", !word.Describe(includeAlbum: false).Contains("The Missing Piece", StringComparison.Ordinal),
            word.Describe(includeAlbum: false));

        var plain = new LyricsCandidate { Id = 8, HasPlain = true };
        Check("a plain record is named plain", plain.Describe().Contains("plain", StringComparison.Ordinal), plain.Describe());
    }

    private static void ActiveLine()
    {
        LyricLine[] lines =
        [
            new(TimeSpan.FromSeconds(0), "one"),
            new(TimeSpan.FromSeconds(10), "two"),
            new(TimeSpan.FromSeconds(20), "three")
        ];

        Check("before the first line nothing is active", LyricText.ActiveLine(lines, TimeSpan.FromSeconds(-1)) == -1);
        Check("the first line is active at zero", LyricText.ActiveLine(lines, TimeSpan.Zero) == 0);
        Check("a line stays active until the next one", LyricText.ActiveLine(lines, TimeSpan.FromSeconds(19.9)) == 1);
        Check("the exact next start moves on", LyricText.ActiveLine(lines, TimeSpan.FromSeconds(20)) == 2);
        Check("after the last line it stays there", LyricText.ActiveLine(lines, TimeSpan.FromHours(1)) == 2);
    }

    private static void Sheets()
    {
        var sheet = new LyricSheet
        {
            Title = "Winning",
            Artist = "Gentle Giant",
            Lines = [new(TimeSpan.Zero, "one"), new(TimeSpan.FromSeconds(4), "two")]
        };

        Check("a sheet knows whether it has a track", sheet.HasTrack);
        Check("a sheet counts its lines", sheet.LineCount == 2, sheet.LineCount);

        var empty = new LyricSheet();
        Check("an empty sheet has no track", !empty.HasTrack);
        Check("an empty sheet has no lines", empty.LineCount == 0);
    }

    private static void Check(string name, bool passed, object? detail = null)
    {
        if (passed)
        {
            Console.WriteLine($"PASS  {name}");
            return;
        }

        _failures++;
        Console.WriteLine($"FAIL  {name}{(detail is null ? "" : $"  [{detail}]")}");
    }
}
