using System.Diagnostics;
using System.Globalization;
using Overlyric.Interop;
using Overlyric.Lyrics;
using Overlyric.Media;
using Overlyric.Overlay;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.App;

/// <summary>
/// Application coordinator. Owns the message loop and, on every tick, turns the current media
/// snapshot plus playback position into an <see cref="OverlayVisual"/> and redraws only when
/// something actually changed.
/// </summary>
internal sealed class AppHost
{
    private const string UserAgent = "Overlyric/0.1.0 (https://github.com/overlyric/overlyric)";
    private const uint TimerId = 1;
    private const uint FastIntervalMs = 250;
    private const uint SlowIntervalMs = 1000;
    private const int OffsetStepMs = 250;

    /// <summary>Ticks between z-order re-assertions; 20 fast ticks is roughly every five seconds.</summary>
    private const int TopmostReassertTicks = 20;

    private const string InstrumentalStatus = "Instrumental";
    private const string NoLyricsStatus = "No lyrics found";

    /// <summary>
    /// Shown instead of a lyric line when the player publishes no playback position at all. Pretending
    /// otherwise means parking on the first line for the whole song, which reads as broken lyrics
    /// rather than as a player that never said where it was.
    /// </summary>
    private const string NoPositionStatus = "Player is not reporting a playback position";

    /// <summary>
    /// How many times to re-ask when a lookup came back empty. A definitive miss is tombstoned and
    /// answers from the cache instantly, so these cost a network round-trip only when lrclib was
    /// genuinely unreachable - which, given its throttling, is the common reason for an empty answer.
    /// </summary>
    private const int MaxLyricsRetries = 2;

    private static readonly int[] RetryDelaysMs = [15_000, 45_000];

    private readonly AppConfig _config;
    private readonly MessageWindow _messageWindow = new();
    private readonly Lock _gate = new();
    private readonly PlayerWarningWatch _playerWarning = new();
    private readonly bool _demo;
    private readonly Stopwatch _demoClock = Stopwatch.StartNew();

    private HotKeyManager? _hotKeys;
    private TrayIcon? _tray;
    private OverlayWindow? _overlay;
    private MediaSessionWatcher? _watcher;
    private LyricsService? _lyrics;

    private LyricLine[] _lines = [];
    private bool _lyricsSynced;
    private string? _lyricsStatus;
    private bool _isInstrumental;
    private bool _fetchCompleted;
    private int _lyricsRetryAttempt;
    private long _nextLyricsRetryAt;
    private long _lyricsRevision = -1;

    private CancellationTokenSource? _fetchCts;
    private OverlayVisual _visual = new();
    private long _lastRevision = -1;
    private NowPlayingSnapshot _snapshot = NowPlayingSnapshot.Empty;
    private IReadOnlyList<LyricsCandidate> _candidates = [];
    private long _candidatesRevision = -1;
    private uint _currentInterval;
    private bool _overlayEnabled = true;
    private bool _locked = true;
    private string? _lastTooltip;
    private int _topmostTicks;

    private static readonly NowPlayingSnapshot DemoSnapshot = new()
    {
        HasSession = true,
        SourceApp = "Overlyric.Demo",
        Title = "Demo Track",
        Artist = "Overlyric",
        Album = "Preview",
        DurationSeconds = 36,
        IsPlaying = true,
        TimelineIsLive = true,
        Revision = 1
    };

    /// <summary>Canned, synced lyrics used by <c>--demo</c>. Doubles as in-app documentation.</summary>
    private static readonly LyricLine[] DemoLines =
    [
        new(TimeSpan.FromSeconds(0.0), "Overlyric demo mode"),
        new(TimeSpan.FromSeconds(3.0), "No plugin for any player"),
        new(TimeSpan.FromSeconds(6.5), "TIDAL, Spotify or a browser"),
        new(TimeSpan.FromSeconds(10.0), "Windows says what is playing"),
        new(TimeSpan.FromSeconds(13.5), "And we paint it on the glass"),
        new(TimeSpan.FromSeconds(17.0), "Ctrl+Alt+M changes the layout"),
        new(TimeSpan.FromSeconds(20.5), "Ctrl+Alt+L lets you drag me"),
        new(TimeSpan.FromSeconds(24.0), "Ctrl+Alt+comma nudges the timing"),
        new(TimeSpan.FromSeconds(27.5), "Ctrl+Alt+H hides the overlay"),
        new(TimeSpan.FromSeconds(31.0), "This is only a preview")
    ];

    private TimeSpan DemoPosition()
    {
        var total = DemoLines[^1].Start + TimeSpan.FromSeconds(5);
        return TimeSpan.FromTicks(_demoClock.Elapsed.Ticks % total.Ticks);
    }

    public AppHost(AppConfig config, bool demo = false)
    {
        _config = config;
        _demo = demo;
    }

    public int Run()
    {
        _messageWindow.Create();
        _messageWindow.SetHandler(OnMessage);

        _hotKeys = new HotKeyManager(_messageWindow.Handle);
        _hotKeys.Pressed += OnHotKey;

        _tray = new TrayIcon(_messageWindow.Handle);
        _tray.LeftClicked += ToggleOverlay;
        _tray.MenuRequested += ShowTrayMenu;
        _tray.Show("Overlyric - waiting for playback");

        _overlay = new OverlayWindow(_config.ToTheme());
        _overlay.Create(_config.Mode, _config.Anchor, _config.OffsetX, _config.OffsetY, MonitorUnderCursor());
        RepairLegacyPlacement();

        _locked = _config.Locked;
        _overlay.SetLocked(_locked);

        _overlay.OpacityScrolled += OnOpacityScrolled;
        _overlay.Moved += OnOverlayMoved;

        if (_demo)
        {
            // Preview mode: drive the overlay from a canned synced track so the rendering
            // path can be exercised without anything playing.
            _overlay.SetVisible(true);

            lock (_gate)
            {
                _lines = DemoLines;
                _lyricsSynced = true;
                _lyricsStatus = null;
                _fetchCompleted = true;
                _lyricsRevision = DemoSnapshot.Revision;
            }
        }
        else
        {
            _watcher = MediaSessionWatcher.CreateAsync().GetAwaiter().GetResult();
            _watcher.Clock.ManualOffset = TimeSpan.FromMilliseconds(_config.LyricsOffsetMs);

            var overrides = VersionOverrides.Load(Path.Combine(AppConfig.DataDirectory, "versions.json"));

            _lyrics = new LyricsService(
                new LrclibClient(UserAgent),
                new LyricsCache(AppConfig.CacheDirectory),
                overrides);
        }

        RegisterHotKeys();

        _currentInterval = SlowIntervalMs;
        SetTimer(_messageWindow.Handle, TimerId, _currentInterval, 0);

        _messageWindow.Show();

        return RunMessageLoop();
    }

    private static int RunMessageLoop()
    {
        while (true)
        {
            var result = GetMessageW(out var message, nint.Zero, 0, 0);
            if (result <= 0)
            {
                return result;
            }

            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    private void RegisterHotKeys()
    {
        if (_hotKeys is null)
        {
            return;
        }

        const uint CtrlAlt = MOD_CONTROL | MOD_ALT;

        _hotKeys.Register(Commands.ToggleLock, CtrlAlt, 0x4C);        // Ctrl+Alt+L
        _hotKeys.Register(Commands.ToggleOverlay, CtrlAlt, 0x48);     // Ctrl+Alt+H
        _hotKeys.Register(Commands.ToggleMode, CtrlAlt, 0x4D);        // Ctrl+Alt+M
        _hotKeys.Register(Commands.OffsetBack, CtrlAlt, 0xBC);        // Ctrl+Alt+,
        _hotKeys.Register(Commands.OffsetForward, CtrlAlt, 0xBE);     // Ctrl+Alt+.
        _hotKeys.Register(Commands.OffsetReset, CtrlAlt, 0x30);       // Ctrl+Alt+0
    }

    private void OnMessage(uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_TIMER when wParam == TimerId:
                OnTick();
                break;

            case WM_HOTKEY:
                _hotKeys?.HandleMessage(wParam);
                break;

            case WM_DESTROY:
                PostQuitMessage(0);
                break;

            default:
                if (_tray is not null && message == TrayIcon.Message)
                {
                    _tray.HandleMessage(lParam);
                }

                break;
        }
    }

    private void OnTick()
    {
        if (_overlay is null)
        {
            return;
        }

        var snapshot = _demo ? DemoSnapshot : _watcher?.Snapshot ?? NowPlayingSnapshot.Empty;
        var position = _demo ? DemoPosition() : _watcher?.Clock.Position ?? TimeSpan.Zero;
        var positionIsUsable = _demo || _watcher is null || _watcher.Clock.PositionIsUsable;
        _snapshot = snapshot;

        UpdatePlayerWarning(snapshot, positionIsUsable);

        if (snapshot.Revision != _lastRevision)
        {
            _lastRevision = snapshot.Revision;

            if (!_demo)
            {
                _lyricsRetryAttempt = 0;
                _nextLyricsRetryAt = 0;
                StartLyricsFetch(snapshot);
            }
        }
        else if (!_demo && ShouldRetryLyrics())
        {
            _lyricsRetryAttempt++;
            StartLyricsFetch(snapshot);
        }

        var visual = BuildVisual(snapshot, position, positionIsUsable);
        if (!VisualEquals(_visual, visual))
        {
            _visual = visual;
            _overlay.SetVisual(visual);

            if (IsOverlayVisible(snapshot))
            {
                _overlay.Render();
            }
        }

        UpdateVisibility(snapshot);
        UpdateTimerInterval(snapshot);
    }

    private OverlayVisual BuildVisual(NowPlayingSnapshot snapshot, TimeSpan position, bool positionIsUsable)
    {
        if (!snapshot.HasTrack)
        {
            return new OverlayVisual { Mode = _config.Mode, Status = null };
        }

        LyricLine[] lines;
        bool synced;
        string? status;
        bool completed;

        lock (_gate)
        {
            lines = _lines;
            synced = _lyricsSynced;
            status = _lyricsStatus;
            completed = _fetchCompleted;
        }

        var header = _config.ShowHeader ? BuildHeader(snapshot) : null;

        var trackProgress = Quantise(ResolveTrackProgress(position, snapshot.DurationSeconds));
        var lineProgress = -1f;

        if (!completed)
        {
            // Still resolving lyrics: keep the overlay blank rather than flashing a placeholder.
            return new OverlayVisual
            {
                Mode = _config.Mode,
                Lines = [],
                ActiveIndex = -1,
                Header = header,
                ShowHeader = false
            };
        }

        if (lines.Length == 0)
        {
            return new OverlayVisual
            {
                Mode = _config.Mode,
                Lines = [],
                ActiveIndex = -1,
                Header = header,
                ShowHeader = false,
                Status = status,
                ShowProgressBar = false
            };
        }

        var count = _config.Mode == OverlayMode.SingleLine ? 1 : Math.Clamp(_config.MultiLineCount, 1, lines.Length);

        if (!positionIsUsable && snapshot.IsPlaying)
        {
            // The player never said where it is, so every line we could pick would be a guess - and
            // guessing the first line holds the song at its opening for its whole length.
            return new OverlayVisual
            {
                Mode = _config.Mode,
                Lines = [],
                ActiveIndex = -1,
                Header = header,
                ShowHeader = _config.ShowHeader,
                Status = NoPositionStatus,
                ShowProgressBar = false
            };
        }

        if (!synced)
        {
            // Unsynced lyrics have no timings, so scroll them proportionally instead of pretending.
            if (_config.Mode == OverlayMode.SingleLine)
            {
                return new OverlayVisual
                {
                    Mode = OverlayMode.SingleLine,
                    Lines = [],
                    ActiveIndex = -1,
                    Header = header,
                    ShowHeader = _config.ShowHeader,
                    Status = "Lyrics not synced"
                };
            }

            var offset = Math.Clamp(
                (int)(Math.Max(trackProgress, 0f) * Math.Max(1, lines.Length - count)),
                0,
                Math.Max(0, lines.Length - count));

            var slice = new string[Math.Min(count, lines.Length)];
            for (var i = 0; i < slice.Length; i++)
            {
                slice[i] = lines[offset + i].Text;
            }

            return new OverlayVisual
            {
                Mode = OverlayMode.MultiLine,
                Lines = slice,
                ActiveIndex = -1,
                Header = header,
                ShowHeader = _config.ShowHeader,
                ShowProgressBar = _config.ShowProgressBar,
                TrackProgress = trackProgress
            };
        }

        var index = FindActiveLine(lines, position);
        if (index < 0)
        {
            index = 0;
        }

        if (index + 1 < lines.Length)
        {
            var span = lines[index + 1].Start - lines[index].Start;
            if (span > TimeSpan.Zero)
            {
                var elapsed = position - lines[index].Start;
                lineProgress = Quantise((float)Math.Clamp(elapsed / span, 0.0, 1.0));
            }
        }

        var visible = new string[count];
        var windowStart = index - (count / 2);
        windowStart = Math.Clamp(windowStart, 0, Math.Max(0, lines.Length - count));

        for (var i = 0; i < count; i++)
        {
            visible[i] = lines[windowStart + i].Text;
        }

        return new OverlayVisual
        {
            Mode = _config.Mode,
            Lines = visible,
            ActiveIndex = index - windowStart,
            Header = header,
            ShowHeader = _config.ShowHeader,
            ShowProgressBar = _config.ShowProgressBar,
            LineProgress = lineProgress,
            TrackProgress = trackProgress
        };
    }

    /// <summary>
    /// Header text for the overlay. The title is cleaned first, because a player can report an entire
    /// movement listing as the track name - TIDAL does exactly that for "Song of Scheherazade" - which
    /// would otherwise stretch the header across the whole screen.
    /// </summary>
    private static string? BuildHeader(NowPlayingSnapshot snapshot)
    {
        var title = MetadataNormaliser.CleanTitle(snapshot.Title);

        var text = string.IsNullOrWhiteSpace(snapshot.Artist)
            ? title
            : $"{snapshot.Artist} \u2014 {title}";

        text = text.Trim(' ', '\u2014');

        return text.Length == 0 ? null : Truncate(text, 80);
    }

    private static float ResolveTrackProgress(TimeSpan position, double? durationSeconds)
    {
        if (durationSeconds is not > 0)
        {
            return -1f;
        }

        return (float)Math.Clamp(position.TotalSeconds / durationSeconds.Value, 0.0, 1.0);
    }

    /// <summary>Snap to 1/200 so a moving progress bar does not force a redraw every tick.</summary>
    private static float Quantise(float value) =>
        value < 0f ? -1f : MathF.Round(value * 200f) / 200f;

    private static int FindActiveLine(LyricLine[] lines, TimeSpan position)
    {
        var low = 0;
        var high = lines.Length - 1;
        var result = -1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (lines[mid].Start <= position)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    private bool IsOverlayVisible(NowPlayingSnapshot snapshot)
    {
        if (!_overlayEnabled || !snapshot.HasTrack)
        {
            return false;
        }

        if (_config.HideWhenPaused && !snapshot.IsPlaying)
        {
            return false;
        }

        lock (_gate)
        {
            if (_fetchCompleted && _lines.Length == 0)
            {
                // "This track is instrumental" and "no lyrics could be found" are different answers
                // and are hidden independently.
                var hide = _isInstrumental ? _config.HideWhenInstrumental : _config.HideWhenNoLyrics;

                if (hide)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Tells the user when their player is not publishing what Overlyric needs - either nothing at
    /// all, or a track without a position. Neither is Overlyric's fault and neither is visible
    /// otherwise: the overlay simply never appears, or holds the first line for the whole song.
    /// </summary>
    private void UpdatePlayerWarning(NowPlayingSnapshot snapshot, bool positionIsUsable)
    {
        if (!_config.WarnWhenPlayerDoesNotPublish)
        {
            _playerWarning.Reset();
            return;
        }

        _playerWarning.Update(snapshot, positionIsUsable);

        if (!_playerWarning.IsNewSituation || _tray is null)
        {
            return;
        }

        var source = _playerWarning.SourceApp ?? "the player";

        var (title, text) = _playerWarning.Kind switch
        {
            PlayerWarningKind.NothingPublishes => (
                "Overlyric - no player is publishing",
                "Audio is playing, but no player is publishing to Windows media controls, the only thing "
                + "Overlyric can read. Check whether your player supports them - and if it does not, look "
                + "for a plug-in that adds them: AIMP, for example, has one."),
            _ => (
                $"Overlyric - {source} is not reporting a position",
                $"{source} is publishing a track but no playback position, so the lyrics cannot follow the "
                + "music. Check whether your player - or its plug-in - publishes the position as well.")
        };

        _tray.Alert(title, text);
    }

    /// <summary>One line for the tray tooltip, or null when there is nothing to warn about.</summary>
    private string? PlayerWarningTooltip() => _playerWarning.Kind switch
    {
        PlayerWarningKind.NothingPublishes =>
            "audio is playing, but no player publishes to Windows media controls",
        PlayerWarningKind.NoPlaybackPosition =>
            $"{_playerWarning.SourceApp} reports no playback position",
        _ => null
    };

    /// <summary>The longer explanation, which only the tray menu has room for.</summary>
    private IReadOnlyList<TrayMenuItem> PlayerWarningMenuItems()
    {
        switch (_playerWarning.Kind)
        {
            case PlayerWarningKind.NothingPublishes:
                return
                [
                    TrayMenuItem.Disabled("Audio is playing, but no player publishes to Windows media controls"),
                    TrayMenuItem.Disabled("Overlyric reads only that API - look for support, or a plug-in (AIMP has one)")
                ];

            case PlayerWarningKind.NoPlaybackPosition:
                return
                [
                    TrayMenuItem.Disabled($"{_playerWarning.SourceApp} publishes the track but no playback position"),
                    TrayMenuItem.Disabled("Synced lyrics cannot follow it - check the player or its plug-in")
                ];

            default:
                return [];
        }
    }

    private void UpdateVisibility(NowPlayingSnapshot snapshot)
    {
        if (_overlay is null)
        {
            return;
        }

        UpdateTooltip(snapshot);

        var visible = IsOverlayVisible(snapshot);
        _overlay.SetVisible(visible);

        if (!visible)
        {
            return;
        }

        // Full-screen applications sometimes displace other topmost windows, so re-asserting the
        // z-order is worthwhile insurance - but a few times a minute, not several times a second.
        if (++_topmostTicks >= TopmostReassertTicks)
        {
            _topmostTicks = 0;
            _overlay.ReassertTopmost();
        }
    }

    /// <summary>
    /// Tray tooltip: what is playing, and - when the player is not giving Overlyric what it needs -
    /// why the overlay is quiet.
    /// <para>
    /// Shell_NotifyIcon is a comparatively heavy call; only talk to the shell when the text actually
    /// differs, otherwise it runs on every tick and churns memory for nothing.
    /// </para>
    /// </summary>
    private void UpdateTooltip(NowPlayingSnapshot snapshot)
    {
        if (_tray is null)
        {
            return;
        }

        // The tray is the only place the lock state shows, and unlocking is the one state in which
        // the overlay takes the mouse, so it is worth naming there.
        var header = _locked ? "Overlyric" : "Overlyric (unlocked - draggable)";

        var text = snapshot.HasTrack
            ? $"{header}\n{snapshot.SourceAppFriendly}: {snapshot.TrackDescription}"
            : header;

        if (PlayerWarningTooltip() is { } warning)
        {
            text += $"\n{warning}";
        }

        if (!string.Equals(text, _lastTooltip, StringComparison.Ordinal))
        {
            _lastTooltip = text;
            _tray.SetTooltip(text);
        }
    }

    private void UpdateTimerInterval(NowPlayingSnapshot snapshot)
    {
        var wanted = snapshot.IsPlaying && IsOverlayVisible(snapshot) ? FastIntervalMs : SlowIntervalMs;

        if (wanted == _currentInterval)
        {
            return;
        }

        _currentInterval = wanted;
        SetTimer(_messageWindow.Handle, TimerId, wanted, 0);
    }

    /// <summary>
    /// True when an earlier lookup produced nothing to show and enough time has passed to re-ask.
    /// </summary>
    private bool ShouldRetryLyrics()
    {
        if (_lyricsRetryAttempt >= MaxLyricsRetries || _nextLyricsRetryAt == 0)
        {
            return false;
        }

        if (Environment.TickCount64 < _nextLyricsRetryAt)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_fetchCompleted || _lines.Length > 0 || _isInstrumental)
            {
                return false;
            }
        }

        _nextLyricsRetryAt = 0;
        return true;
    }

    private void StartLyricsFetch(NowPlayingSnapshot snapshot)
    {
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        _fetchCts = new CancellationTokenSource();
        var token = _fetchCts.Token;
        var revision = snapshot.Revision;
        var attempt = _lyricsRetryAttempt;

        lock (_gate)
        {
            _lines = [];
            _lyricsSynced = false;
            _lyricsStatus = null;
            _isInstrumental = false;
            _fetchCompleted = false;
            _lyricsRevision = revision;
        }

        var artist = snapshot.Artist;
        var title = snapshot.Title;
        var album = snapshot.Album;
        var duration = snapshot.DurationSeconds;

        _ = Task.Run(
            async () =>
            {
                LyricsResult? result = null;

                try
                {
                    result = await _lyrics!.GetLyricsAsync(artist, title, album, duration, token)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // LyricsService already swallows network errors; this is a last-resort guard.
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                var needsVersions = false;

                lock (_gate)
                {
                    if (_lyricsRevision != revision)
                    {
                        return;
                    }

                    _fetchCompleted = true;

                    if (result is null)
                    {
                        // lrclib has no record for this track. If the title or album announces it as
                        // an instrumental, that is a better answer than "no lyrics found" - and it
                        // stops the overlay nagging on every play of a wordless track.
                        _lines = [];
                        _lyricsSynced = false;
                        _isInstrumental = InstrumentalDetector.TitleIndicatesInstrumental(title, album);
                        _lyricsStatus = _isInstrumental ? InstrumentalStatus : NoLyricsStatus;
                    }
                    else if (result.IsInstrumental)
                    {
                        _lines = [];
                        _lyricsSynced = false;
                        _isInstrumental = true;
                        _lyricsStatus = InstrumentalStatus;
                    }
                    else
                    {
                        _lines = [.. result.Lines];
                        _lyricsSynced = result.IsSynced;
                        _isInstrumental = false;
                        _lyricsStatus = null;
                    }

                    // Nothing to show: line up another attempt unless lrclib answered definitively.
                    _nextLyricsRetryAt =
                        _lines.Length == 0 && !_isInstrumental && attempt < MaxLyricsRetries
                            ? Environment.TickCount64 + RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length - 1)]
                            : 0;

                    // Missing or untimed lyrics are exactly where a listener is likely to want a
                    // different version, so the alternative list is fetched up front to keep the menu
                    // instant. Good synced lyrics need no extra request.
                    needsVersions = _lines.Length == 0 || !_lyricsSynced;
                }

                if (needsVersions)
                {
                    await LoadVersionsAsync(artist, title, album, duration, revision, token).ConfigureAwait(false);
                }
            },
            token);
    }

    /// <summary>
    /// Fetches the alternative lrclib records for the current track, for the version menu.
    /// </summary>
    private async Task LoadVersionsAsync(
        string artist,
        string title,
        string? album,
        double? durationSeconds,
        long revision,
        CancellationToken ct)
    {
        if (_lyrics is null)
        {
            return;
        }

        try
        {
            var candidates = await _lyrics.GetVersionsAsync(artist, title, album, durationSeconds, ct)
                .ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                return;
            }

            lock (_gate)
            {
                if (_lyricsRevision != revision)
                {
                    return;
                }

                _candidates = candidates;
                _candidatesRevision = revision;
            }
        }
        catch (Exception)
        {
            // Best effort: the menu simply offers to look again.
        }
    }

    private static bool VisualEquals(OverlayVisual a, OverlayVisual b)
    {
        if (a.Mode != b.Mode
            || a.ActiveIndex != b.ActiveIndex
            || a.ShowHeader != b.ShowHeader
            || a.ShowProgressBar != b.ShowProgressBar
            || !string.Equals(a.Header, b.Header, StringComparison.Ordinal)
            || !string.Equals(a.Status, b.Status, StringComparison.Ordinal)
            || a.Lines.Count != b.Lines.Count)
        {
            return false;
        }

        // Progress only warrants a redraw when it is actually being drawn; otherwise a moving
        // position would force a frame every tick for something invisible.
        if (a.ShowProgressBar
            && (a.LineProgress != b.LineProgress || a.TrackProgress != b.TrackProgress))
        {
            return false;
        }

        for (var i = 0; i < a.Lines.Count; i++)
        {
            if (!string.Equals(a.Lines[i], b.Lines[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void OnHotKey(int command) => Execute(command);

    private void ToggleOverlay() => Execute(Commands.ToggleOverlay);

    private void OnOpacityScrolled(int delta)
    {
        var step = delta > 0 ? 0.05 : -0.05;
        _config.BackgroundOpacity = Math.Clamp(_config.BackgroundOpacity + step, 0.15, 1.0);
        ApplyTheme();
        _config.Save();
    }

    private void OnOverlayMoved(int x, int y)
    {
        if (_overlay is null)
        {
            return;
        }

        var monitor = MonitorFromWindow(_overlay.Handle, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info) || !GetWindowRect(_overlay.Handle, out var window))
        {
            return;
        }

        var work = info.rcWork;
        var width = window.Right - window.Left;
        var height = window.Bottom - window.Top;
        var centreY = work.Top + (work.Height / 2);

        _config.Anchor = y > centreY ? OverlayAnchor.BottomCenter : OverlayAnchor.TopCenter;

        // Offsets are stored relative to the anchor - the same shape ApplyBounds reads back - so the
        // overlay reopens exactly where it was left. Storing the window's own coordinates instead
        // meant a bottom-anchored overlay was read as a bottom margin that large and reappeared at
        // the top of the screen, which is precisely the bug this now prevents.
        _config.OffsetX = x - (work.Left + ((work.Width - width) / 2));
        _config.OffsetY = _config.Anchor == OverlayAnchor.BottomCenter
            ? Math.Max(0, work.Bottom - (y + height))
            : Math.Max(0, y - work.Top);

        _config.Save();
    }

    /// <summary>
    /// Lock or unlock, and remember it. Unlocked is the adjustment mode - draggable, and it takes
    /// the wheel - so it is also the only state in which the overlay captures clicks on the lyrics.
    /// </summary>
    private void SetLocked(bool locked)
    {
        _locked = locked;
        _config.Locked = locked;
        _overlay?.SetLocked(locked);
        _config.Save();
    }

    /// <summary>
    /// One-time repair for placements saved by builds that wrote the overlay's raw position where an
    /// offset from the anchor belongs: a bottom-anchored overlay came back near the top of the screen
    /// and every overlay came back half a window to the left. Runs against the window that has just
    /// been created, so the monitor and the size in play are the ones the values will be applied to.
    /// </summary>
    private void RepairLegacyPlacement()
    {
        if (_overlay is null || _config.PlacementVersion >= 1)
        {
            return;
        }

        _config.PlacementVersion = 1;

        var monitor = MonitorFromWindow(_overlay.Handle, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info) || !GetWindowRect(_overlay.Handle, out var window))
        {
            return;
        }

        var work = info.rcWork;
        var width = window.Right - window.Left;
        var height = window.Bottom - window.Top;

        // X: nothing but a drag ever writes this, and every drag wrote the window's left edge
        // measured from the centre of the work area rather than the centred position.
        if (_config.OffsetX != 0)
        {
            _config.OffsetX += width / 2;
        }

        // Y: a bottom-anchored drag wrote the distance from the top of the work area, and a value
        // past the half-way line cannot be a margin from the bottom - a drag that high would have
        // anchored to the top instead. That is also what makes this test safe to repeat.
        if (_config.Anchor == OverlayAnchor.BottomCenter && _config.OffsetY > work.Height / 2)
        {
            _config.OffsetY = Math.Max(0, work.Bottom - (work.Top + _config.OffsetY) - height);
        }

        _overlay.SetPlacement(_config.Anchor, _config.OffsetX, _config.OffsetY, nint.Zero);
        _config.Save();
    }

    private void Execute(int command)
    {
        if (_overlay is null)
        {
            return;
        }

        // Version-menu entries carry their candidate index in the command id.
        if (command >= Commands.SelectVersionBase)
        {
            SelectVersion(command - Commands.SelectVersionBase);
            return;
        }

        switch (command)
        {
            case Commands.ToggleOverlay:
                _overlayEnabled = !_overlayEnabled;

                // Visibility is applied on the tick, so without this the overlay would take up to a
                // second to answer the click that just asked for it.
                ForceRedraw();
                break;

            case Commands.ToggleMode:
                _config.Mode = _config.Mode == OverlayMode.SingleLine ? OverlayMode.MultiLine : OverlayMode.SingleLine;
                _overlay.SetMode(_config.Mode);
                ForceRedraw();
                _config.Save();
                break;

            case Commands.ToggleLock:
                SetLocked(!_locked);
                break;

            case Commands.ToggleHeader:
                _config.ShowHeader = !_config.ShowHeader;
                ForceRedraw();
                _config.Save();
                break;

            case Commands.ToggleProgressBar:
                _config.ShowProgressBar = !_config.ShowProgressBar;
                ForceRedraw();
                _config.Save();
                break;

            case Commands.ToggleHideWhenPaused:
                _config.HideWhenPaused = !_config.HideWhenPaused;
                _config.Save();
                break;

            case Commands.ToggleHideWhenNoLyrics:
                _config.HideWhenNoLyrics = !_config.HideWhenNoLyrics;
                _config.Save();
                break;

            case Commands.ToggleHideWhenInstrumental:
                _config.HideWhenInstrumental = !_config.HideWhenInstrumental;
                _config.Save();
                break;

            case Commands.TogglePlayerWarning:
                _config.WarnWhenPlayerDoesNotPublish = !_config.WarnWhenPlayerDoesNotPublish;
                if (!_config.WarnWhenPlayerDoesNotPublish)
                {
                    _playerWarning.Reset();
                }

                ForceRedraw();
                _config.Save();
                break;

            case Commands.OffsetBack:
                NudgeLyrics(-OffsetStepMs);
                break;

            case Commands.OffsetForward:
                NudgeLyrics(OffsetStepMs);
                break;

            case Commands.OffsetReset:
                _config.LyricsOffsetMs = 0;
                if (_watcher is not null)
                {
                    _watcher.Clock.ManualOffset = TimeSpan.Zero;
                }

                ForceRedraw();
                _config.Save();
                break;

            case Commands.AnchorBottom:
                SetAnchor(OverlayAnchor.BottomCenter);
                break;

            case Commands.AnchorTop:
                SetAnchor(OverlayAnchor.TopCenter);
                break;

            case Commands.AnchorCenter:
                SetAnchor(OverlayAnchor.Center);
                break;

            case Commands.OpacityUp:
                _config.BackgroundOpacity = Math.Clamp(_config.BackgroundOpacity + 0.05, 0.15, 1.0);
                ApplyTheme();
                _config.Save();
                break;

            case Commands.OpacityDown:
                _config.BackgroundOpacity = Math.Clamp(_config.BackgroundOpacity - 0.05, 0.15, 1.0);
                ApplyTheme();
                _config.Save();
                break;

            case Commands.MoreLines:
                _config.MultiLineCount = Math.Clamp(_config.MultiLineCount + 2, 1, 11);
                ApplyTheme();
                ForceRedraw();
                _config.Save();
                break;

            case Commands.FewerLines:
                _config.MultiLineCount = Math.Clamp(_config.MultiLineCount - 2, 1, 11);
                ApplyTheme();
                ForceRedraw();
                _config.Save();
                break;

            case Commands.OpenCacheFolder:
                OpenFolder(AppConfig.CacheDirectory);
                break;

            case Commands.OpenConfigFolder:
                OpenFolder(AppConfig.DataDirectory);
                break;

            case Commands.AutoVersion:
                if (_snapshot.HasTrack && _lyrics is not null)
                {
                    _lyrics.ClearVersion(_snapshot.Artist, _snapshot.Title, _snapshot.Album);
                    StartLyricsFetch(_snapshot);
                }

                break;

            case Commands.RefreshVersions:
                RefreshVersions();
                break;

            case Commands.Exit:
                Shutdown();
                break;
        }
    }

    /// <summary>Pin the chosen lrclib record for this track and re-resolve straight away.</summary>
    private void SelectVersion(int index)
    {
        IReadOnlyList<LyricsCandidate> candidates;

        lock (_gate)
        {
            candidates = _candidates;
        }

        if (index < 0 || index >= candidates.Count)
        {
            return;
        }

        var snapshot = _snapshot;
        if (!snapshot.HasTrack || _lyrics is null)
        {
            return;
        }

        _lyrics.PinVersion(snapshot.Artist, snapshot.Title, snapshot.Album, candidates[index].Id);
        StartLyricsFetch(snapshot);
    }

    /// <summary>Discard the memoised candidate list so the menu can look again.</summary>
    private void RefreshVersions()
    {
        var snapshot = _snapshot;
        if (!snapshot.HasTrack || _lyrics is null)
        {
            return;
        }

        lock (_gate)
        {
            _candidates = [];
            _candidatesRevision = -1;
        }

        _ = LoadVersionsAsync(
            snapshot.Artist,
            snapshot.Title,
            snapshot.Album,
            snapshot.DurationSeconds,
            snapshot.Revision,
            CancellationToken.None);
    }

    private void NudgeLyrics(int deltaMs)
    {
        _config.LyricsOffsetMs = Math.Clamp(_config.LyricsOffsetMs + deltaMs, -10000, 10000);

        if (_watcher is not null)
        {
            _watcher.Clock.ManualOffset = TimeSpan.FromMilliseconds(_config.LyricsOffsetMs);
        }

        ForceRedraw();
        _config.Save();
    }

    private void SetAnchor(OverlayAnchor anchor)
    {
        _config.Anchor = anchor;
        _config.OffsetX = 0;
        _config.OffsetY = anchor == OverlayAnchor.Center ? 0 : 90;
        _overlay?.SetPlacement(anchor, _config.OffsetX, _config.OffsetY, nint.Zero);
        ForceRedraw();
        _config.Save();
    }

    private void ApplyTheme()
    {
        if (_overlay is null)
        {
            return;
        }

        // The theme bakes into the renderer, so rebuild the overlay host to pick it up.
        _overlay.Dispose();
        _overlay = new OverlayWindow(_config.ToTheme());
        _overlay.Create(_config.Mode, _config.Anchor, _config.OffsetX, _config.OffsetY, nint.Zero);
        _overlay.OpacityScrolled += OnOpacityScrolled;
        _overlay.Moved += OnOverlayMoved;
        _overlay.SetLocked(_locked);
        ForceRedraw();
    }

    private void ForceRedraw()
    {
        if (_overlay is null)
        {
            return;
        }

        _visual = new OverlayVisual();
        OnTick();
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // Nothing useful to do if the shell refuses to open the folder.
        }
    }

    private void ShowTrayMenu()
    {
        if (_tray is null || _messageWindow is null)
        {
            return;
        }

        var status = _snapshot;
        var trackLine = status.HasTrack
            ? $"{status.SourceAppFriendly}: {Truncate(status.TrackDescription, 48)}"
            : "Nothing playing";

        IReadOnlyList<LyricsCandidate> candidates;
        long candidatesRevision;

        lock (_gate)
        {
            candidates = _candidates;
            candidatesRevision = _candidatesRevision;
        }

        long pinnedId = 0;
        var hasPinned = status.HasTrack
                        && _lyrics is not null
                        && _lyrics.TryGetPinnedVersion(status.Artist, status.Title, status.Album, out pinnedId);

        var versionItems = new List<TrayMenuItem>();

        if (!status.HasTrack)
        {
            versionItems.Add(TrayMenuItem.Disabled("Nothing playing"));
        }
        else if (candidates.Count == 0 || candidatesRevision != status.Revision)
        {
            versionItems.Add(new TrayMenuItem(Commands.RefreshVersions, "Look for other versions\u2026"));
        }
        else
        {
            versionItems.Add(new TrayMenuItem(Commands.AutoVersion, "Automatic (best match)", !hasPinned));
            versionItems.Add(TrayMenuItem.Separator);

            var limit = Math.Min(candidates.Count, 12);
            for (var i = 0; i < limit; i++)
            {
                versionItems.Add(new TrayMenuItem(
                    Commands.SelectVersionBase + i,
                    Truncate(candidates[i].Describe(), 70),
                    hasPinned && candidates[i].Id == pinnedId));
            }
        }

        // The top level keeps what is worth reaching for while music is playing. Everything that is
        // set once and then left alone is nested one level down instead of competing for the same
        // list, which is what made this menu unreadable.
        var displayItems = new List<TrayMenuItem>
        {
            new(Commands.ToggleHeader, "Show track info", _config.ShowHeader),
            new(Commands.ToggleProgressBar, "Show progress bar", _config.ShowProgressBar),
            TrayMenuItem.Separator,
            TrayMenuItem.Disabled($"Lines: {_config.MultiLineCount}"),
            new(Commands.MoreLines, "More lines"),
            new(Commands.FewerLines, "Fewer lines"),
            TrayMenuItem.Separator,
            TrayMenuItem.Disabled($"Opacity: {OpacityPercent()}%"),
            new(Commands.OpacityUp, "More opaque"),
            new(Commands.OpacityDown, "Less opaque")
        };

        var behaviourItems = new List<TrayMenuItem>
        {
            new(Commands.ToggleHideWhenPaused, "Hide when paused", _config.HideWhenPaused),
            new(Commands.ToggleHideWhenNoLyrics, "Hide when no lyrics", _config.HideWhenNoLyrics),
            new(Commands.ToggleHideWhenInstrumental, "Hide instrumentals", _config.HideWhenInstrumental),
            new(Commands.TogglePlayerWarning, "Warn when a player does not publish", _config.WarnWhenPlayerDoesNotPublish)
        };

        var timingItems = new List<TrayMenuItem>
        {
            new(Commands.OffsetBack, $"Lyrics earlier (-{OffsetStepSeconds()}s)"),
            new(Commands.OffsetForward, $"Lyrics later (+{OffsetStepSeconds()}s)"),
            TrayMenuItem.Separator,
            new(Commands.OffsetReset, "Reset timing")
        };

        var positionItems = new List<TrayMenuItem>
        {
            new(Commands.AnchorBottom, "Bottom", _config.Anchor == OverlayAnchor.BottomCenter),
            new(Commands.AnchorTop, "Top", _config.Anchor == OverlayAnchor.TopCenter),
            new(Commands.AnchorCenter, "Centre", _config.Anchor == OverlayAnchor.Center)
        };

        var advancedItems = new List<TrayMenuItem>
        {
            new(Commands.OpenConfigFolder, "Open settings folder"),
            new(Commands.OpenCacheFolder, "Open cache folder")
        };

        var items = new List<TrayMenuItem>
        {
            // Informational, and previously selectable while doing nothing.
            TrayMenuItem.Disabled(trackLine),
            TrayMenuItem.Submenu("Lyrics version", versionItems),
            TrayMenuItem.Separator,
            new(Commands.ToggleOverlay, "Show overlay", _overlayEnabled),
            new(Commands.ToggleMode, _config.Mode == OverlayMode.SingleLine ? "Mode: single line" : "Mode: multi line"),
            new(Commands.ToggleLock, "Locked (click-through)", _locked),
            TrayMenuItem.Separator,
            TrayMenuItem.Submenu("Display", displayItems),
            TrayMenuItem.Submenu("Behaviour", behaviourItems),
            TrayMenuItem.Submenu($"Timing: {FormatOffset(_config.LyricsOffsetMs)}", timingItems),
            TrayMenuItem.Submenu($"Position: {AnchorName(_config.Anchor)}", positionItems),
            TrayMenuItem.Separator,
            TrayMenuItem.Submenu("Advanced", advancedItems),
            TrayMenuItem.Separator,
            new(Commands.Exit, "Exit")
        };

        // The explanation for a silent overlay belongs where the user comes looking for one.
        var warningItems = PlayerWarningMenuItems();
        if (warningItems.Count > 0)
        {
            items.InsertRange(1, warningItems);
        }

        var selected = TrayIcon.ShowMenu(_messageWindow.Handle, items);
        if (selected != 0)
        {
            Execute(selected);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "\u2026";

    /// <summary>The step both timing commands move by, as the submenu spells it: "0.25".</summary>
    private static string OffsetStepSeconds() =>
        (OffsetStepMs / 1000.0).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The current offset as the submenu label shows it: "+0.25 s", "-1.00 s" or "0 s".</summary>
    private static string FormatOffset(int milliseconds)
    {
        if (milliseconds == 0)
        {
            return "0 s";
        }

        return (milliseconds / 1000.0).ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + " s";
    }

    /// <summary>The anchor in one word, so the submenu label stays short.</summary>
    private static string AnchorName(OverlayAnchor anchor) => anchor switch
    {
        OverlayAnchor.TopCenter => "top",
        OverlayAnchor.Center => "centre",
        _ => "bottom"
    };

    /// <summary>The backdrop opacity as the whole percentage the menu shows.</summary>
    private int OpacityPercent() =>
        (int)Math.Round(_config.BackgroundOpacity * 100, MidpointRounding.AwayFromZero);

    private static nint MonitorUnderCursor()
    {
        if (GetCursorPos(out var point))
        {
            return MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        }

        return nint.Zero;
    }

    private void Shutdown()
    {
        _config.Save();
        _fetchCts?.Cancel();

        _overlay?.Dispose();
        _overlay = null;

        _tray?.Dispose();
        _tray = null;

        _hotKeys?.Dispose();
        _hotKeys = null;

        _watcher?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _watcher = null;

        KillTimer(_messageWindow.Handle, TimerId);
        _messageWindow.Dispose();

        PostQuitMessage(0);
    }
}
