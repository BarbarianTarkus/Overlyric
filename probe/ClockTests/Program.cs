using System.Diagnostics;
using Overlyric.Media;

namespace Overlyric.ClockTests;

/// <summary>
/// Verifies the assumption that decides whether synced lyrics survive Overlyric being started
/// mid-song: the position a player reports is a snapshot taken at <c>LastUpdatedTime</c>, not the
/// position now.
/// </summary>
/// <remarks>
/// TIDAL is why it matters. Measured with <c>probe\SmtcProbe</c>, it holds
/// <c>position = 00:00:00.017</c> and <c>LastUpdatedTime</c> at the moment the track began, for the
/// whole track. Trusting the raw number means starting Overlyric eleven minutes in still shows the
/// first line, which is exactly what this file guards against.
/// </remarks>
internal static class Program
{
    private static int _failures;

    private static async Task<int> Main()
    {
        // As TIDAL reports them for the entire track.
        var trackStart = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(11);
        var frozenPosition = TimeSpan.FromSeconds(0.017);

        // ---- 1. The bug: a frozen 00:00 with no stamp is taken at face value. ----
        var legacy = new PlaybackClock();
        legacy.ResetForNewTrack(frozenPosition, default, true);
        await Task.Delay(200);
        Check(
            "a frozen 00:00 with no stamp starts the lyrics at the beginning",
            legacy.Position.TotalSeconds < 1,
            legacy.Position);

        // ---- 2. Attaching mid-song, as Overlyric does when it is started while a track plays. ----
        var midSong = new PlaybackClock();
        midSong.ResetForNewTrack(frozenPosition, trackStart, true);
        Check(
            "attaching mid-song lands on the real position, not 00:00",
            Math.Abs(midSong.Position.TotalSeconds - 660) < 1.0,
            midSong.Position);

        await Task.Delay(300);
        var beforeFrozenPoll = midSong.Position;

        // A frozen player keeps reporting the same two values forever.
        midSong.Observe(frozenPosition, trackStart, timelineAdvanced: false, playing: true);
        Check(
            "a frozen poll does not drag the position back",
            beforeFrozenPoll.TotalSeconds is > 659 and < 662,
            midSong.Position);
        Check("the position keeps advancing", midSong.Position > beforeFrozenPoll, midSong.Position);

        // ---- 3. Pause and resume mid-song: the position must be kept, not rebuilt from 00:00. ----
        midSong.Observe(frozenPosition, trackStart, timelineAdvanced: false, playing: false);
        var atPause = midSong.Position;
        await Task.Delay(250);
        Check("pausing freezes the position", midSong.Position == atPause, midSong.Position);

        midSong.Observe(frozenPosition, trackStart, timelineAdvanced: false, playing: true);
        Check(
            "resuming keeps the mid-song position",
            Math.Abs((midSong.Position - atPause).TotalSeconds) < 0.2,
            midSong.Position);

        // ---- 4. Started while the track was paused: the reset can only see the frozen 00:00. ----
        var pausedAttach = new PlaybackClock();
        pausedAttach.ResetForNewTrack(frozenPosition, trackStart, playing: false);
        Check(
            "a paused attach has nothing better than the raw position",
            pausedAttach.Position.TotalSeconds < 1,
            pausedAttach.Position);

        pausedAttach.Observe(frozenPosition, trackStart, timelineAdvanced: false, playing: true);
        Check(
            "playback then seeds the position from the frozen stamp",
            pausedAttach.Position.TotalSeconds is > 659 and < 662,
            pausedAttach.Position);

        // ---- 5. A live timeline (browsers, Spotify) is still followed, and still snapped to. ----
        // One poll sees position and stamp both about 0.9 s old, which is what a player that
        // updates its timeline roughly once a second actually publishes.
        var live = new PlaybackClock();
        var elapsed = Stopwatch.StartNew();
        live.ResetForNewTrack(TimeSpan.FromSeconds(100), DateTimeOffset.UtcNow, true);

        var reported = TimeSpan.FromSeconds(100);
        for (var poll = 0; poll < 6; poll++)
        {
            await Task.Delay(200);

            var staleness = TimeSpan.FromMilliseconds(900);
            reported = TimeSpan.FromSeconds(100) + elapsed.Elapsed - staleness;
            live.Observe(reported, DateTimeOffset.UtcNow - staleness, timelineAdvanced: true, playing: true);
        }

        Check("a progressing timeline is detected as live", live.TimelineIsLive);
        Check(
            "an aged report lines up with real time instead of pulling the clock back",
            Math.Abs((live.Position - (TimeSpan.FromSeconds(100) + elapsed.Elapsed)).TotalSeconds) < 0.2,
            live.Position);
        Check("following a live timeline is not mistaken for seeking", live.SeekCount == 0);

        live.Observe(TimeSpan.FromSeconds(300), DateTimeOffset.UtcNow, timelineAdvanced: true, playing: true);
        Check(
            "a real seek snaps to the new position",
            live.Position.TotalSeconds is > 299 and < 301,
            live.Position);
        Check("a real seek is counted", live.SeekCount >= 1);

        // ---- 6. Players that tell us less. ----
        var paused = new PlaybackClock();
        paused.ResetForNewTrack(
            TimeSpan.FromSeconds(42),
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
            playing: false);
        Check(
            "a paused position is never aged (that would count the pause as playback)",
            Math.Abs(paused.Position.TotalSeconds - 42) < 0.001,
            paused.Position);

        var unstamped = new PlaybackClock();
        unstamped.ResetForNewTrack(TimeSpan.FromSeconds(5), default, true);
        Check(
            "a player with no stamp falls back to the raw position",
            Math.Abs(unstamped.Position.TotalSeconds - 5) < 0.05,
            unstamped.Position);

        // ---- 7. A player that publishes a track but no timeline whatsoever. ----
        // AIMP with the third-party "Windows Media Control" plug-in is exactly this: the track and
        // the play state arrive, the position stays 00:00 and the stamp stays at zero. Windows hands
        // that zero over as the FILETIME epoch, and ageing it blindly would place the track four
        // centuries in and pin the overlay to the last line of the song.
        var fileTimeEpoch = new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var noTimeline = new PlaybackClock();
        noTimeline.ResetForNewTrack(TimeSpan.Zero, fileTimeEpoch, true);
        await Task.Delay(200);
        Check(
            "a FILETIME-epoch stamp is not aged into the future",
            noTimeline.Position < TimeSpan.FromMinutes(1),
            noTimeline.Position);

        noTimeline.Observe(TimeSpan.Zero, fileTimeEpoch, timelineAdvanced: false, playing: true);
        Check("a player publishing no timeline is not treated as usable", !noTimeline.PositionIsUsable);

        var stale = new PlaybackClock();
        stale.ResetForNewTrack(TimeSpan.Zero, DateTimeOffset.UtcNow - TimeSpan.FromDays(2), true);
        Check(
            "a two-day-old stamp is not an anchor either",
            stale.Position < TimeSpan.FromMinutes(1) && !stale.PositionIsUsable,
            stale.Position);

        var frozenButStamped = new PlaybackClock();
        frozenButStamped.ResetForNewTrack(frozenPosition, trackStart, true);
        Check("a frozen timeline with a real stamp is still usable", frozenButStamped.PositionIsUsable);

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool passed, TimeSpan? position = null)
    {
        var measured = position is { } value ? $"   ({value.ToString(@"hh\:mm\:ss\.fff")})" : "";
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} | {name}{measured}");

        if (!passed)
        {
            _failures++;
        }
    }
}
