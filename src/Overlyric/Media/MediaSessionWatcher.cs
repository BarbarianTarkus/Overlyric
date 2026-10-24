using Windows.Media.Control;

namespace Overlyric.Media;

/// <summary>
/// Watches Windows' Global System Media Transport Controls (GSMTC) and publishes what is
/// playing. This is the piece that makes Overlyric player-agnostic: any application that
/// integrates with the Windows media controls (TIDAL, Spotify, browsers, VLC, MusicBee,
/// foobar2000, the built-in Media Player, ...) shows up here with no per-app integration.
/// </summary>
/// <remarks>
/// <para>
/// Polling strategy matters for memory. <c>TryGetMediaPropertiesAsync</c> is by far the most
/// expensive call in the loop, and at several calls a second its allocations dominate the managed
/// heap long before the garbage collector bothers to run, which shows up as a steadily climbing
/// working set. It only needs to run when the <em>track</em> changes, so it is gated on the track
/// duration changing - a signal that is cheap to read every poll and cannot change within a track -
/// plus a slow safety net for players that behave unusually.
/// </para>
/// <para>
/// Which player is followed is <see cref="SourceSelector"/>'s decision, taken afresh on every poll
/// from the settings the caller supplies: enumerate the sessions, reduce each one to a
/// <see cref="SourceCandidate"/>, then run the per-track logic below against whichever session the
/// selector names. That logic is deliberately the same code as before a choice existed, and with
/// nothing configured the session used is still exactly <c>GetCurrentSession()</c> - see
/// <see cref="SelectSession"/> for why the default case is taken literally instead of being
/// reconstructed from the candidate list.
/// </para>
/// <para>
/// The class is internal because the selector's settings and candidate types are internal: a public
/// surface would have to publish them, and nothing outside this assembly consumes the watcher.
/// </para>
/// </remarks>
internal sealed class MediaSessionWatcher : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1000);

    /// <summary>Backstop for players that change track without any observable signal.</summary>
    private static readonly TimeSpan PropertyRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly GlobalSystemMediaTransportControlsSessionManager _manager;
    private readonly Func<SourceSettings> _readSettings;
    private readonly PlaybackClock _clock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pollTask;

    private NowPlayingSnapshot _snapshot = NowPlayingSnapshot.Empty;
    private IReadOnlyList<SourceCandidate> _liveSources = [];

    /// <summary>
    /// Position and stamp each session showed on the previous poll, keyed the same way as
    /// <see cref="SourceCandidate.Key"/>. It exists only to answer "did this player's timeline move
    /// since we last looked", which is the selector's tie-break between sessions of one application;
    /// it is kept separate from the track-change state below so that watching several players cannot
    /// perturb the clock handling of the one being followed.
    /// </summary>
    private readonly Dictionary<string, (TimeSpan Position, DateTimeOffset Updated)> _previousTimelines =
        new(StringComparer.Ordinal);

    private string _lastTitle = "";
    private string _lastArtist = "";
    private string _lastSource = "";
    private string? _lastAlbum;

    private TimeSpan _lastReportedPosition = TimeSpan.MinValue;
    private TimeSpan _lastEndTime = TimeSpan.MinValue;
    private DateTimeOffset _lastUpdated = DateTimeOffset.MinValue;

    private long _revision;
    private bool _haveProperties;
    private long _propertyRefreshes;
    private DateTimeOffset _lastPropertyRefresh = DateTimeOffset.MinValue;
    private volatile bool _resetBaseline;

    private MediaSessionWatcher(
        GlobalSystemMediaTransportControlsSessionManager manager,
        Func<SourceSettings> readSettings)
    {
        _manager = manager;
        _readSettings = readSettings;
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    /// <summary>Acquires the Windows media session manager. Works from any thread; GSMTC is agile.</summary>
    /// <param name="readSettings">
    /// Called on the polling thread once per poll, so a change made in the settings window or the tray
    /// menu takes effect on the next tick without a restart. It must hand back a view that is safe to
    /// enumerate while the UI thread may still be editing the configuration.
    /// </param>
    public static async Task<MediaSessionWatcher> CreateAsync(Func<SourceSettings> readSettings)
    {
        ArgumentNullException.ThrowIfNull(readSettings);

        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return new MediaSessionWatcher(manager, readSettings);
    }

    /// <summary>The adaptive position source that synced lyrics are timed against.</summary>
    public PlaybackClock Clock => _clock;

    /// <summary>Latest snapshot. Cheap to read from the UI thread; safe to call often.</summary>
    public NowPlayingSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Every player that was publishing on the last poll, in the order Windows lists them, with the
    /// facts the source menus and the selector need. Replaced wholesale each poll and read with a
    /// volatile load, like <see cref="Snapshot"/>, so a menu can be built from it on the UI thread
    /// without touching the polling thread's state.
    /// </summary>
    /// <remarks>
    /// The list is a snapshot in time: the poll keeps running while a tray menu is open, so a caller
    /// that shows one entry per element has to keep the list it built the menu from and act on that,
    /// not re-read this property when the click arrives.
    /// <para>
    /// <see cref="SourceCandidate.IsSystemCurrent"/> is approximate when two sessions share one app
    /// user model id - two browser tabs - because Windows re-creates the projected session object on
    /// every call, leaving the app user model id as the only correlation there is; the marker names
    /// the first session carrying Windows' current id. That is the one approximation in the rule, and
    /// it is also why the automatic path never relies on the marker. It reaches the selector only
    /// through its SystemCurrent step, which runs when a pin or a priority list is configured and
    /// nothing in it matches.
    /// </para>
    /// </remarks>
    public IReadOnlyList<SourceCandidate> LiveSources => Volatile.Read(ref _liveSources);

    /// <summary>Number of times the expensive media-properties call has been made. Diagnostic only.</summary>
    public long PropertyRefreshCount => Interlocked.Read(ref _propertyRefreshes);

    /// <summary>Raised on the polling thread whenever the snapshot changes.</summary>
    public event Action<NowPlayingSnapshot>? Changed;

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        // Force the next poll to treat whatever is now current as a fresh track.
        _resetBaseline = true;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync();
            }
            catch (Exception)
            {
                // A player can disappear between calls; a failed poll is not fatal.
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(ct))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync()
    {
        // Windows' own answer is asked for first, in the same order the poll has always made its
        // calls, because it is the answer the automatic setting uses.
        var current = _manager.GetCurrentSession();

        IReadOnlyList<GlobalSystemMediaTransportControlsSession>? sessions;
        try
        {
            sessions = _manager.GetSessions();
        }
        catch (Exception)
        {
            // Enumeration is the one call the poll makes for the menus rather than for playback. It
            // is in-process and cheap, but it must not take playback down with it: the automatic
            // setting asks GetCurrentSession() directly, so only this poll's candidate list is lost.
            sessions = null;
        }

        var candidates = new List<SourceCandidate>();
        var sessionsByKey = new List<GlobalSystemMediaTransportControlsSession>();

        if (sessions is not null)
        {
            BuildCandidates(sessions, current, candidates, sessionsByKey);
        }

        Volatile.Write(ref _liveSources, candidates);

        var settings = _readSettings();
        var session = SelectSession(candidates, sessionsByKey, settings, current);

        if (session is null)
        {
            // Nothing to follow - no player publishing at all, or no candidate the settings allow.
            // This is the same answer, and the same bookkeeping, as a null current session.
            _haveProperties = false;
            Publish(NowPlayingSnapshot.Empty);
            return;
        }

        var source = session.SourceAppUserModelId ?? "";
        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();

        var isPlaying =
            playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        // A track's duration never changes while it is playing, so a change here means a new track.
        // This is a plain property read, unlike the asynchronous media-properties call.
        var durationChanged = timeline.EndTime != _lastEndTime;
        var baselineReset = _resetBaseline;
        _resetBaseline = false;

        if (!IsAutomatic(settings))
        {
            // With a pinned or prioritised source, Windows changing its own current session is not a
            // reason to reset the clock: the session being followed has not moved. Its own track
            // changes are caught by the duration and source checks below, exactly as they are for
            // any other player. Automatic mode keeps the event's answer untouched, which is what
            // makes the default path identical to what it was before a source could be chosen.
            baselineReset = false;
        }

        var now = DateTimeOffset.UtcNow;
        var needProperties =
            !_haveProperties
            || baselineReset
            || durationChanged
            || !string.Equals(source, _lastSource, StringComparison.Ordinal)
            || now - _lastPropertyRefresh >= PropertyRefreshInterval;

        var title = _lastTitle;
        var artist = _lastArtist;
        var album = _lastAlbum;
        var trackChanged = baselineReset;

        if (needProperties)
        {
            var properties = await session.TryGetMediaPropertiesAsync();

            title = properties?.Title ?? "";
            artist = properties?.Artist ?? "";
            album = properties?.AlbumTitle;

            trackChanged |=
                !string.Equals(title, _lastTitle, StringComparison.Ordinal)
                || !string.Equals(artist, _lastArtist, StringComparison.Ordinal);

            _lastTitle = title;
            _lastArtist = artist;
            _lastAlbum = album;
            _haveProperties = true;
            _lastPropertyRefresh = now;
            Interlocked.Increment(ref _propertyRefreshes);
        }

        if (trackChanged || !string.Equals(source, _lastSource, StringComparison.Ordinal))
        {
            _lastSource = source;
            _clock.ResetForNewTrack(timeline.Position, timeline.LastUpdatedTime, isPlaying);
            _revision++;
            _lastReportedPosition = timeline.Position;
            _lastUpdated = timeline.LastUpdatedTime;
        }
        else
        {
            var timelineAdvanced =
                timeline.Position != _lastReportedPosition
                || timeline.LastUpdatedTime != _lastUpdated;

            _lastReportedPosition = timeline.Position;
            _lastUpdated = timeline.LastUpdatedTime;

            _clock.Observe(timeline.Position, timeline.LastUpdatedTime, timelineAdvanced, isPlaying);
        }

        _lastEndTime = timeline.EndTime;

        double? duration = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime.TotalSeconds : null;

        Publish(new NowPlayingSnapshot
        {
            HasSession = true,
            SourceApp = source,
            Title = title,
            Artist = artist,
            Album = string.IsNullOrWhiteSpace(album) ? null : album,
            DurationSeconds = duration,
            IsPlaying = isPlaying,
            TimelineIsLive = _clock.TimelineIsLive,
            Revision = _revision
        });
    }

    /// <summary>
    /// Reduces the sessions Windows is holding to the facts the selector and the menus need, and
    /// fills <paramref name="sessionsByKey"/> so the key the selector picked can be turned back into
    /// the session to follow.
    /// </summary>
    /// <remarks>
    /// Every read here is guarded: a player can exit between the enumeration and the read, and one
    /// unreadable session must not cost the whole poll - it is simply not a candidate any more. The
    /// calls are cheap synchronous properties; the expensive asynchronous media-properties call is
    /// still gated in <see cref="PollOnceAsync"/>.
    /// </remarks>
    private void BuildCandidates(
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions,
        GlobalSystemMediaTransportControlsSession? current,
        List<SourceCandidate> candidates,
        List<GlobalSystemMediaTransportControlsSession> sessionsByKey)
    {
        var currentAumid = AumidOf(current);
        var currentIndex = -1;
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new List<string>(sessions.Count);

        foreach (var session in sessions)
        {
            string aumid;
            GlobalSystemMediaTransportControlsSessionPlaybackInfo playback;
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline;

            try
            {
                aumid = session.SourceAppUserModelId ?? "";
                playback = session.GetPlaybackInfo();
                timeline = session.GetTimelineProperties();
            }
            catch (Exception)
            {
                // The session closed between the enumeration and the read. It is not a candidate, and
                // the next poll will not list it at all.
                continue;
            }

            // Keys are per poll and per occurrence. Windows offers no stable session id, so two
            // sessions sharing one app user model id - two browser tabs - are told apart by their
            // order in the list. That is stable enough to choose with and never stable enough to
            // persist a preference against, which is why the config stores the app id alone.
            var ordinal = ordinals.GetValueOrDefault(aumid);
            ordinals[aumid] = ordinal + 1;
            var key = ordinal == 0 ? aumid : $"{aumid}#{ordinal}";
            seen.Add(key);

            if (currentIndex < 0
                && aumid.Length > 0
                && string.Equals(aumid, currentAumid, StringComparison.Ordinal))
            {
                currentIndex = candidates.Count;
            }

            // "Moved since the previous poll" is evidence rather than a guess: a key seen for the
            // first time has no previous poll to compare against and starts as not advancing.
            var advances =
                _previousTimelines.TryGetValue(key, out var previous)
                && (previous.Position != timeline.Position || previous.Updated != timeline.LastUpdatedTime);

            _previousTimelines[key] = (timeline.Position, timeline.LastUpdatedTime);

            candidates.Add(new SourceCandidate(
                key,
                aumid,
                NowPlayingSnapshot.FriendlyNameFor(aumid),
                IsSystemCurrent: false,
                IsPlaying: playback.PlaybackStatus
                          == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                PositionAdvances: advances));

            sessionsByKey.Add(session);
        }

        // The current session is recognised by its app user model id, not by reference: Windows
        // re-creates the projected session object on every call (measured with a scratch probe,
        // ReferenceEquals between GetCurrentSession() and the matching entry of GetSessions() is
        // false). With two sessions of one application this marks the first of them, which is as far
        // as Windows' API allows. That approximation reaches the selector through its SystemCurrent
        // step - configured, but neither the pin nor the list matched anything - and is why the
        // automatic path never relies on this flag.
        if (currentIndex >= 0)
        {
            candidates[currentIndex] = candidates[currentIndex] with { IsSystemCurrent = true };
        }

        // Forget sessions that are gone, so a player that exits and returns is judged against a fresh
        // timeline instead of one from minutes ago.
        if (_previousTimelines.Count > 0)
        {
            List<string>? stale = null;

            foreach (var key in _previousTimelines.Keys)
            {
                if (!seen.Contains(key))
                {
                    (stale ??= []).Add(key);
                }
            }

            if (stale is not null)
            {
                foreach (var key in stale)
                {
                    _previousTimelines.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// The session to follow: Windows' own answer when nothing is configured, the selector's when
    /// something is. Null means nothing is to be followed.
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? SelectSession(
        IReadOnlyList<SourceCandidate> candidates,
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessionsByKey,
        SourceSettings settings,
        GlobalSystemMediaTransportControlsSession? current)
    {
        // With nothing configured the rule is the one this app has always had - follow
        // GetCurrentSession() - and it is taken literally rather than reconstructed from the
        // candidate list. That is not an optimisation: the projected session objects are recreated
        // on every call, so the candidate list can only recognise the current session by its app
        // user model id, and two sessions of one application would make that guess ambiguous. Asking
        // Windows directly is exact, costs one call, and keeps the default path byte-for-byte what it
        // was. The selector still decides everything as soon as the user configures a pin or a list.
        if (IsAutomatic(settings))
        {
            return current;
        }

        var choice = SourceSelector.Choose(candidates, settings);

        if (!choice.HasChoice)
        {
            return null;
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (string.Equals(candidates[i].Key, choice.Key, StringComparison.Ordinal))
            {
                return sessionsByKey[i];
            }
        }

        // The chosen session closed between being described and being used. The next poll settles it.
        return null;
    }

    /// <summary>
    /// True when the user has expressed no preference at all. Both halves are normalised when the
    /// config is loaded, so an empty pin is empty rather than whitespace and a cleared list is empty
    /// rather than a row of blanks.
    /// </summary>
    private static bool IsAutomatic(SourceSettings settings) =>
        settings.Priority.Count == 0 && string.IsNullOrWhiteSpace(settings.PinnedAumid);

    /// <summary>
    /// App user model id of a session, or empty when it cannot be read - a session that closed
    /// between being named and being described has no id left to report.
    /// </summary>
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

    private void Publish(NowPlayingSnapshot snapshot)
    {
        var previous = Interlocked.Exchange(ref _snapshot, snapshot);
        if (!AreEquivalent(previous, snapshot))
        {
            Changed?.Invoke(snapshot);
        }
    }

    /// <summary>
    /// Compares everything except the live timeline flag, which flips on its own as
    /// liveness detection warms up and would otherwise cause a redundant notification.
    /// </summary>
    private static bool AreEquivalent(NowPlayingSnapshot a, NowPlayingSnapshot b) =>
        a.HasSession == b.HasSession
        && a.IsPlaying == b.IsPlaying
        && a.Revision == b.Revision
        && string.Equals(a.Title, b.Title, StringComparison.Ordinal)
        && string.Equals(a.Artist, b.Artist, StringComparison.Ordinal)
        && string.Equals(a.SourceApp, b.SourceApp, StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        _manager.CurrentSessionChanged -= OnCurrentSessionChanged;

        await _cts.CancelAsync();

        try
        {
            await _pollTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }

        _cts.Dispose();
    }
}
