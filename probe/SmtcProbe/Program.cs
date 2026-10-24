using System.Diagnostics;
using Windows.Media.Control;

namespace SmtcProbe;

/// <summary>
/// Measures how (and whether) a player actually updates the SMTC timeline, and - with
/// <c>--sessions</c> - how Windows arbitrates when more than one application publishes at once.
/// This determines if synced lyrics can trust SMTC's position or must self-clock, and which
/// session an overlay should follow when several are available.
/// </summary>
internal static class Program
{
    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        int seconds = 90;
        bool secondsGiven = false, listSessions = false;

        foreach (var arg in args)
        {
            if (int.TryParse(arg, out var parsed))
            {
                seconds = parsed;
                secondsGiven = true;
            }
            else if (arg is "--sessions" or "--list" or "-l")
            {
                listSessions = true;
            }
        }

        if (listSessions)
        {
            // Watching a list is cheap, so it does not need the long default the timeline watch uses.
            await ListSessionsAsync(secondsGiven ? seconds : 15);
            return;
        }

        await WatchTimelineAsync(seconds);
    }

    /// <summary>
    /// Watches whichever session the system calls current, and reports every timeline event.
    /// </summary>
    private static async Task WatchTimelineAsync(int seconds)
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var session = manager.GetCurrentSession();
        if (session is null)
        {
            Console.WriteLine("NO_SESSION");
            return;
        }

        Console.WriteLine($"WATCH {session.SourceAppUserModelId} for {seconds}s");

        var sw = Stopwatch.StartNew();
        string lastTitle = "", lastArtist = "";
        TimeSpan lastPos = TimeSpan.MinValue;
        DateTimeOffset lastUpd = default;
        var lastStatus = (GlobalSystemMediaTransportControlsSessionPlaybackStatus)(-1);
        var lastHeartbeat = TimeSpan.Zero;

        int posChanges = 0, updChanges = 0, statusChanges = 0, trackChanges = 0;
        int tlEvents = 0, mpEvents = 0, pbEvents = 0;
        session.TimelinePropertiesChanged += (_, _) => Interlocked.Increment(ref tlEvents);
        session.MediaPropertiesChanged += (_, _) => Interlocked.Increment(ref mpEvents);
        session.PlaybackInfoChanged += (_, _) => Interlocked.Increment(ref pbEvents);

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var pb = session.GetPlaybackInfo();
            var tl = session.GetTimelineProperties();
            var props = await session.TryGetMediaPropertiesAsync();
            var title = props?.Title ?? "";
            var artist = props?.Artist ?? "";

            if (tl.Position != lastPos)
            {
                posChanges++;
                Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,7:0.00}s] POSITION {lastPos} -> {tl.Position}");
                lastPos = tl.Position;
            }

            if (tl.LastUpdatedTime != lastUpd)
            {
                updChanges++;
                Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,7:0.00}s] UPDATED  {lastUpd:HH:mm:ss.fff} -> {tl.LastUpdatedTime:HH:mm:ss.fff}");
                lastUpd = tl.LastUpdatedTime;
            }

            if (pb.PlaybackStatus != lastStatus)
            {
                statusChanges++;
                Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,7:0.00}s] STATUS   {lastStatus} -> {pb.PlaybackStatus}");
                lastStatus = pb.PlaybackStatus;
            }

            if (title != lastTitle || artist != lastArtist)
            {
                trackChanges++;
                Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,7:0.00}s] TRACK    '{lastArtist} - {lastTitle}' -> '{artist} - {title}'");
                lastTitle = title;
                lastArtist = artist;
            }

            if (sw.Elapsed - lastHeartbeat >= TimeSpan.FromSeconds(20))
            {
                lastHeartbeat = sw.Elapsed;
                Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,7:0.00}s] heartbeat pos={tl.Position} upd={tl.LastUpdatedTime:HH:mm:ss} status={pb.PlaybackStatus} events(tl={tlEvents},mp={mpEvents},pb={pbEvents})");
            }

            await Task.Delay(200);
        }

        Console.WriteLine($"DONE posChanges={posChanges} updChanges={updChanges} statusChanges={statusChanges} trackChanges={trackChanges} events tl={tlEvents} mp={mpEvents} pb={pbEvents}");
    }

    // ------------------------------------------------------------------ session list

    /// <summary>
    /// Lists every session Windows is holding, marks the one it considers current, and then reports
    /// each change while several are alive.
    /// </summary>
    /// <remarks>
    /// This answers the two questions a source picker depends on: does Windows switch its "current"
    /// session by itself when a second player starts, and can a session be told to be playing while
    /// its timeline stands still - which is the difference between a source worth following and one
    /// that would park the overlay on the first lyric line.
    /// </remarks>
    private static async Task ListSessionsAsync(int seconds)
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

        int sessionsChanged = 0, currentChanged = 0;
        manager.SessionsChanged += (_, _) => Interlocked.Increment(ref sessionsChanged);
        manager.CurrentSessionChanged += (_, _) => Interlocked.Increment(ref currentChanged);

        var entries = Enumerate(manager);
        if (entries is null)
        {
            // Saying "none" here would be a lie: the enumeration failed, it did not come back empty.
            Console.WriteLine("SESSIONS ?   (enumeration failed)");
            return;
        }

        Console.WriteLine($"SESSIONS {entries.Count}   (listing for {seconds}s)");

        if (entries.Count == 0)
        {
            // Same conclusion as NO_SESSION above, stated in this mode's own vocabulary.
            Console.WriteLine("  none: nothing publishes to Windows media controls.");
            return;
        }

        var currentSession = CurrentSession(manager);
        var currentAumid = AumidOf(currentSession);
        var currentKey = CurrentKey(entries, currentSession, currentAumid);

        var known = new Dictionary<string, (string Status, TimeSpan Position, DateTimeOffset Updated)>(StringComparer.Ordinal);
        var advancing = new HashSet<string>(StringComparer.Ordinal);
        var currentSeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, session) in entries)
        {
            Sample sample;
            bool positionAdvanced;
            try
            {
                (sample, positionAdvanced) = await DescribeAsync(key, session, key == currentKey);
            }
            catch (Exception ex)
            {
                // A player can exit while it is being described; that is a result, not a failure.
                Console.WriteLine($"  [       ] {key}  (unreadable: {ex.GetType().Name})");
                continue;
            }

            known[key] = (sample.Status, sample.Position, sample.Updated);
            seen.Add(key);

            // A session already advancing when the watch starts counts as advancing, so the watch
            // reports the onset rather than the continuation.
            if (positionAdvanced)
            {
                advancing.Add(key);
            }
        }

        // ---------------------------------------------------------------- watch for changes

        var sw = Stopwatch.StartNew();
        var lastSwitch = sw.Elapsed;
        var lastCurrent = currentAumid;
        var currentChanges = 0;

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(1000);

            if (lastCurrent.Length > 0)
            {
                currentSeconds[lastCurrent] = currentSeconds.GetValueOrDefault(lastCurrent) + (sw.Elapsed - lastSwitch).TotalSeconds;
            }

            var fresh = Enumerate(manager);
            if (fresh is null)
            {
                // Diffing against a failed poll would report every session as removed and then
                // added again; the known set is left alone instead.
                Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,7:0.00}s] FAILED   enumeration failed, keeping {known.Count} known session(s)");
            }
            else
            {
                await WatchOnce(sw, fresh, known, advancing, seen);
            }

            lastCurrent = AumidOf(CurrentSession(manager));
            if (!string.Equals(lastCurrent, currentAumid, StringComparison.Ordinal))
            {
                currentChanges++;
                Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,7:0.00}s] CURRENT  '{currentAumid}' -> '{lastCurrent}'");
                currentAumid = lastCurrent;
            }

            lastSwitch = sw.Elapsed;
        }

        if (lastCurrent.Length > 0)
        {
            currentSeconds[lastCurrent] = currentSeconds.GetValueOrDefault(lastCurrent) + (sw.Elapsed - lastSwitch).TotalSeconds;
        }

        Console.WriteLine($"SUMMARY seen={seen.Count} currentChanges={currentChanges} position-advancing=[{string.Join(", ", advancing)}] events(sessions={sessionsChanged}, current={currentChanged})");

        foreach (var (key, held) in currentSeconds.OrderByDescending(kv => kv.Value))
        {
            Console.WriteLine($"  held current {held,6:0.0}s  {key}");
        }
    }

    /// <summary>
    /// One session per entry, keyed for diffing. The public API exposes no stable session id, so
    /// identical app user model ids - two browser tabs, say - are told apart by their order in the
    /// list, which is stable enough to watch and not stable enough to persist a preference against.
    /// </summary>
    private static List<(string Key, GlobalSystemMediaTransportControlsSession Session)>? Enumerate(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var result = new List<(string, GlobalSystemMediaTransportControlsSession)>();
        var seenAumids = new Dictionary<string, int>(StringComparer.Ordinal);

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
        try
        {
            sessions = manager.GetSessions();
        }
        catch (Exception)
        {
            // Null, not empty: an enumeration that failed is not an enumeration that found nothing,
            // and telling those two apart is the difference between a quiet probe and one that
            // reports every session as removed and then added again.
            return null;
        }

        foreach (var session in sessions)
        {
            string aumid;
            try
            {
                aumid = session.SourceAppUserModelId ?? "";
            }
            catch (Exception)
            {
                // A session that closed between the call and the read has nothing left to report.
                continue;
            }

            var ordinal = seenAumids.GetValueOrDefault(aumid);
            seenAumids[aumid] = ordinal + 1;
            result.Add((ordinal == 0 ? aumid : $"{aumid}#{ordinal}", session));
        }

        return result;
    }

    /// <summary>
    /// One poll of the change watch: additions, removals, status flips, and the first time a session's
    /// <em>position</em> moves.
    /// </summary>
    private static async Task WatchOnce(
        Stopwatch sw,
        List<(string Key, GlobalSystemMediaTransportControlsSession Session)> fresh,
        Dictionary<string, (string Status, TimeSpan Position, DateTimeOffset Updated)> known,
        HashSet<string> advancing,
        HashSet<string> seen)
    {
        var freshKeys = fresh.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var (key, session) in fresh)
        {
            Sample sample;
            try
            {
                sample = await SampleAsync(session);
            }
            catch (Exception)
            {
                // A session can close between the enumeration and the read; the next poll settles it.
                continue;
            }

            if (!known.TryGetValue(key, out var previous))
            {
                Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,7:0.00}s] ADDED    {key}  status={sample.Status}");
                seen.Add(key);
            }
            else
            {
                // Position movement is what synced lyrics need. A player that stamps its clock while
                // its position stands still - AIMP paused - is not a followable source, so it does not
                // count here; the pos/upd numbers printed beside it let a human tell them apart.
                if (sample.Position != previous.Position && advancing.Add(key))
                {
                    Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,7:0.00}s] ADVANCES {key}  pos={sample.Position}  upd={sample.Updated:yyyy-MM-dd HH:mm:ss.fff}");
                }

                if (sample.Status != previous.Status)
                {
                    Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,7:0.00}s] STATUS   {key}  {previous.Status} -> {sample.Status}");
                }
            }

            known[key] = (sample.Status, sample.Position, sample.Updated);
        }

        foreach (var key in known.Keys.Where(k => !freshKeys.Contains(k)).ToList())
        {
            Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,7:0.00}s] REMOVED  {key}");
            known.Remove(key);

            // A player that exits and returns under the same app user model id has a new timeline,
            // and one that is genuinely advancing should be able to say so again.
            advancing.Remove(key);
        }
    }

    /// <summary>
    /// The session Windows calls current, or null if asking fails. The getter is guarded for the same
    /// reason the enumeration is: a session can close between the call and the read.
    /// </summary>
    private static GlobalSystemMediaTransportControlsSession? CurrentSession(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        try
        {
            return manager.GetCurrentSession();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string AumidOf(GlobalSystemMediaTransportControlsSession? session)
    {
        try
        {
            return session?.SourceAppUserModelId ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Which key the current session owns. Identity is compared first, because two sessions can share
    /// one app user model id - two browser tabs - and the raw id would always mark the first of them.
    /// </summary>
    private static string CurrentKey(
        List<(string Key, GlobalSystemMediaTransportControlsSession Session)> entries,
        GlobalSystemMediaTransportControlsSession? current,
        string currentAumid)
    {
        if (current is not null)
        {
            foreach (var (key, session) in entries)
            {
                if (ReferenceEquals(session, current))
                {
                    return key;
                }
            }
        }

        foreach (var (key, _) in entries)
        {
            if (string.Equals(key, currentAumid, StringComparison.Ordinal))
            {
                return key;
            }
        }

        return "";
    }

    private static async Task<(Sample After, bool PositionAdvanced)> DescribeAsync(
        string key,
        GlobalSystemMediaTransportControlsSession session,
        bool isCurrent)
    {
        var before = await SampleAsync(session);
        await Task.Delay(700);
        var after = await SampleAsync(session);

        var posMoved = after.Position - before.Position;
        var updMoved = after.Updated - before.Updated;

        // Only position movement makes a session followable. A player that updates its stamp while
        // its position stands still is a third thing, and reporting it as "advancing" would be the
        // exact mistake this probe exists to prevent.
        var positionAdvanced = posMoved > TimeSpan.Zero;
        var verdict = positionAdvanced ? "position" : updMoved > TimeSpan.Zero ? "stamp-only" : "none";

        Console.WriteLine($"  [{(isCurrent ? "current" : "       ")}] {key}");
        Console.WriteLine($"              status={after.Status}  pos={after.Position}  end={after.End}  upd={after.Updated:yyyy-MM-dd HH:mm:ss.fff}");
        Console.WriteLine($"              track='{after.Artist} - {after.Title}'{(after.Album is null ? "" : $"  album='{after.Album}'")}");
        Console.WriteLine($"              advancing={verdict,-10}  (pos {Signed(posMoved)}, upd {Signed(updMoved)})");

        if (after.Updated.Year <= 1601)
        {
            // The FILETIME zero: the player publishes a track but has never stamped a position.
            Console.WriteLine("              timeline=never-stamped (1601 is the FILETIME zero)");
        }

        return (after, positionAdvanced);
    }

    /// <summary>
    /// A delta with its sign kept. TimeSpan's custom formats apply to the absolute value, so a rewind
    /// - or a reset to the 1601 FILETIME zero, which is the case worth catching - would otherwise
    /// print as forward movement.
    /// </summary>
    private static string Signed(TimeSpan delta) =>
        $"{(delta < TimeSpan.Zero ? "-" : "+")}{delta.Duration():hh\\:mm\\:ss\\.fff}";

    private sealed record Sample(
        string Status,
        TimeSpan Position,
        TimeSpan End,
        DateTimeOffset Updated,
        string Title,
        string Artist,
        string? Album);

    private static async Task<Sample> SampleAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var props = await session.TryGetMediaPropertiesAsync();

        return new Sample(
            playback.PlaybackStatus.ToString(),
            timeline.Position,
            timeline.EndTime,
            timeline.LastUpdatedTime,
            props?.Title ?? "",
            props?.Artist ?? "",
            string.IsNullOrWhiteSpace(props?.AlbumTitle) ? null : props!.AlbumTitle);
    }
}
