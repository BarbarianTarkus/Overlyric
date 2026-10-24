using Windows.Media.Control;

namespace Overlyric.Media;

/// <summary>
/// Watches Windows' Global System Media Transport Controls (GSMTC) and publishes what is
/// playing. This is the piece that makes Overlyric player-agnostic: any application that
/// integrates with the Windows media controls (TIDAL, Spotify, browsers, VLC, MusicBee,
/// foobar2000, the built-in Media Player, ...) shows up here with no per-app integration.
/// </summary>
/// <remarks>
/// Polling strategy matters for memory. <c>TryGetMediaPropertiesAsync</c> is by far the most
/// expensive call in the loop, and at several calls a second its allocations dominate the managed
/// heap long before the garbage collector bothers to run, which shows up as a steadily climbing
/// working set. It only needs to run when the <em>track</em> changes, so it is gated on the track
/// duration changing - a signal that is cheap to read every poll and cannot change within a track -
/// plus a slow safety net for players that behave unusually.
/// </remarks>
public sealed class MediaSessionWatcher : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1000);

    /// <summary>Backstop for players that change track without any observable signal.</summary>
    private static readonly TimeSpan PropertyRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly GlobalSystemMediaTransportControlsSessionManager _manager;
    private readonly PlaybackClock _clock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pollTask;

    private NowPlayingSnapshot _snapshot = NowPlayingSnapshot.Empty;

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

    private MediaSessionWatcher(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        _manager = manager;
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    /// <summary>Acquires the Windows media session manager. Works from any thread; GSMTC is agile.</summary>
    public static async Task<MediaSessionWatcher> CreateAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return new MediaSessionWatcher(manager);
    }

    /// <summary>The adaptive position source that synced lyrics are timed against.</summary>
    public PlaybackClock Clock => _clock;

    /// <summary>Latest snapshot. Cheap to read from the UI thread; safe to call often.</summary>
    public NowPlayingSnapshot Snapshot => Volatile.Read(ref _snapshot);

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
        var session = _manager.GetCurrentSession();
        if (session is null)
        {
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
