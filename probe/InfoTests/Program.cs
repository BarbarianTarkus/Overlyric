using Overlyric.App;
using Overlyric.Overlay;

namespace Overlyric.InfoTests;

/// <summary>
/// Checks the pure logic behind the transient overlay notice: its lifetime
/// (<see cref="InfoPopup"/>), the line that describes what the lyric lookup found
/// (<see cref="LyricStatus"/>), and when a Windows notification is warranted
/// (<see cref="LyricsAlert"/>).
/// </summary>
/// <remarks>
/// None of these types reads a clock or touches a window, so the whole matrix runs in milliseconds.
/// The probe compiles the four source files directly instead of referencing the app, which keeps it
/// runnable without Vortice or WinRT.
/// </remarks>
internal static class Program
{
    private const string Track = "Boards of Canada - Roygbiv";

    private static int _checks;
    private static int _failures;

    private static int Main()
    {
        InfoPopupChecks();
        LyricStatusChecks();
        LyricsAlertChecks();

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ALL {_checks} CHECKS PASSED"
            : $"{_failures} OF {_checks} CHECK(S) FAILED");

        return _failures == 0 ? 0 : 1;
    }

    // ---- InfoPopup: a notice is time-boxed from the moment it appears, and a new showing restarts
    // ---- the full deadline rather than extending the old one.
    private static void InfoPopupChecks()
    {
        var popup = new InfoPopup();
        Check("a fresh pop-up shows nothing", !popup.IsShowing && popup.Current is null, NoticeText(popup));

        popup.Start("Boards of Canada - Roygbiv", LyricStatus.WordSyncedDetail, NoticeTone.Info, 1_000, 3);
        Check("Start makes the notice visible", popup.IsShowing && popup.Current is not null, NoticeText(popup));
        Check("Start keeps the title", popup.Current?.Title == "Boards of Canada - Roygbiv", NoticeText(popup));
        Check("Start keeps the detail", popup.Current?.Detail == LyricStatus.WordSyncedDetail, NoticeText(popup));
        Check("Start keeps the tone", popup.Current?.Tone == NoticeTone.Info, NoticeText(popup));

        Check("one millisecond before the deadline changes nothing", !popup.Expire(3_999) && popup.IsShowing, NoticeText(popup));
        Check("at exactly the deadline the notice expires", popup.Expire(4_000) && !popup.IsShowing, NoticeText(popup));
        Check("expiring an already-expired notice reports no second change", !popup.Expire(4_001), NoticeText(popup));

        // The provisional "looking up" notice has gone; the verdict arrives late and starts a new
        // showing. Its deadline comes from the second start, not from the first one.
        popup.Start("A", "first", NoticeTone.Info, 1_000, 2);
        popup.Start("A", "second", NoticeTone.Warning, 2_000, 2);
        Check("a restart passes the old deadline instead of expiring",
            !popup.Expire(3_000) && popup.IsShowing, NoticeText(popup));
        Check("the restarted showing replaces the detail", popup.Current?.Detail == "second", NoticeText(popup));
        Check("the restarted showing replaces the tone", popup.Current?.Tone == NoticeTone.Warning, NoticeText(popup));
        Check("the restarted showing expires at its own deadline", popup.Expire(4_000) && !popup.IsShowing, NoticeText(popup));

        popup.Start("A", "shown", NoticeTone.Info, 5_000, 4);
        popup.Start("A", "shown", NoticeTone.Info, 5_100, 0);
        Check("a zero-second start clears whatever is visible",
            !popup.IsShowing && popup.Current is null, NoticeText(popup));
        Check("a zero-second start leaves nothing to expire", !popup.Expire(6_000), NoticeText(popup));

        popup.Start("A", "shown", NoticeTone.Info, 7_000, 5);
        popup.Start("A", "shown", NoticeTone.Info, 7_100, -1);
        Check("a negative duration is treated as off", !popup.IsShowing, NoticeText(popup));

        popup.Start("A", "shown", NoticeTone.Info, 8_000, 5);
        popup.Clear();
        Check("Clear drops a visible notice", !popup.IsShowing && popup.Current is null, NoticeText(popup));
        Check("Clear on an empty pop-up is harmless", !popup.Expire(9_000) && !popup.IsShowing, NoticeText(popup));
    }

    // ---- LyricStatus: one line, with the precedence AppHost.BuildVisual already uses. ----
    private static void LyricStatusChecks()
    {
        var looking = LyricStatus.Describe(lookupComplete: false, false, false, false, false, false);
        Check("an incomplete lookup says it is looking", looking.Detail == LyricStatus.LookingUpDetail, looking.Detail);
        Check("an incomplete lookup is informational", looking.Tone == NoticeTone.Info, looking.Tone);
        var lookingWithEverything = LyricStatus.Describe(false, true, false, false, false, false);
        Check("the lookup line outranks an instrumental answer",
            lookingWithEverything.Detail == LyricStatus.LookingUpDetail, lookingWithEverything.Detail);

        var instrumental = LyricStatus.Describe(true, true, false, false, false, false);
        Check("an instrumental track is named as such", instrumental.Detail == LyricStatus.InstrumentalDetail, instrumental.Detail);
        Check("an instrumental track is informational", instrumental.Tone == NoticeTone.Info, instrumental.Tone);
        var instrumentalNoPosition = LyricStatus.Describe(true, true, true, true, false, false);
        Check("instrumental outranks a missing playback position",
            instrumentalNoPosition.Detail == LyricStatus.InstrumentalDetail, instrumentalNoPosition.Detail);

        var noLyrics = LyricStatus.Describe(true, false, false, false, false, true);
        Check("no lines says no lyrics were found", noLyrics.Detail == LyricStatus.NoLyricsDetail, noLyrics.Detail);
        Check("no lyrics warns", noLyrics.Tone == NoticeTone.Warning, noLyrics.Tone);
        var noLyricsNoPosition = LyricStatus.Describe(true, false, false, false, false, false);
        Check("no lyrics outranks a missing playback position",
            noLyricsNoPosition.Detail == LyricStatus.NoLyricsDetail, noLyricsNoPosition.Detail);

        var noPosition = LyricStatus.Describe(true, false, true, true, false, false);
        Check("a synced sheet with no playback position says so",
            noPosition.Detail == LyricStatus.NoPositionDetail, noPosition.Detail);
        Check("a missing playback position warns", noPosition.Tone == NoticeTone.Warning, noPosition.Tone);
        var noPositionUntimed = LyricStatus.Describe(true, false, true, false, false, false);
        Check("a missing playback position outranks the unsynced verdict",
            noPositionUntimed.Detail == LyricStatus.NoPositionDetail, noPositionUntimed.Detail);

        var untimed = LyricStatus.Describe(true, false, true, false, false, true);
        Check("untimed lyrics are called not synced", untimed.Detail == LyricStatus.NotSyncedDetail, untimed.Detail);
        Check("untimed lyrics warn", untimed.Tone == NoticeTone.Warning, untimed.Tone);

        var lineSynced = LyricStatus.Describe(true, false, true, true, false, true);
        Check("line timings are named", lineSynced.Detail == LyricStatus.SyncedDetail, lineSynced.Detail);
        Check("line timings are informational", lineSynced.Tone == NoticeTone.Info, lineSynced.Tone);
        Check("line timings are distinguished from word timings",
            lineSynced.Detail != LyricStatus.WordSyncedDetail, lineSynced.Detail);

        var wordSynced = LyricStatus.Describe(true, false, true, true, true, true);
        Check("word timings are named", wordSynced.Detail == LyricStatus.WordSyncedDetail, wordSynced.Detail);
        Check("word timings are informational", wordSynced.Tone == NoticeTone.Info, wordSynced.Tone);
    }

    // ---- LyricsAlert: only a settled, definitive answer is worth a Windows notification. ----
    private static void LyricsAlertChecks()
    {
        Check("a definitive miss announces no lyrics",
            LyricsAlert.Decide(true, false, false, false) == LyricsAlertKind.NoLyrics,
            LyricsAlert.Decide(true, false, false, false));
        Check("a transient failure announces nothing",
            LyricsAlert.Decide(false, false, false, false) == LyricsAlertKind.None,
            LyricsAlert.Decide(false, false, false, false));
        Check("untimed lyrics warrant an alert",
            LyricsAlert.Decide(true, false, true, false) == LyricsAlertKind.NoSyncedLyrics,
            LyricsAlert.Decide(true, false, true, false));
        Check("untimed lyrics are still announced after a retryable miss",
            LyricsAlert.Decide(false, false, true, false) == LyricsAlertKind.NoSyncedLyrics,
            LyricsAlert.Decide(false, false, true, false));
        Check("synced lyrics need no alert",
            LyricsAlert.Decide(true, false, true, true) == LyricsAlertKind.None,
            LyricsAlert.Decide(true, false, true, true));
        Check("an instrumental is never alerted even when definitive and line-less",
            LyricsAlert.Decide(true, true, false, false) == LyricsAlertKind.None,
            LyricsAlert.Decide(true, true, false, false));
        Check("an instrumental with untimed words is never alerted",
            LyricsAlert.Decide(false, true, true, false) == LyricsAlertKind.None,
            LyricsAlert.Decide(false, true, true, false));

        var noLyrics = LyricsAlert.Describe(LyricsAlertKind.NoLyrics, Track);
        Check("the no-lyrics title says so",
            noLyrics.Title.Contains("no lyrics", StringComparison.OrdinalIgnoreCase), noLyrics.Title);
        Check("the no-lyrics body mentions the track",
            noLyrics.Text.Contains(Track, StringComparison.Ordinal), noLyrics.Text);

        var noSynced = LyricsAlert.Describe(LyricsAlertKind.NoSyncedLyrics, Track);
        Check("the unsynced title says so",
            noSynced.Title.Contains("not synced", StringComparison.OrdinalIgnoreCase), noSynced.Title);
        Check("the unsynced body mentions the track",
            noSynced.Text.Contains(Track, StringComparison.Ordinal), noSynced.Text);
        Check("the two alerts do not read the same", noLyrics.Title != noSynced.Title, noSynced.Title);

        var none = LyricsAlert.Describe(LyricsAlertKind.None, Track);
        Check("None has no title", none.Title == "", none.Title);
        Check("None has no body", none.Text == "", none.Text);
    }

    private static void Check(string what, bool ok, object? detail)
    {
        _checks++;
        if (ok)
        {
            Console.WriteLine($"PASS | {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"FAIL | {what}   (got: {detail})");
    }

    private static string NoticeText(InfoPopup popup) =>
        popup.Current is { } notice
            ? $"'{notice.Title}' / '{notice.Detail}' / {notice.Tone}"
            : "<no notice>";
}
