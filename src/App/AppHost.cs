using System.Collections.Concurrent;
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
    private const uint TimerId = 1;
    private const uint FastIntervalMs = 250;
    private const uint SlowIntervalMs = 1000;

    /// <summary>About 30 frames a second, which is what a sweeping highlight needs.</summary>
    private const uint KaraokeIntervalMs = 33;
    private const int OffsetStepMs = 250;

    /// <summary>
    /// How often the default output device is re-read. A device switch is rare, and the roadmap's
    /// budget applies: poll on a slow cadence and on every track change, not on every frame.
    /// </summary>
    private const int AudioOutputPollMs = 5000;

    /// <summary>
    /// Minimum time between z-order re-assertions. A tick count was wrong here: twenty ticks is five
    /// seconds on the karaoke path but twenty on the one-second idle path, which is long enough for a
    /// game that raises its own window to keep the overlay buried.
    /// </summary>
    private const int TopmostReassertMs = 2000;

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

    /// <summary>Length of a tray-initiated preview, in milliseconds.</summary>
    private const int PreviewDurationMs = 15_000;

    /// <summary>When the current preview ends; zero when no preview has been started.</summary>
    private long _previewUntil;

    /// <summary>True while the previous tick was showing the preview, so the end can be noticed once.</summary>
    private bool _previewActive;

    private bool IsPreviewActive => _previewUntil != 0 && Environment.TickCount64 < _previewUntil;

    private bool IsDemoActive => _demo || IsPreviewActive;

    private HotKeyManager? _hotKeys;
    private TrayIcon? _tray;
    private OverlayWindow? _overlay;
    private ShortcutToastWindow? _toast;
    private MediaSessionWatcher? _watcher;
    private LyricsService? _lyrics;
    private LocalLyricsStore? _localLyrics;

    private LyricLine[] _lines = [];

    /// <summary>The optional second-language sidecar for the current local sheet, if one exists.</summary>
    private LyricLine[] _translation = [];
    private bool _lyricsSynced;
    private string? _lyricsStatus;
    private bool _isInstrumental;
    private bool _fetchCompleted;
    private int _lyricsRetryAttempt;
    private long _nextLyricsRetryAt;
    private long _lyricsRevision = -1;

    /// <summary>The lrclib record behind the lines on screen, when the answer carried one.</summary>
    private long? _lyricsRecordId;

    /// <summary>Where the current sheet came from; a local sheet is badged differently.</summary>
    private LyricSource _lyricsSource = LyricSource.Lrclib;

    /// <summary>Revision whose explicit local selection could not be read, or -1.</summary>
    private long _localFileMissingRevision = -1;

    /// <summary>Revision already told about a missing local file, so the warning is said once.</summary>
    private long _alertedMissingLocalRevision = -1;

    /// <summary>An import preview waiting for confirmation, and its source path and track revision.</summary>
    private LocalLyricsPreview? _importPreview;
    private string? _importSourcePath;
    private long _importPreviewRevision = -1;

    /// <summary>A sentence about the last import or clipboard action, shown in the selector.</summary>
    private string? _importMessage;

    /// <summary>The local file opened in an external editor, and the write time last seen.</summary>
    private string? _externalEditPath;
    private DateTime _externalEditStamp;

    /// <summary>How many version searches are in flight; the selector shows a loading state while it is not zero.</summary>
    private int _versionsInFlight;

    /// <summary>
    /// Bumped whenever the lyric answer changes, so the open windows can redraw on the UI thread
    /// instead of being rendered from the lookup's background thread.
    /// </summary>
    private int _lyricsStamp;

    /// <summary>Bumped whenever the candidate list changes, for the same reason.</summary>
    private int _versionsStamp;

    /// <summary>
    /// The track revision whose alternative list has already been asked for. It stops the selector's
    /// live refresh from re-asking every tick after a failed search; Look again resets the intent.
    /// </summary>
    private long _versionsAttemptedRevision = -1;

    private int _uiLyricsStamp = -1;
    private int _uiVersionsStamp = -1;

    /// <summary>
    /// The transient "what just started, and what did the lookup find" notice. It lives in the same
    /// overlay as the lyrics, which is also the only surface an OLED preset has left: it is dynamic,
    /// so it disappears again instead of lighting the same pixels for hours.
    /// </summary>
    private readonly InfoPopup _infoPopup = new();

    /// <summary>True while a notice has been asked for but its lyric verdict is still outstanding.</summary>
    private bool _popupRequested;
    private bool _popupStarted;

    /// <summary>Whether the resolved sheet carries word-level timings, which is the distinction the pop-up names.</summary>
    private bool _lyricsWordSynced;

    /// <summary>Whether the last empty lookup was a definitive "lrclib has nothing" rather than an outage.</summary>
    private bool _lyricsDefinitive;

    /// <summary>
    /// Revision whose missing or untimed lyrics have already been announced. Announcing per poll - or
    /// per retry - would be a notification nobody reads; once per track is the contract.
    /// </summary>
    private long _alertedRevision = -1;

    private CancellationTokenSource? _fetchCts;
    private OverlayVisual _visual = new();
    private long _lastRevision = -1;

    /// <summary>When the current track started, for <see cref="AppConfig.HeaderAutoHideSeconds"/>.</summary>
    private long _headerShownAt;
    private NowPlayingSnapshot _snapshot = NowPlayingSnapshot.Empty;
    private IReadOnlyList<LyricsCandidate> _candidates = [];
    private long _candidatesRevision = -1;

    /// <summary>
    /// What the complete-lyrics window's own search found for the current track. Kept apart from the
    /// selector's list because it answers a different question: the listener's words, not the
    /// player's metadata.
    /// </summary>
    private IReadOnlyList<LyricsCandidate> _searchResults = [];
    private string? _searchMessage;
    private bool _searchLoading;
    private long _searchRevision = -1;
    private CancellationTokenSource? _searchCts;

    /// <summary>
    /// The live-source list the open source menu was built from. The watcher keeps polling while a
    /// tray menu is up, so the list can change underneath the menu; the command id names an index
    /// into this list, which is the list the user was looking at.
    /// </summary>
    private IReadOnlyList<SourceCandidate> _menuSources = [];

    private uint _currentInterval;
    private bool _overlayEnabled = true;
    private bool _locked = true;
    private string? _lastTooltip;
    private long _lastTopmostReassertAt;

    /// <summary>Device name of the display the overlay is on, for the per-display placement map.</summary>
    private string _currentMonitorDevice = "";

    /// <summary>The foreground process image path as last polled, for the per-app hide list.</summary>
    private string? _foregroundPath;

    /// <summary>The foreground window at the same poll, for the fullscreen comparison.</summary>
    private nint _foregroundWindow;

    /// <summary>When the foreground/fullscreen facts are polled again.</summary>
    private long _nextPresenceCheckAt;

    /// <summary>The sustained exclusive-fullscreen watch; one quiet tray sentence per situation.</summary>
    private readonly FullscreenWatch _fullscreenWatch = new();

    /// <summary>True while the fullscreen heuristic is sustained, for the tray menu row.</summary>
    private bool _fullscreenNotice;

    /// <summary>True while an application is capturing from the default microphone.</summary>
    private bool _microphoneInUse;

    /// <summary>The rolling decision log behind the diagnostics window; bounded in memory and on disk.</summary>
    private readonly DiagnosticLog _diagnosticLog = new(Path.Combine(AppConfig.DataDirectory, "diagnostics.log"));

    /// <summary>The explicit-publish client, created once; the UI never publishes without confirmation.</summary>
    private LrclibPublisher? _publisher;
    private CancellationTokenSource? _publishCts;
    private bool _publishing;
    private bool _publishCompleted;
    private (PublishOutcome Outcome, string Message) _publishResult;

    private DiagnosticsWindow? _diagnostics;

    /// <summary>The default output device as last read; null until CoreAudio answers once.</summary>
    private AudioOutput? _audioOutput;

    /// <summary>When the output device is read again; see <see cref="AudioOutputPollMs"/>.</summary>
    private long _nextAudioOutputCheckAt;

    /// <summary>Key of the current track's saved timing override, or null without usable metadata.</summary>
    private string? _trackKey;

    /// <summary>A correction made for this session only; the next saved nudge folds it in.</summary>
    private int _sessionOffsetMs;

    /// <summary>The correction actually pushed into the clock, with the layer that supplied it.</summary>
    private TimingResolution _timing;
    private SettingsWindow? _settings;
    private LyricPickerWindow? _picker;
    private LyricViewerWindow? _viewer;
    private TrayLauncherWindow? _launcher;

    /// <summary>Show-or-swap plus visibility, shared by the launcher and every title bar.</summary>
    private readonly WindowNavigation _navigation;

    /// <summary>The page the shell last showed, so a hidden shell reopens where it was left.</summary>
    private WindowDestination _lastDestination = WindowDestination.Settings;

    /// <summary>Last screen rectangle of the shell, used when no page window has a rectangle yet.</summary>
    private RECT? _shellRect;
    private readonly bool _openSettings;
    private readonly bool _openPreview;
    private readonly HostCommand _startupCommand;
    private readonly ConcurrentQueue<HostCommand> _pendingCommands = new();
    private readonly CancellationTokenSource _commandCts = new();

    /// <summary>Action names whose default chord another application already owns.</summary>
    private readonly HashSet<string> _unavailableShortcuts = [];

    private const int ErrorHotKeyAlreadyRegistered = 1409;

    /// <summary>
    /// The empty priority list handed to the watcher while nothing is configured. A shared instance
    /// keeps the default path free of a per-poll allocation, which matters because the watcher reads
    /// this setting once a second for as long as the application runs.
    /// </summary>
    private static readonly IReadOnlyList<SourcePreference> NoPriority = [];

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

    public AppHost(
        AppConfig config,
        bool demo = false,
        bool openSettings = false,
        bool openPreview = false,
        HostCommand startupCommand = HostCommand.None)
    {
        _config = config;
        _demo = demo;
        _openSettings = openSettings;
        _openPreview = openPreview;
        _startupCommand = startupCommand;
        _navigation = new WindowNavigation(OpenShell, IsWindowOpen);
    }

    public int Run()
    {
        _messageWindow.Create();
        _messageWindow.SetHandler(OnMessage);

        _hotKeys = new HotKeyManager(_messageWindow.Handle);
        _hotKeys.Pressed += OnHotKey;

        _tray = new TrayIcon(_messageWindow.Handle);
        _tray.LeftClicked += ShowLauncher;
        _tray.MenuRequested += ShowTrayMenu;
        _tray.Show("Overlyric - waiting for playback");

        _diagnosticLog.Add(
            "app",
            string.Concat(
                "started ",
                typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "?",
                _demo ? " (demo)" : string.Empty));

        if (_config.StartWithWindows)
        {
            // Keep the Run value pointed at this executable, because an update or a moved build
            // folder would otherwise leave Windows starting the old path.
            StartupRegistration.SetEnabled(true);
        }

        if (_config.IsFirstRun && !_demo)
        {
            _tray.Alert(
                "Overlyric is running",
                "Play something, or click the tray icon for the launcher: lyrics, versions, diagnostics and settings are all there.");
        }

        _overlay = new OverlayWindow(_config.ToTheme());

        // The configured display decides where the overlay opens; its own remembered placement is
        // loaded before the window is created, so it never appears on the wrong screen first.
        var monitor = ApplyMonitorPreference(out var monitorWarning);
        _overlay.Create(_config.Mode, _config.Anchor, _config.OffsetX, _config.OffsetY, monitor);

        if (_config.StageMode)
        {
            _overlay.SetStage(true);
        }

        RepairLegacyPlacement();

        if (monitorWarning is not null)
        {
            _tray?.Alert("Overlyric \u2014 display not found", monitorWarning);
        }

        _locked = _config.Locked;
        _overlay.SetLocked(_locked);

        _overlay.OpacityScrolled += OnOpacityScrolled;
        _overlay.Moved += OnOverlayMoved;
        _overlay.SettingsRequested += OpenSettings;
        _overlay.OffsetStepRequested += NudgeLyrics;
        _overlay.OffsetResetRequested += ResetLyricsOffset;
        _overlay.ViewLyricsRequested += OpenLyricViewer;
        _overlay.ChooseLyricsRequested += OpenLyricPicker;

        // The bar's layout chip and lock button are the same commands as the hotkeys, so there is one
        // implementation of "cycle the mode" and one of "lock", not a second pair behind a click.
        _overlay.LayoutRequested += () => Execute(Commands.ToggleMode);
        _overlay.LockRequested += () => Execute(Commands.ToggleLock);
        _overlay.PlayPauseRequested += TogglePlayback;
        _overlay.NextRequested += SkipNextTrack;

        if (_demo)
        {
            // Preview mode: drive the overlay from a canned synced track so the rendering
            // path can be exercised without anything playing.
            _overlay.SetVisible(true);

            // Every page reads the same snapshot; without this the demo sheet listed its lines
            // under a header that said nothing was playing.
            _snapshot = DemoSnapshot;

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
            _watcher = MediaSessionWatcher.CreateAsync(ReadSourceSettings).GetAwaiter().GetResult();

            // Timing resolution needs the output device before the first frame, so the saved profile
            // for the headphones in use is already in force when the first line appears.
            _audioOutput = AudioEndpoint.ReadDefault();
            ApplyLyricOffset();
            ApplyKaraokeTiming();

            // Explicit choices live in selections.json; the old versions.json is migrated into it on
            // first load, so a record pinned before the library existed still wins.
            var selections = LyricSelectionStore.Load(
                Path.Combine(AppConfig.DataDirectory, "selections.json"),
                Path.Combine(AppConfig.DataDirectory, "versions.json"));

            _localLyrics = new LocalLyricsStore(AppConfig.LyricsDirectory);
            _publisher = new LrclibPublisher();

            _lyrics = new LyricsService(
                new LrclibClient(LrclibClient.DefaultUserAgent),
                new LyricsCache(AppConfig.CacheDirectory),
                selections,
                _localLyrics);
        }

        RegisterHotKeys();

        _currentInterval = SlowIntervalMs;
        SetTimer(_messageWindow.Handle, TimerId, _currentInterval, 0);

        if (_openPreview)
        {
            StartPreview();
        }

        if (_openSettings)
        {
            OpenSettings();
        }

        // A second launch forwards its command line over this pipe instead of starting a second
        // overlay. The callback runs on the pipe thread and only queues; the UI thread dispatches.
        CommandPipe.Start(
            line =>
            {
                if (CommandLine.TryParse(line.Split(' '), out var forwarded, out _)
                    && forwarded != HostCommand.None)
                {
                    _pendingCommands.Enqueue(forwarded);
                }
            },
            _commandCts.Token);

        if (_startupCommand != HostCommand.None)
        {
            DispatchHostCommand(_startupCommand);
        }

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

        foreach (var action in ShortcutActions.All)
        {
            var chord = ShortcutActions.Chord(_config, action);

            if (chord.IsEmpty)
            {
                continue;
            }

            if (!_hotKeys.TryRegister(action.Command, chord.Modifiers, chord.VirtualKey, out _))
            {
                // Another application already owns it. The settings window shows these as taken
                // rather than leaving the user pressing a shortcut that does nothing.
                _unavailableShortcuts.Add(action.Name);
            }
        }
    }

    /// <summary>
    /// Rebind one action. Returns null on success, or a message explaining the refusal so the
    /// settings window can show it beside the shortcut.
    /// </summary>
    private string? AssignShortcut(int command, string text)
    {
        if (_hotKeys is null)
        {
            return "Hotkeys are not available.";
        }

        var action = ShortcutActions.ForCommand(command);
        if (action is null)
        {
            return "Unknown action.";
        }

        var chord = HotKeyChord.Parse(text);

        if (!chord.IsEmpty && !chord.HasStrongModifier)
        {
            return "Add Ctrl, Alt or Win.";
        }

        if (!chord.IsEmpty)
        {
            foreach (var other in ShortcutActions.All)
            {
                if (other.Command == command)
                {
                    continue;
                }

                if (ShortcutActions.Chord(_config, other) == chord)
                {
                    return $"{chord} is already used by {other.Label}.";
                }
            }
        }

        var previous = ShortcutActions.Chord(_config, action);

        _hotKeys.Unregister(command);
        _unavailableShortcuts.Remove(action.Name);

        if (!chord.IsEmpty && !_hotKeys.TryRegister(command, chord.Modifiers, chord.VirtualKey, out var error))
        {
            if (!previous.IsEmpty)
            {
                _hotKeys.TryRegister(command, previous.Modifiers, previous.VirtualKey, out _);
            }

            return error == ErrorHotKeyAlreadyRegistered
                ? $"{chord} is already taken by another application."
                : "Windows refused that combination.";
        }

        _config.Shortcuts[action.Name] = chord.ToString();
        _config.Save();
        return null;
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

            case WM_DISPLAYCHANGE:
                OnDisplayChanged();
                break;

            case WM_DESTROY:
                _commandCts.Cancel();
                _commandCts.Dispose();
                _publishCts?.Cancel();
                _publishCts?.Dispose();
                _publisher?.Dispose();
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

        // Commands from a second instance are queued by the pipe thread and executed here, on the
        // UI thread that owns every window.
        while (_pendingCommands.TryDequeue(out var pending))
        {
            DispatchHostCommand(pending);
        }

        // Foreground and fullscreen facts change slowly; two seconds is the roadmap cadence, and it
        // is also the interval the topmost re-assertion already uses.
        UpdatePresence(Environment.TickCount64);

        // A device switch applies that endpoint's profile. The read is cheap and only happens when
        // the slow interval is up, or when the track changed (see below).
        if (_watcher is not null && RefreshAudioOutput())
        {
            ApplyLyricOffset();
            _settings?.RefreshIfOpen();
        }

        var preview = IsPreviewActive;
        if (_previewActive && !preview)
        {
            _previewActive = false;
            EndPreview();
        }
        else if (preview)
        {
            _previewActive = true;
        }

        var demoActive = _demo || preview;
        var snapshot = demoActive ? DemoSnapshot : _watcher?.Snapshot ?? NowPlayingSnapshot.Empty;
        var position = demoActive ? DemoPosition() : _watcher?.Clock.Position ?? TimeSpan.Zero;
        var positionIsUsable = demoActive || _watcher is null || _watcher.Clock.PositionIsUsable;
        _snapshot = snapshot;

        UpdatePlayerWarning(snapshot, positionIsUsable);

        if (snapshot.Revision != _lastRevision)
        {
            _lastRevision = snapshot.Revision;
            _headerShownAt = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _versionsStamp);

            // The track key decides which saved correction applies, and a new track is also where a
            // session-only nudge stops applying. The endpoint is re-read now, not on the slow poll,
            // because "new song, new output" is exactly when the profile should follow.
            _trackKey = demoActive ? null : TimingProfiles.TrackKey(snapshot.Artist, snapshot.Title);
            _sessionOffsetMs = 0;
            RefreshAudioOutput(force: true);

            _diagnosticLog.Add(
                "track",
                string.Concat(
                    snapshot.Artist,
                    snapshot.Artist.Length > 0 && snapshot.Title.Length > 0 ? " - " : string.Empty,
                    snapshot.Title,
                    " [",
                    snapshot.SourceAppFriendly,
                    "] rev ",
                    snapshot.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            // A new song - or the first one seen after start-up - is exactly when a listener wants
            // to know what is playing and what the lyric lookup made of it.
            RequestInfoPopup(snapshot);

            // ResetForNewTrack clears the clock's manual offset, so a correction that is still
            // displayed has to be pushed back before this frame is built. Re-read the position
            // afterwards: it now includes the correction.
            ApplyLyricOffset();
            position = demoActive ? DemoPosition() : _watcher?.Clock.Position ?? TimeSpan.Zero;

            if (!_demo && !preview && snapshot.HasTrack)
            {
                _lyricsRetryAttempt = 0;
                _nextLyricsRetryAt = 0;
                StartLyricsFetch(snapshot, clearVersions: true);
            }
        }
        else if (!_demo && !preview && ShouldRetryLyrics())
        {
            _lyricsRetryAttempt++;
            StartLyricsFetch(snapshot);
        }

        // After the track-change block, never before it: the block clears the previous track's
        // verdict, and the notification must not be decided on facts that belong to the song before.
        UpdateLyricsAlert(snapshot);
        UpdateLocalFileAlert();
        CheckExternalEditReload();
        UpdatePublishResult();

        var visual = BuildVisual(snapshot, position, positionIsUsable);

        if (_config.StageMode)
        {
            visual = visual with
            {
                Mode = OverlayMode.MultiLine,
                StageMode = true,
                ShowHeader = false,
                ShowProgressBar = false
            };
        }

        visual = WithTranslation(visual, position);
        visual = WithInfoPopup(visual, snapshot, positionIsUsable);

        // The burn-in nudge rides on the visual rather than the window, so placement, dragging and
        // hit testing are unaffected.
        var (shiftX, shiftY) = PixelShiftOffset(position);
        visual = visual with
        {
            OffsetX = shiftX,
            OffsetY = shiftY,
            Artwork = _snapshot.Artwork,
            ArtworkWidth = _snapshot.ArtworkWidth,
            ArtworkHeight = _snapshot.ArtworkHeight,
            IsPlaying = _snapshot.IsPlaying,
            OffsetLabel = TimingProfiles.Describe(TimingForVisual, _audioOutput),
            OffsetIsDefault = TimingForVisual.OffsetMs == 0
        };

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
        RefreshLyricWindows();
    }

    /// <summary>Adds the active sidecar line, when there is one and the option is on.</summary>
    private OverlayVisual WithTranslation(OverlayVisual visual, TimeSpan position)
    {
        if (!_config.ShowTranslation || _translation.Length == 0 || visual.ActiveIndex < 0)
        {
            return visual;
        }

        var text = LyricText.TranslationAt(_translation, position);
        return text is null ? visual : visual with { Translation = text };
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

        var header = !_config.StageMode && _config.ShowHeader && HeaderVisible() ? BuildHeader(snapshot) : null;

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

        var count = _config.StageMode
            ? Math.Clamp(7, 1, lines.Length)
            : _config.Mode switch
            {
                OverlayMode.SingleLine => 1,
                OverlayMode.SingleLineWithNext => 2,
                _ => Math.Clamp(_config.MultiLineCount, 1, lines.Length)
            };

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

        var index = LyricText.ActiveLine(lines, position);
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

        var sungChars = -1;

        if (_config.Karaoke)
        {
            // The sheet's own line end beats "the next line starts here": across a long
            // instrumental break the second one is minutes away, and the sweep would crawl.
            var lineEnd = lines[index].End
                          ?? (index + 1 < lines.Length
                              ? lines[index + 1].Start
                              : lines[index].Start + TimeSpan.FromSeconds(5));

            sungChars = KaraokeSweep.SungCharacters(lines[index], lineEnd, position);
        }

        // The peek mode is active line first, next line second. At the last line there is no next
        // line, so it shrinks to one rather than showing the previous line above the active one.
        var visibleCount = _config.Mode == OverlayMode.SingleLineWithNext
            ? Math.Min(2, Math.Max(1, lines.Length - index))
            : count;

        var windowStart = _config.Mode == OverlayMode.SingleLineWithNext
            ? index
            : Math.Clamp(index - (visibleCount / 2), 0, Math.Max(0, lines.Length - visibleCount));

        var visible = new string[visibleCount];

        for (var i = 0; i < visibleCount; i++)
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
            TrackProgress = trackProgress,
            GapProgress = Quantise(LyricText.GapProgress(lines, index, position)),
            SungChars = sungChars
        };
    }

    /// <summary>
    /// Header text for the overlay. The title is cleaned first, because a player can report an entire
    /// movement listing as the track name - TIDAL does exactly that for "Song of Scheherazade" - which
    /// would otherwise stretch the header across the whole screen.
    /// </summary>
    private static string? BuildHeader(NowPlayingSnapshot snapshot)
    {
        var title = MetadataNormaliser.CleanTitle(snapshot.Title, snapshot.Artist);

        var text = string.IsNullOrWhiteSpace(snapshot.Artist)
            ? title
            : $"{snapshot.Artist} \u2014 {title}";

        text = text.Trim(' ', '\u2014');

        return text.Length == 0 ? null : Truncate(text, 80);
    }

    /// <summary>
    /// Ask for the info notice. What it says is decided on the tick instead of here, because a lyric
    /// lookup that has just started has no verdict to report yet.
    /// </summary>
    private void RequestInfoPopup(NowPlayingSnapshot snapshot)
    {
        if (!snapshot.HasTrack || _config.InfoPopupSeconds <= 0)
        {
            _infoPopup.Clear();
            _popupRequested = false;
            _popupStarted = false;
            return;
        }

        _popupRequested = true;
        _popupStarted = false;
    }

    /// <summary>
    /// Fold the info notice into the frame. While the lookup is still running the notice says so; when
    /// the answer lands it gets a fresh showing, because "looking up lyrics" and "no synced lyrics"
    /// are different statements and only the second is the one the listener was waiting for. That
    /// also means a slow lookup cannot keep the notice on screen: the deadline belongs to the
    /// showing, not to the request.
    /// </summary>
    private OverlayVisual WithInfoPopup(OverlayVisual visual, NowPlayingSnapshot snapshot, bool positionIsUsable)
    {
        var now = Environment.TickCount64;

        if (_config.InfoPopupSeconds <= 0 || !snapshot.HasTrack)
        {
            _infoPopup.Clear();
            _popupRequested = false;
            _popupStarted = false;
            return visual with { Notice = null };
        }

        if (_popupRequested)
        {
            bool completed;
            bool instrumental;
            bool hasLines;
            bool synced;
            bool wordSynced;

            lock (_gate)
            {
                completed = _fetchCompleted;
                instrumental = _isInstrumental;
                hasLines = _lines.Length > 0;
                synced = _lyricsSynced;
                wordSynced = _lyricsWordSynced;
            }

            // The provisional notice is drawn once; the verdict replaces it and restarts the clock.
            if (completed || !_popupStarted)
            {
                var (detail, tone) = LyricStatus.Describe(
                    completed,
                    instrumental,
                    hasLines,
                    synced,
                    wordSynced,
                    positionIsUsable);

                _infoPopup.Start(BuildNoticeTitle(snapshot), detail, tone, now, _config.InfoPopupSeconds);
                _popupStarted = true;
            }

            if (completed)
            {
                _popupRequested = false;
                _popupStarted = false;
            }
        }

        _infoPopup.Expire(now);

        return visual with { Notice = _infoPopup.Current };
    }

    /// <summary>
    /// One Windows notification per track, when the lookup settles with something the listener needs
    /// to know and cannot otherwise see: lrclib has no lyrics, or has the words but no timings. An
    /// outage is neither of those - it is not evidence about the track at all - which is what the
    /// definitive flag is for.
    /// </summary>
    private void UpdateLyricsAlert(NowPlayingSnapshot snapshot)
    {
        if (IsDemoActive || _tray is null || !_config.NotifyWhenLyricsMissing || !snapshot.HasTrack)
        {
            return;
        }

        if (snapshot.Revision == _alertedRevision)
        {
            return;
        }

        bool completed;
        bool definitive;
        bool instrumental;
        bool hasLines;
        bool synced;

        lock (_gate)
        {
            completed = _fetchCompleted;
            definitive = _lyricsDefinitive;
            instrumental = _isInstrumental;
            hasLines = _lines.Length > 0;
            synced = _lyricsSynced;
        }

        if (!completed)
        {
            return;
        }

        // Recorded before the decision, so a track that is fine - or one still waiting on a retry -
        // is not re-examined on every tick for the rest of the song.
        _alertedRevision = snapshot.Revision;

        var kind = LyricsAlert.Decide(definitive, instrumental, hasLines, synced);
        if (kind == LyricsAlertKind.None)
        {
            return;
        }

        var (title, text) = LyricsAlert.Describe(kind, NoticeTrack(snapshot));
        _tray.Alert(title, text);
    }

    /// <summary>The song, cleaned and short enough for the notice's first line.</summary>
    private static string BuildNoticeTitle(NowPlayingSnapshot snapshot) => Truncate(NoticeTrack(snapshot), 64);

    /// <summary><c>Artist - Title</c>, with the release noise the header also strips.</summary>
    private static string NoticeTrack(NowPlayingSnapshot snapshot)
    {
        var title = MetadataNormaliser.CleanTitle(snapshot.Title, snapshot.Artist);

        return string.IsNullOrWhiteSpace(snapshot.Artist)
            ? title.Trim()
            : $"{snapshot.Artist} \u2014 {title}".Trim();
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

        // A listed application owns the foreground window: stay out of its way. An unknown
        // foreground process never matches, so a protected game cannot hide the overlay by accident.
        if (PresenceRules.IsHiddenForApp(_config.HideApps, _foregroundPath))
        {
            return false;
        }

        if (_config.HideWhenMicrophoneInUse && _microphoneInUse)
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
    /// <summary>
    /// Polls the foreground process and the shell's notification state on the Presence cadence.
    /// The hide list acts on every tick through <see cref="IsOverlayVisible"/>; the fullscreen
    /// heuristic has to be sustained for ten seconds before it earns one tray sentence, and it
    /// never draws through the overlay (which is invisible in exactly that case).
    /// </summary>
    private void UpdatePresence(long now)
    {
        if (now < _nextPresenceCheckAt)
        {
            return;
        }

        _nextPresenceCheckAt = now + 2000;
        _foregroundWindow = Foreground.Handle();
        _foregroundPath = Foreground.ProcessPath();

        _microphoneInUse = _config.HideWhenMicrophoneInUse && AudioSessions.IsMicrophoneCapturing();

        var overlayMonitor = _overlay is { IsVisible: true }
            ? MonitorFromWindow(_overlay.Handle, MONITOR_DEFAULTTONEAREST)
            : MonitorUnderCursor();

        var foregroundIsOwnOrShell = IsOwnWindow(_foregroundWindow) || Foreground.IsShellWindow(_foregroundWindow);
        var sameMonitor = _foregroundWindow != nint.Zero
            && Foreground.MonitorOf(_foregroundWindow) == overlayMonitor;
        var coversMonitor = Foreground.CoversMonitor(_foregroundWindow, overlayMonitor);

        _fullscreenWatch.Update(
            PresenceRules.LooksExclusiveFullscreen(
                Foreground.NotificationState(),
                foregroundIsOwnOrShell,
                sameMonitor,
                coversMonitor),
            now);

        _fullscreenNotice = _fullscreenWatch.Candidate;

        if (!_fullscreenWatch.IsNewSituation || _tray is null)
        {
            return;
        }

        _tray.Alert(
            "Overlyric - fullscreen game",
            "The game is running in exclusive fullscreen, where no overlay can draw. Switch it to Borderless or Windowed Fullscreen to see the lyrics over it.");
    }

    /// <summary>True when a window belongs to one of Overlyric's own visible windows.</summary>
    private bool IsOwnWindow(nint window) =>
        window != nint.Zero
        && (window == _overlay?.Handle
            || window == _settings?.Handle
            || window == _picker?.Handle
            || window == _viewer?.Handle);

    /// <summary>
    /// What the Behaviour section prints for "add the app in front". Overlyric's own windows are
    /// foreground whenever the settings window is open, and offering to hide the overlay for
    /// Overlyric itself would be a trap, so that case is named instead of added.
    /// </summary>
    private string ForegroundAppLabel()
    {
        if (IsOwnWindow(_foregroundWindow))
        {
            return "Overlyric itself";
        }

        return string.IsNullOrWhiteSpace(_foregroundPath) ? "No app detected" : Path.GetFileName(_foregroundPath);
    }

    /// <summary>Adds the foreground executable's name to the hide list.</summary>
    private void AddForegroundApp()
    {
        // Adding Overlyric to its own hide list would make the overlay vanish whenever the settings
        // window is open and appear broken; the row says why instead.
        if (IsOwnWindow(_foregroundWindow))
        {
            return;
        }

        var name = Path.GetFileName(_foregroundPath ?? string.Empty);

        if (name.Length == 0)
        {
            return;
        }

        if (!_config.HideApps.Exists(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
        {
            _config.HideApps.Add(name);
            _config.Save();
        }

        _settings?.RefreshIfOpen();
        ForceRedraw();
    }

    /// <summary>Empties the hide list; the overlay follows the foreground again immediately.</summary>
    private void ClearHideApps()
    {
        if (_config.HideApps.Count == 0)
        {
            return;
        }

        _config.HideApps.Clear();
        _config.Save();
        _settings?.RefreshIfOpen();
        ForceRedraw();
    }

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
        // z-order is worthwhile insurance - but at most every couple of seconds, not on every tick.
        //
        // Not while one of Overlyric's own lyric windows is open: they are topmost as well, and every
        // re-assert would lift the overlay back over the window the user is reading. The overlay can
        // wait; the moment the panel closes, the next tick puts it back on top.
        var now = Environment.TickCount64;
        if (!IsOwnWindowOpen && now - _lastTopmostReassertAt >= TopmostReassertMs)
        {
            _lastTopmostReassertAt = now;
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
        var header = _locked ? "Overlyric" : "Overlyric (unlocked - hover for controls)";

        var text = snapshot.HasTrack
            ? $"{header}\n{snapshot.SourceAppFriendly}: {snapshot.TrackDescription}"
            : header;

        if (PresenceRules.IsHiddenForApp(_config.HideApps, _foregroundPath))
        {
            text += $"\nHidden for {Path.GetFileName(_foregroundPath)}";
        }

        if (_config.HideWhenMicrophoneInUse && _microphoneInUse)
        {
            text += "\nHidden while the microphone is in use";
        }

        if (_fullscreenNotice)
        {
            text += "\nThe game is in exclusive fullscreen - use Borderless";
        }

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
        var playing = snapshot.IsPlaying && IsOverlayVisible(snapshot);

        // The sweep is the one thing that needs a frame rate rather than a tick rate; everything
        // else stays on the slow path so the overlay costs nothing when nothing is moving.
        var wanted = playing
            ? _config.Karaoke && _lyricsSynced ? KaraokeIntervalMs : FastIntervalMs
            : SlowIntervalMs;

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

    private void StartLyricsFetch(NowPlayingSnapshot snapshot, bool clearVersions = false)
    {
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        _fetchCts = new CancellationTokenSource();
        var token = _fetchCts.Token;
        var revision = snapshot.Revision;
        var attempt = _lyricsRetryAttempt;

        if (clearVersions)
        {
            // A manual search belongs to the track that was playing when it was asked. Cancel it and
            // drop what it found before this track's answer replaces it.
            CancelSearch(clear: true);
        }

        lock (_gate)
        {
            _lines = [];
            _translation = [];
            _lyricsSynced = false;
            _lyricsStatus = null;
            _isInstrumental = false;
            _fetchCompleted = false;
            _lyricsRevision = revision;
            _lyricsWordSynced = false;
            _lyricsDefinitive = false;
            _lyricsRecordId = null;
            _lyricsSource = LyricSource.Lrclib;
            _localFileMissingRevision = -1;

            if (clearVersions)
            {
                // The list belongs to the previous track; offering it for the new one would let a
                // click pin a record that has nothing to do with what is playing.
                _candidates = [];
                _candidatesRevision = -1;
                _versionsAttemptedRevision = -1;
                _importPreview = null;
                _importSourcePath = null;
                _importPreviewRevision = -1;
                _importMessage = null;
                _externalEditPath = null;
                _externalEditStamp = default;
            }
        }

        Interlocked.Increment(ref _lyricsStamp);
        Interlocked.Increment(ref _versionsStamp);

        var artist = snapshot.Artist;
        var title = snapshot.Title;
        var album = snapshot.Album;
        var duration = snapshot.DurationSeconds;

        _ = Task.Run(
            async () =>
            {
                LyricsLookup lookup = default;

                try
                {
                    lookup = await _lyrics!.LookupDetailedAsync(artist, title, album, duration, token)
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

                var result = lookup.Result;
                var needsVersions = false;

                lock (_gate)
                {
                    if (_lyricsRevision != revision)
                    {
                        return;
                    }

                    _fetchCompleted = true;
                    _lyricsDefinitive = lookup.Definitive;
                    _lyricsWordSynced = false;
                    _lyricsRecordId = result?.LrclibId;
                    _lyricsSource = result?.Source ?? LyricSource.Lrclib;
                    _localFileMissingRevision = lookup.LocalFileMissing ? revision : -1;

                    // A sidecar only exists beside a local sheet; loading it is a small read on the
                    // lookup thread, and a missing one is simply no translation.
                    _translation = _lyricsSource == LyricSource.Local
                        && _lyrics is not null
                        && _lyrics.TryLoadTranslation(artist, title, album, out var translationLines)
                            ? [.. translationLines]
                            : [];

                    _diagnosticLog.Add(
                        "lyrics",
                        string.Concat(
                            _lyricsSource.ToString(),
                            " lines=",
                            _lines.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            _lyricsSynced ? " synced" : " untimed",
                            _lyricsStatus is null ? string.Empty : " status=" + _lyricsStatus,
                            _lyricsRecordId is { } loggedId ? " record=" + loggedId.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
                            lookup.LocalFileMissing ? " (local file missing)" : string.Empty));

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
                        _lyricsWordSynced = result.HasWordTimings;
                        _isInstrumental = false;
                        _lyricsStatus = null;
                    }

                    // Nothing to show: line up another attempt unless lrclib answered definitively.
                    // A definitive miss is already a tombstone, so retrying it would only re-read the
                    // cache; an outage is exactly what the retry exists for.
                    _nextLyricsRetryAt =
                        _lines.Length == 0 && !_isInstrumental && !lookup.Definitive && attempt < MaxLyricsRetries
                            ? Environment.TickCount64 + RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length - 1)]
                            : 0;

                    // Missing or untimed lyrics are exactly where a listener is likely to want a
                    // different version, so the alternative list is fetched up front to keep the menu
                    // instant. Good synced lyrics need no extra request - and a lookup that could not
                    // reach lrclib gets none either, or a failed resolution would be followed by two
                    // more searches of the same unavailable server.
                    needsVersions = (_lines.Length == 0 || !_lyricsSynced) && lookup.Definitive;
                }

                Interlocked.Increment(ref _lyricsStamp);

                if (needsVersions)
                {
                    await LoadVersionsAsync(artist, title, album, duration, revision, refresh: false, token)
                        .ConfigureAwait(false);
                }
            },
            token);
    }

    /// <summary>
    /// Fetches the alternative lrclib records for the current track, for the selector window.
    /// </summary>
    private async Task LoadVersionsAsync(
        string artist,
        string title,
        string? album,
        double? durationSeconds,
        long revision,
        bool refresh,
        CancellationToken ct)
    {
        if (_lyrics is null)
        {
            return;
        }

        Interlocked.Increment(ref _versionsInFlight);
        Interlocked.Increment(ref _versionsStamp);

        try
        {
            var candidates = await _lyrics.GetVersionsAsync(artist, title, album, durationSeconds, ct, refresh)
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
            // Best effort: the selector simply offers to look again.
        }
        finally
        {
            Interlocked.Decrement(ref _versionsInFlight);
            Interlocked.Increment(ref _versionsStamp);
        }
    }

    private bool IsVersionsLoading => Volatile.Read(ref _versionsInFlight) > 0;

    /// <summary>
    /// Whether the track info is still within its on-screen time. It is the most static thing the
    /// overlay draws, so on an OLED it is the first thing worth making disappear.
    /// </summary>
    private bool HeaderVisible()
    {
        if (_config.HeaderAutoHideSeconds <= 0)
        {
            return true;
        }

        return Stopwatch.GetElapsedTime(_headerShownAt).TotalSeconds < _config.HeaderAutoHideSeconds;
    }

    /// <summary>
    /// Four positions a few pixels apart, one per minute of playback, so no pixel stays lit for the
    /// whole track. Quantised to the minute so it changes rarely instead of redrawing every tick.
    /// </summary>
    private (float X, float Y) PixelShiftOffset(TimeSpan position)
    {
        if (!_config.PixelShift)
        {
            return (0f, 0f);
        }

        return (((int)(position.TotalSeconds / 60)) % 4) switch
        {
            1 => (3f, 0f),
            2 => (0f, 3f),
            3 => (3f, 3f),
            _ => (0f, 0f)
        };
    }

    /// <summary>
    /// Compares the floats of a frame. Every one of them is quantised before it reaches the visual
    /// (progress to 1/200, the burn-in shift to whole pixels), so a tolerance far below that step
    /// compares exactly what the eye sees without testing floating-point values for exact equality.
    /// </summary>
    private static bool SameValue(float a, float b) => MathF.Abs(a - b) < 0.0001f;

    private static bool VisualEquals(OverlayVisual a, OverlayVisual b)
    {
        if (a.Mode != b.Mode
            || a.ActiveIndex != b.ActiveIndex
            || a.ShowHeader != b.ShowHeader
            || a.ShowProgressBar != b.ShowProgressBar
            || a.SungChars != b.SungChars
            || !SameValue(a.OffsetX, b.OffsetX)
            || !SameValue(a.OffsetY, b.OffsetY)
            || a.OffsetIsDefault != b.OffsetIsDefault
            || !string.Equals(a.OffsetLabel, b.OffsetLabel, StringComparison.Ordinal)
            || !string.Equals(a.Header, b.Header, StringComparison.Ordinal)
            || !string.Equals(a.Translation, b.Translation, StringComparison.Ordinal)
            || !ReferenceEquals(a.Artwork, b.Artwork)
            || a.ArtworkWidth != b.ArtworkWidth
            || a.ArtworkHeight != b.ArtworkHeight
            || a.IsPlaying != b.IsPlaying
            || !string.Equals(a.Status, b.Status, StringComparison.Ordinal)
            || !Equals(a.Notice, b.Notice)
            || a.Lines.Count != b.Lines.Count)
        {
            return false;
        }

        // A gap indicator is drawn even when the progress bar is off, so it gets its own check.
        if (!SameValue(a.GapProgress, b.GapProgress))
        {
            return false;
        }

        // Progress only warrants a redraw when it is actually being drawn; otherwise a moving
        // position would force a frame every tick for something invisible.
        if (a.ShowProgressBar
            && (!SameValue(a.LineProgress, b.LineProgress) || !SameValue(a.TrackProgress, b.TrackProgress)))
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

    private void OnHotKey(int command)
    {
        // Read the effective offset before the command runs, so the toast can tell "moved earlier"
        // from "already at the limit"; the title still reports the total now in force.
        var offsetBefore = _timing.OffsetMs;
        Execute(command);
        ShowShortcutFeedback(command, offsetBefore);
    }

    /// <summary>
    /// Confirm a global shortcut with a small toast. Only hotkeys do this: a tray selection already
    /// had a menu to explain itself, and the settings window changes settings the user is looking at.
    /// </summary>
    private void ShowShortcutFeedback(int command, int offsetBefore)
    {
        var notice = command switch
        {
            Commands.ToggleOverlay => ShortcutFeedback.ForOverlay(_overlayEnabled, _overlay?.IsVisible ?? false),
            Commands.ToggleMode => _config.Mode == OverlayMode.SingleLineWithNext
                ? ShortcutFeedback.ForPeek()
                : ShortcutFeedback.ForLayout(_config.Mode == OverlayMode.MultiLine),
            Commands.ToggleLock => ShortcutFeedback.ForLock(_locked),
            Commands.OffsetBack or Commands.OffsetForward or Commands.OffsetReset =>
                ShortcutFeedback.ForOffset(
                    _timing.OffsetMs,
                    _timing.OffsetMs - offsetBefore,
                    command == Commands.OffsetReset,
                    TimingProfiles.SourceLabel(_timing.Source, _audioOutput)),
            Commands.OpenSettings => ShortcutFeedback.ForSettings(),
            _ => null
        };

        if (notice is null)
        {
            return;
        }

        try
        {
            // Created on the first shortcut rather than at startup: the window costs a second
            // composition stack, and the people who never touch a hotkey should not pay for it.
            _toast ??= CreateToast();

            // Top and bottom are opposites on purpose: the toast must not cover the lyrics it is
            // describing. When the overlay is hidden there is nothing to avoid, so the toast uses
            // the monitor under the cursor, which is where the user's attention already is.
            _toast.Show(notice, _config.Anchor != OverlayAnchor.TopCenter, ToastMonitor());
        }
        catch (Exception)
        {
            // A shortcut still has to work when the second composition device cannot be created.
            _toast?.Dispose();
            _toast = null;
        }
    }

    private ShortcutToastWindow CreateToast()
    {
        var toast = new ShortcutToastWindow(_config.ToTheme());

        try
        {
            toast.Create();
            return toast;
        }
        catch (Exception)
        {
            // Create can fail after the HWND exists, so the half-built window is released here
            // rather than left registered with nothing owning it.
            toast.Dispose();
            throw;
        }
    }

    private nint ToastMonitor()
    {
        if (_overlay is not null && _overlay.IsVisible)
        {
            return MonitorFromWindow(_overlay.Handle, MONITOR_DEFAULTTONEAREST);
        }

        return MonitorUnderCursor();
    }

    /// <summary>
    /// Switch between the anchored lyric overlay and the full-monitor audience surface. Stage mode
    /// is display-only, so it locks the overlay; the backdrop and the larger centred text come from
    /// the staged theme, and the choice is persisted.
    /// </summary>
    private void SetStage(bool enabled)
    {
        if (_config.StageMode == enabled)
        {
            return;
        }

        _config.StageMode = enabled;

        if (enabled)
        {
            SetLocked(true);
        }

        _overlay?.SetStage(enabled);
        ApplyTheme();
        ForceRedraw();
        _config.Save();
        _settings?.RefreshIfOpen();
        _diagnosticLog.Add("stage", enabled ? (_config.StageChroma ? "on (chroma)" : "on (black)") : "off");
    }

    /// <summary>Runs a command from the command line, a second instance or the startup arguments.</summary>
    private void DispatchHostCommand(HostCommand command)
    {
        if (_overlay is null || command == HostCommand.None)
        {
            return;
        }

        switch (command)
        {
            case HostCommand.Show:
                if (!_overlayEnabled)
                {
                    Execute(Commands.ToggleOverlay);
                }

                break;

            case HostCommand.Hide:
                if (_overlayEnabled)
                {
                    Execute(Commands.ToggleOverlay);
                }

                break;

            case HostCommand.Toggle:
                Execute(Commands.ToggleOverlay);
                break;

            case HostCommand.LayoutSingle:
                SetLayout(OverlayMode.SingleLine);
                break;

            case HostCommand.LayoutPeek:
                SetLayout(OverlayMode.SingleLineWithNext);
                break;

            case HostCommand.LayoutMulti:
                SetLayout(OverlayMode.MultiLine);
                break;

            case HostCommand.LayoutNext:
                Execute(Commands.ToggleMode);
                break;

            case HostCommand.Lock:
                if (!_locked)
                {
                    SetLocked(true);
                }

                break;

            case HostCommand.Unlock:
                if (_locked)
                {
                    SetLocked(false);
                }

                break;

            case HostCommand.ToggleLock:
                Execute(Commands.ToggleLock);
                break;

            case HostCommand.OffsetEarlier:
                Execute(Commands.OffsetBack);
                break;

            case HostCommand.OffsetLater:
                Execute(Commands.OffsetForward);
                break;

            case HostCommand.OffsetReset:
                Execute(Commands.OffsetReset);
                break;

            case HostCommand.Preview:
                StartPreview();
                break;

            case HostCommand.Settings:
                OpenSettings();
                break;

            case HostCommand.Diagnostics:
                OpenDiagnostics();
                break;

            case HostCommand.RefreshLyrics:
                RefreshCurrentLyrics();
                break;

            case HostCommand.StageToggle:
                SetStage(!_config.StageMode);
                break;

            case HostCommand.StageOn:
                SetStage(true);
                break;

            case HostCommand.StageOff:
                SetStage(false);
                break;

            case HostCommand.StageGreen:
                _config.StageChroma = true;
                SetStage(true);
                break;

            case HostCommand.StageBlack:
                _config.StageChroma = false;
                SetStage(true);
                break;

            case HostCommand.PlayPause:
                TogglePlayback();
                break;

            case HostCommand.NextTrack:
                SkipNextTrack();
                break;

            case HostCommand.Exit:
                Execute(Commands.Exit);
                break;
        }

        _diagnosticLog.Add("command", command.ToString());
    }

    /// <summary>Play/pause through the session Windows is following; the next poll repaints the bar.</summary>
    private void TogglePlayback()
    {
        _diagnosticLog.Add("transport", "play/pause");

        _ = Task.Run(
            async () =>
            {
                if (_watcher is not null)
                {
                    _ = await _watcher.TogglePlayPauseAsync().ConfigureAwait(false);
                }
            },
            CancellationToken.None);
    }

    /// <summary>Skip to the next track through the same session.</summary>
    private void SkipNextTrack()
    {
        _diagnosticLog.Add("transport", "next");

        _ = Task.Run(
            async () =>
            {
                if (_watcher is not null)
                {
                    _ = await _watcher.SkipNextAsync().ConfigureAwait(false);
                }
            },
            CancellationToken.None);
    }

    private void ToggleOverlay() => Execute(Commands.ToggleOverlay);

    private void OnOpacityScrolled(int delta)
    {
        var step = delta > 0 ? 0.05 : -0.05;
        _config.BackgroundOpacity = Math.Clamp(_config.BackgroundOpacity + step, 0.0, 1.0);
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

        // Remember the placement against the display it was made on: a laptop and a docked monitor
        // have different work areas, and one global offset is wrong on one of them.
        var device = DisplayMonitors.DeviceNameFor(monitor);

        if (device.Length > 0)
        {
            _currentMonitorDevice = device;
            _config.MonitorOffsets[device] = new MonitorPlacement
            {
                X = _config.OffsetX,
                Y = _config.OffsetY
            };
        }

        _config.Save();
    }

    /// <summary>
    /// Picks the display the config asks for, loads that display's remembered placement and reports a
    /// requested display that is no longer connected so the caller can say so once.
    /// </summary>
    private nint ApplyMonitorPreference(out string? warning)
    {
        warning = null;
        var monitor = ChooseMonitor(out var device, out warning);
        _currentMonitorDevice = device;

        if (device.Length > 0 && _config.MonitorOffsets.TryGetValue(device, out var placement))
        {
            _config.OffsetX = placement.X;
            _config.OffsetY = placement.Y;
        }

        return monitor;
    }

    private nint ChooseMonitor(out string deviceName, out string? warning)
    {
        warning = null;
        deviceName = string.Empty;

        switch (_config.Monitor)
        {
            case MonitorChoice.Primary:
                if ((DisplayMonitors.Primary() ?? FirstMonitor()) is { } primary)
                {
                    deviceName = primary.DeviceName;
                    return primary.Handle;
                }

                break;

            case MonitorChoice.Specific:
                var chosen = DisplayMonitors.Find(_config.MonitorDevice);

                if (chosen is null)
                {
                    foreach (var monitor in DisplayMonitors.Enumerate())
                    {
                        if (monitor.Primary)
                        {
                            chosen = monitor;
                            break;
                        }
                    }
                }

                chosen ??= FirstMonitor();

                if (chosen is not null)
                {
                    deviceName = chosen.DeviceName;

                    if (!string.Equals(chosen.DeviceName, _config.MonitorDevice.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        warning = $"The chosen display is not connected, so the overlay uses {chosen.Label}.";
                    }

                    return chosen.Handle;
                }

                break;
        }

        var cursorMonitor = MonitorUnderCursor();
        deviceName = DisplayMonitors.DeviceNameFor(cursorMonitor);
        return cursorMonitor;
    }

    private static DisplayMonitor? FirstMonitor()
    {
        var monitors = DisplayMonitors.Enumerate();
        return monitors.Count > 0 ? monitors[0] : null;
    }

    /// <summary>What the Position section prints for the monitor row.</summary>
    private string MonitorLabel() => _config.Monitor switch
    {
        MonitorChoice.Primary => "Primary display",
        MonitorChoice.Specific => DisplayMonitors.Find(_config.MonitorDevice)?.Label ?? "Chosen display (missing)",
        _ => "Follow the cursor"
    };

    /// <summary>Cycles cursor -> primary -> each connected display, applying the choice immediately.</summary>
    private void CycleMonitor()
    {
        var options = new List<(MonitorChoice Mode, string Device)>
        {
            (MonitorChoice.Cursor, string.Empty),
            (MonitorChoice.Primary, string.Empty)
        };

        foreach (var monitor in DisplayMonitors.Enumerate())
        {
            options.Add((MonitorChoice.Specific, monitor.DeviceName));
        }

        var current = options.FindIndex(option =>
            option.Mode == _config.Monitor
            && (option.Mode != MonitorChoice.Specific
                || string.Equals(option.Device, _config.MonitorDevice, StringComparison.OrdinalIgnoreCase)));

        var next = options[(current < 0 ? 0 : current + 1) % options.Count];
        _config.Monitor = next.Mode;
        _config.MonitorDevice = next.Device;

        var monitorHandle = ApplyMonitorPreference(out var warning);
        _overlay?.SetPlacement(_config.Anchor, _config.OffsetX, _config.OffsetY, monitorHandle);
        ForceRedraw();
        _config.Save();
        _settings?.RefreshIfOpen();

        if (warning is not null)
        {
            _tray?.Alert("Overlyric \u2014 display not found", warning);
        }
    }

    /// <summary>A monitor was added, removed or rearranged: re-choose it and put the overlay back there.</summary>
    private void OnDisplayChanged()
    {
        if (_overlay is null)
        {
            return;
        }

        var monitor = ApplyMonitorPreference(out var warning);
        _overlay.SetPlacement(_config.Anchor, _config.OffsetX, _config.OffsetY, monitor);
        ForceRedraw();
        _settings?.RefreshIfOpen();

        if (warning is not null)
        {
            _tray?.Alert("Overlyric \u2014 display changed", warning);
        }
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

        // Source-menu entries carry the index of the entry they were built for, in their own id range.
        if (command >= Commands.PinSourceBase && command < Commands.PinSourceEnd)
        {
            PinSource(command - Commands.PinSourceBase);
            return;
        }

        switch (command)
        {
            case Commands.ToggleOverlay:
                _overlayEnabled = !_overlayEnabled;

                // Turning the overlay back on mid-song is the second "overlay starts" moment: the
                // notice tells the listener what this overlay is showing before the next line.
                if (_overlayEnabled)
                {
                    RequestInfoPopup(_snapshot);
                }

                // Visibility is applied on the tick, so without this the overlay would take up to a
                // second to answer the click that just asked for it.
                ForceRedraw();
                break;

            case Commands.ToggleMode:
                _config.Mode = _config.Mode switch
                {
                    OverlayMode.SingleLine => OverlayMode.SingleLineWithNext,
                    OverlayMode.SingleLineWithNext => OverlayMode.MultiLine,
                    _ => OverlayMode.SingleLine
                };
                _overlay.SetMode(_config.Mode);
                ForceRedraw();
                _config.Save();
                break;

            case Commands.LayoutSingle:
                SetLayout(OverlayMode.SingleLine);
                break;

            case Commands.LayoutPeek:
                SetLayout(OverlayMode.SingleLineWithNext);
                break;

            case Commands.LayoutMulti:
                SetLayout(OverlayMode.MultiLine);
                break;

            case Commands.OpenLyricSheet:
                OpenLyricViewer();
                break;

            case Commands.ToggleLock:
                SetLocked(!_locked);
                break;

            case Commands.ToggleStage:
                SetStage(!_config.StageMode);
                break;

            case Commands.PlayPause:
                TogglePlayback();
                break;

            case Commands.NextTrack:
                SkipNextTrack();
                break;

            case Commands.StageBlack:
                _config.StageChroma = false;
                SetStage(true);
                break;

            case Commands.StageChromaGreen:
                _config.StageChroma = true;
                SetStage(true);
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
                ResetLyricsOffset();
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
                _config.BackgroundOpacity = Math.Clamp(_config.BackgroundOpacity + 0.05, 0.0, 1.0);
                ApplyTheme();
                _config.Save();
                break;

            case Commands.OpacityDown:
                _config.BackgroundOpacity = Math.Clamp(_config.BackgroundOpacity - 0.05, 0.0, 1.0);
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

            case Commands.OpenLyricPicker:
                OpenLyricPicker();
                break;

            case Commands.RefreshLyrics:
                RefreshCurrentLyrics();
                break;

            case Commands.PreviewOverlay:
                StartPreview();
                break;

            case Commands.OpenSettings:
                OpenSettings();
                break;

            case Commands.OpenDiagnostics:
                OpenDiagnostics();
                break;

            case Commands.SourceAutomatic:
                SetPinnedSource("");
                break;

            case Commands.SourcePriorityOrder:
                OpenSourceSettings();
                break;

            case Commands.Exit:
                Shutdown();
                break;
        }
    }

    /// <summary>
    /// Switch to one named layout, and redraw only when it changed. The tray's three rows, the hotkey
    /// cycle and the bar's chip all end up here so they cannot disagree about what the mode is.
    /// </summary>
    private void SetLayout(OverlayMode mode)
    {
        if (_config.Mode == mode)
        {
            return;
        }

        _config.Mode = mode;
        _overlay?.SetMode(mode);
        ForceRedraw();
        _config.Save();
    }

    /// <summary>
    /// Pin the player the source menu listed at <paramref name="index"/>. The watcher re-reads the
    /// setting on its next poll, so the change lands within a second without touching the watcher or
    /// restarting the application.
    /// </summary>
    private void PinSource(int index)
    {
        if (index < 0 || index >= _menuSources.Count)
        {
            return;
        }

        SetPinnedSource(_menuSources[index].Aumid);
    }

    /// <summary>Remember which player to follow, and persist it: a pin is a setting, not a mood.</summary>
    private void SetPinnedSource(string aumid)
    {
        _config.SourcePinned = aumid;
        _config.Save();
    }

    /// <summary>
    /// Ask lrclib with words the listener typed, rather than the metadata the player published. The
    /// answer is shown in the complete-lyrics window, where the question was asked.
    /// </summary>
    private void SearchLyrics(string query)
    {
        var snapshot = _snapshot;
        if (IsDemoActive || !snapshot.HasTrack || _lyrics is null || string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        query = query.Trim();

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        var revision = snapshot.Revision;
        var duration = snapshot.DurationSeconds;

        lock (_gate)
        {
            _searchRevision = revision;
            _searchLoading = true;
            _searchResults = [];
            _searchMessage = null;
        }

        Interlocked.Increment(ref _lyricsStamp);

        _ = Task.Run(
            async () =>
            {
                LyricsSearchOutcome outcome = default;

                try
                {
                    outcome = await _lyrics.SearchAsync(query, duration, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // The service turns a failure into an unavailable outcome; this guards anything
                    // it did not anticipate.
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                var candidates = outcome.Candidates ?? [];

                lock (_gate)
                {
                    if (_lyricsRevision != revision || _searchRevision != revision)
                    {
                        return;
                    }

                    _searchLoading = false;
                    _searchResults = candidates;

                    if (!outcome.Available)
                    {
                        _searchMessage = "lrclib could not answer just now. Try again in a moment.";
                    }
                    else if (candidates.Count == 0)
                    {
                        _searchMessage = "No records for that search. Try fewer words, or the song title on its own.";
                    }
                    else
                    {
                        _searchMessage = null;
                    }
                }

                Interlocked.Increment(ref _lyricsStamp);
                RefreshPicker();
            },
            token);
    }

    /// <summary>
    /// Stop a manual search that is no longer the right question. <paramref name="clear"/> also drops
    /// what the previous one found, which is what a track change wants; a chosen record keeps the
    /// list, so reopening the search shows the alternatives again.
    /// </summary>
    private void CancelSearch(bool clear)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        lock (_gate)
        {
            _searchLoading = false;

            if (clear)
            {
                _searchResults = [];
                _searchMessage = null;
                _searchRevision = -1;
            }
        }

        Interlocked.Increment(ref _lyricsStamp);
    }

    /// <summary>
    /// Pin the chosen lrclib record for this track and re-resolve straight away. A null id means the
    /// automatic pick, which is the row at the top of the selector.
    /// </summary>
    private void ChooseVersion(long? lrclibId)
    {
        var snapshot = _snapshot;
        if (!snapshot.HasTrack || _lyrics is null)
        {
            return;
        }

        var hasPinned = _lyrics.TryGetPinnedVersion(
            snapshot.Artist,
            snapshot.Title,
            snapshot.Album,
            out var pinnedId);

        if (lrclibId is { } id)
        {
            // Clicking the record that is already pinned is not a reason to ask lrclib again.
            if (hasPinned && pinnedId == id)
            {
                return;
            }

            _lyrics.PinVersion(snapshot.Artist, snapshot.Title, snapshot.Album, id);
        }
        else
        {
            if (!hasPinned)
            {
                return;
            }

            _lyrics.ClearVersion(snapshot.Artist, snapshot.Title, snapshot.Album);
        }

        CancelSearch(clear: false);
        StartLyricsFetch(snapshot);
    }

    /// <summary>
    /// Ask for the alternative records again, discarding everything already known about them. This
    /// is what the selector's <c>Look again</c> means.
    /// </summary>
    private void RefreshVersions() => RefreshVersions(refresh: true);

    /// <summary>
    /// Fetch the alternative records. With <paramref name="refresh"/> false this still uses a list
    /// the previous session stored, which is why opening the selector on an already-played track
    /// costs no request at all.
    /// </summary>
    private void RefreshVersions(bool refresh)
    {
        var snapshot = _snapshot;
        if (!snapshot.HasTrack || _lyrics is null || IsVersionsLoading)
        {
            return;
        }

        _versionsAttemptedRevision = snapshot.Revision;

        lock (_gate)
        {
            _candidates = [];
            _candidatesRevision = -1;
        }

        Interlocked.Increment(ref _versionsStamp);

        _ = LoadVersionsAsync(
            snapshot.Artist,
            snapshot.Title,
            snapshot.Album,
            snapshot.DurationSeconds,
            snapshot.Revision,
            refresh,
            CancellationToken.None);
    }

    /// <summary>
    /// Drive the overlay from the canned demo track for a few seconds. This is the tray equivalent of
    /// starting with --demo, without restarting the process or disturbing the persisted layout.
    /// </summary>
    private void StartPreview()
    {
        if (_overlay is null)
        {
            return;
        }

        if (_demo)
        {
            // --demo already runs the same track indefinitely; restarting the clock is the only
            // useful thing a preview command can do there.
            _demoClock.Restart();
            ForceRedraw();
            return;
        }

        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        _fetchCts = null;

        _overlayEnabled = true;
        _previewUntil = Environment.TickCount64 + PreviewDurationMs;
        _previewActive = true;
        _lastRevision = long.MinValue;
        _demoClock.Restart();

        lock (_gate)
        {
            _lines = DemoLines;
            _lyricsSynced = true;
            _lyricsStatus = null;
            _isInstrumental = false;
            _fetchCompleted = true;
            _lyricsWordSynced = false;
            _lyricsRevision = DemoSnapshot.Revision;
            _lyricsRecordId = null;
        }

        Interlocked.Increment(ref _lyricsStamp);
        ForceRedraw();
    }

    /// <summary>
    /// Leave the preview and force the next tick to resolve the real track again. Clearing the fetch
    /// state first is deliberate: the old lyric lines belong to the demo, not to whatever is playing.
    /// </summary>
    private void EndPreview()
    {
        _previewUntil = 0;
        _lastRevision = -1;

        lock (_gate)
        {
            _lines = [];
            _translation = [];
            _lyricsSynced = false;
            _lyricsStatus = null;
            _isInstrumental = false;
            _fetchCompleted = false;
            _lyricsWordSynced = false;
            _lyricsRevision = -1;
            _lyricsRecordId = null;
        }

        Interlocked.Increment(ref _lyricsStamp);
    }

    /// <summary>
    /// Forget the current track's cached answer and resolve it again. A pinned record is re-fetched
    /// under its own key, so refreshing never silently turns a manual choice back into automatic.
    /// </summary>
    private void RefreshCurrentLyrics()
    {
        var snapshot = _snapshot;
        if (!snapshot.HasTrack || _lyrics is null || IsDemoActive)
        {
            return;
        }

        _lyrics.ForgetCached(snapshot.Artist, snapshot.Title, snapshot.Album);
        _lyricsRetryAttempt = 0;
        _nextLyricsRetryAt = 0;
        StartLyricsFetch(snapshot);
    }

    /// <summary>
    /// Say once per track that an imported local file has disappeared. The lookup then answers from
    /// the automatic pipeline, and without a sentence the listener would think the import was lost.
    /// </summary>
    private void UpdateLocalFileAlert()
    {
        var revision = _lastRevision;

        if (_localFileMissingRevision != revision || _alertedMissingLocalRevision == revision)
        {
            return;
        }

        _alertedMissingLocalRevision = revision;
        _tray?.Alert(
            "Overlyric — local lyric file is missing",
            "The imported file for this track is gone, so Overlyric fell back to lrclib. Import it again from Versions to restore the local sheet.");
    }

    /// <summary>Drop every cached lyric answer. The next lookup for each track asks lrclib again.</summary>
    private void ClearLyricsCache()
    {
        _lyrics?.ClearCache();
        ForceRedraw();
    }

    /// <summary>Cache size for the Advanced section; zero in demo mode, where no cache is created.</summary>
    private (int Entries, long Bytes) CacheStats() => _lyrics?.GetCacheStats() ?? (0, 0);

    /// <summary>Local lyric file count and size for the Advanced section.</summary>
    private (int Entries, long Bytes) LocalLibraryStats() => _localLyrics?.GetStats() ?? (0, 0);

    /// <summary>Open the import dialog and stage the file's preview; nothing is stored until accepted.</summary>
    private void ImportLyricFile()
    {
        var snapshot = _snapshot;

        if (!snapshot.HasTrack || IsDemoActive || _lyrics is null)
        {
            return;
        }

        var path = FileDialogs.OpenLyricFile(_picker?.Handle ?? nint.Zero);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var preview = LyricsService.PreviewImport(path, snapshot.DurationSeconds);

        if (!preview.Ok)
        {
            SetImportMessage(preview.Error ?? "That file could not be read.", snapshot.Revision);
            return;
        }

        _importPreview = preview;
        _importSourcePath = path;
        _importPreviewRevision = snapshot.Revision;
        _importMessage = null;
        RefreshPicker();
    }

    /// <summary>Store the staged import, make it this track's explicit choice and resolve it.</summary>
    private void AcceptLyricImport()
    {
        var snapshot = _snapshot;
        var preview = _importPreview;

        if (preview is null || _importPreviewRevision != snapshot.Revision || _lyrics is null)
        {
            CancelLyricImport();
            return;
        }

        var sourcePath = _importSourcePath ?? preview.FileName;

        if (!_lyrics.TryCommitImport(
                snapshot.Artist,
                snapshot.Title,
                snapshot.Album,
                preview,
                sourcePath,
                out var error))
        {
            SetImportMessage(error ?? "The import could not be saved.", snapshot.Revision);
            return;
        }

        _importPreview = null;
        _importSourcePath = null;
        _importMessage = "Imported and selected for this track.";
        StartLyricsFetch(snapshot);
        RefreshPicker();
    }

    private void CancelLyricImport()
    {
        _importPreview = null;
        _importSourcePath = null;
        _importPreviewRevision = -1;
        _importMessage = null;
        RefreshPicker();
    }

    /// <summary>
    /// Pin a record whose id (or record URL) the listener put on the clipboard. This is the no-text
    /// path for the wrong-query case: the listener finds the record in a browser and pastes the id.
    /// </summary>
    private void PinLyricFromClipboard()
    {
        var snapshot = _snapshot;
        if (!snapshot.HasTrack || IsDemoActive)
        {
            return;
        }

        var text = Clipboard.ReadText(_picker?.Handle ?? _messageWindow.Handle);
        var id = ExtractRecordId(text);

        if (id <= 0)
        {
            SetImportMessage("No lrclib record id found in the clipboard text.", snapshot.Revision);
            return;
        }

        ChooseVersion(id);
        SetImportMessage($"Pinned lrclib #{id}.", snapshot.Revision);
    }

    /// <summary>Open the listener's own words on lrclib's search page, for the query that failed here.</summary>
    private void OpenLyricsOnLrclib()
    {
        var snapshot = _snapshot;
        if (!snapshot.HasTrack)
        {
            return;
        }

        var query = string.Join(
            ' ',
            new[] { snapshot.Artist, snapshot.Title }.Where(static part => !string.IsNullOrWhiteSpace(part)));

        if (query.Length == 0)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://lrclib.net/search/" + Uri.EscapeDataString(query),
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // No browser is not worth a dialog; the listener can copy the query by hand.
        }
    }

    /// <summary>
    /// Opens the track's local lyric file in Notepad (the OS editor, not an in-app text box) and
    /// watches its write time. The first time a non-local sheet is edited this saves it locally, so
    /// the file exists and is the track's selection before it is opened.
    /// </summary>
    private void OpenInExternalEditor(IReadOnlyList<LyricLine> lines)
    {
        var snapshot = _snapshot;

        if (!snapshot.HasTrack || _lyrics is null || lines.Count == 0)
        {
            return;
        }

        if (!_lyrics.TryEnsureLocalSheet(
                snapshot.Artist,
                snapshot.Title,
                snapshot.Album,
                snapshot.DurationSeconds,
                lines,
                out var path,
                out var error))
        {
            _tray?.Alert("Overlyric — cannot edit the file", error ?? "The lyric file could not be written.");
            return;
        }

        try
        {
            _externalEditPath = path;
            _externalEditStamp = File.GetLastWriteTimeUtc(path);

            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "notepad.exe"),
                Arguments = string.Concat("\"", path, "\""),
                UseShellExecute = true
            });

            _tray?.Alert(
                "Overlyric — editing the lyric file",
                "Save in Notepad and Overlyric reloads the timing when the file changes.");
        }
        catch (Exception)
        {
            _externalEditPath = null;
            _tray?.Alert("Overlyric — cannot open the editor", "Notepad could not be started.");
        }
    }

    /// <summary>
    /// Last-write check for the sheet opened in the external editor. A file that no longer parses as
    /// timed lyrics is refused: the previous sheet stays, and one sentence says why.
    /// </summary>
    private void CheckExternalEditReload()
    {
        var path = _externalEditPath;

        if (path is null || _lyrics is null)
        {
            return;
        }

        try
        {
            if (!File.Exists(path))
            {
                _externalEditPath = null;
                _tray?.Alert("Overlyric — the lyric file is gone", "The previous sheet is still on screen.");
                return;
            }

            var stamp = File.GetLastWriteTimeUtc(path);

            if (stamp == _externalEditStamp)
            {
                return;
            }

            _externalEditStamp = stamp;
            var preview = LyricsService.PreviewImport(path, _snapshot.DurationSeconds);

            if (!preview.Ok || !preview.IsSynced)
            {
                _tray?.Alert(
                    "Overlyric — the edited file was not loaded",
                    "It no longer contains timed lyric lines, so the previous sheet is kept.");
                return;
            }

            StartLyricsFetch(_snapshot);
            _tray?.Alert("Overlyric — lyrics reloaded", "The sheet reloaded from the edited file.");
        }
        catch (Exception)
        {
            // A file being written by another process can fail to stat; try again on the next tick.
        }
    }

    /// <summary>
    /// Sends one confirmed sheet to LRCLIB. The validation runs again here even though the viewer
    /// disabled the button, because a guard that only exists in the UI is not a guard. Solving and
    /// sending happen on a task; the result is picked up by the tick, on the thread that owns the
    /// windows and the tray.
    /// </summary>
    private void PublishLyrics(IReadOnlyList<LyricLine> lines)
    {
        var snapshot = _snapshot;

        if (!snapshot.HasTrack
            || snapshot.DurationSeconds is not { } duration
            || _publisher is null
            || _publishing)
        {
            return;
        }

        var errors = PublishValidation.Validate(snapshot.Title, snapshot.Artist, snapshot.DurationSeconds, lines);

        if (errors.Count > 0)
        {
            _viewer?.SetTransientStatus(string.Join(" ", errors));
            return;
        }

        var request = PublishValidation.BuildRequest(
            snapshot.Title,
            snapshot.Artist,
            snapshot.Album,
            duration,
            lines);

        _publishCts?.Dispose();
        _publishCts = new CancellationTokenSource();
        var token = _publishCts.Token;
        _publishing = true;
        _viewer?.SetTransientStatus("Contacting LRCLIB…");

        var progress = new Progress<long>(attempts =>
            _viewer?.SetTransientStatus($"Solving the publish challenge… {attempts / 1_000_000.0:0.0} M tries"));

        _ = Task.Run(
            async () =>
            {
                var result = await _publisher.PublishAsync(request, progress, token).ConfigureAwait(false);
                _publishResult = result;
                _publishCompleted = true;
            },
            token);
    }

    /// <summary>Shows the background publish's result on the UI thread, once.</summary>
    private void UpdatePublishResult()
    {
        if (!_publishCompleted)
        {
            return;
        }

        _publishCompleted = false;
        _publishing = false;
        var (outcome, message) = _publishResult;

        _viewer?.SetTransientStatus(message);
        _diagnosticLog.Add("publish", string.Concat(outcome.ToString(), ": ", message));
        _tray?.Alert(
            outcome == PublishOutcome.Published ? "Overlyric \u2014 lyrics published" : "Overlyric \u2014 publish failed",
            message);
    }

    /// <summary>The position "now", which is what the timing editor stamps a line with.</summary>
    private TimeSpan CurrentPosition() => IsDemoActive
        ? DemoPosition()
        : _watcher?.Clock.Position ?? TimeSpan.Zero;

    /// <summary>Writes the lines the editor currently shows, edited or not, as an LRC file.</summary>
    private void ExportLyrics(IReadOnlyList<LyricLine> lines)
    {
        var snapshot = _snapshot;

        if (!snapshot.HasTrack || lines.Count == 0)
        {
            return;
        }

        var path = FileDialogs.SaveLyricFile(
            _viewer?.Handle ?? nint.Zero,
            SuggestedExportName(snapshot.Artist, snapshot.Title));

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            var text = LyricTextCodec.EncodeLrc(
                snapshot.Artist,
                snapshot.Title,
                snapshot.Album,
                snapshot.DurationSeconds,
                lines);

            File.WriteAllBytes(path, LyricTextCodec.EncodeUtf8(text));
            _tray?.Alert("Overlyric — lyrics exported", path);
        }
        catch (Exception)
        {
            _tray?.Alert("Overlyric — export failed", "The file could not be written.");
        }
    }

    /// <summary>
    /// Saves the timing editor's lines as this track's local sheet and re-resolves, so the overlay
    /// follows the edit immediately. Returns null on success, or the sentence to show in the viewer.
    /// </summary>
    private string? SaveEditedLyrics(IReadOnlyList<LyricLine> lines)
    {
        var snapshot = _snapshot;

        if (!snapshot.HasTrack || _lyrics is null)
        {
            return "There is no track to save against.";
        }

        if (!_lyrics.TrySaveEdited(
                snapshot.Artist,
                snapshot.Title,
                snapshot.Album,
                snapshot.DurationSeconds,
                lines,
                out var error))
        {
            return error ?? "The edited sheet could not be saved.";
        }

        StartLyricsFetch(snapshot);
        return null;
    }

    private void SetImportMessage(string message, long revision)
    {
        _importMessage = message;
        _importPreviewRevision = revision;
        RefreshPicker();
    }

    private void RefreshPicker() => _picker?.Invalidate();

    /// <summary>"Artist - Title.lrc", with characters Windows does not allow in a name replaced.</summary>
    private static string SuggestedExportName(string artist, string title)
    {
        var raw = string.Join(
            " - ",
            new[] { artist, title }.Where(static part => !string.IsNullOrWhiteSpace(part))).Trim();

        if (raw.Length == 0)
        {
            raw = "lyrics";
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            raw = raw.Replace(invalid, '_');
        }

        return (raw.Length > 80 ? raw[..80] : raw) + ".lrc";
    }

    /// <summary>
    /// Pulls an lrclib record id out of clipboard text: a bare number, or the digits in a record URL.
    /// A run shorter than five digits is ignored, so arbitrary copied text is not pinned by accident.
    /// </summary>
    private static long ExtractRecordId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var trimmed = text.Trim();

        if (long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var direct) && direct > 0)
        {
            return direct;
        }

        long value = 0;
        var digits = 0;

        foreach (var character in trimmed)
        {
            if (char.IsAsciiDigit(character))
            {
                if (digits < 18)
                {
                    value = (value * 10) + (character - '0');
                }

                digits++;
                continue;
            }

            if (digits >= 5)
            {
                return value;
            }

            digits = 0;
            value = 0;
        }

        return digits >= 5 ? value : 0;
    }

    /// <summary>
    /// Nudge the timing. With "Remember this track" on (the default) the value is saved for the
    /// current track; with it off, without usable metadata, or with Shift held, the nudge lasts only
    /// for this session, so a one-off correction never silently rewrites the next play.
    /// </summary>
    private void NudgeLyrics(int deltaMs)
    {
        if (_watcher is null || !_config.RememberTrackOffset || _trackKey is null || ShiftHeld())
        {
            _sessionOffsetMs = TimingProfiles.NextSessionOffset(_sessionOffsetMs, deltaMs);
        }
        else
        {
            var resolved = TimingProfiles.Resolve(
                _config.LyricsOffsetMs,
                _config.TrackOffsets,
                _trackKey,
                _config.OutputOffsets,
                _audioOutput);

            _config.TrackOffsets[_trackKey] = TimingProfiles.NextTrackOffset(resolved, _sessionOffsetMs, deltaMs);
            _sessionOffsetMs = 0;
            _config.Save();
        }

        ApplyLyricOffset();
        ForceRedraw();
        _settings?.RefreshIfOpen();
        _diagnosticLog.Add(
            "timing",
            string.Concat(
                deltaMs.ToString("+#;-#;0", System.Globalization.CultureInfo.InvariantCulture),
                " ms -> ",
                TimingProfiles.Describe(_timing, _audioOutput)));
    }

    /// <summary>
    /// Reset to zero for the situation on screen: drop this track's override, drop this output's
    /// profile, and clear the global default a session nudge may have been riding on.
    /// </summary>
    private void ResetLyricsOffset()
    {
        _config.LyricsOffsetMs = 0;
        _sessionOffsetMs = 0;

        if (_trackKey is not null)
        {
            _config.TrackOffsets.Remove(_trackKey);
        }

        if (_audioOutput is { IsKnown: true })
        {
            _config.OutputOffsets.Remove(_audioOutput.Id);
        }

        ApplyLyricOffset();
        ForceRedraw();
        _settings?.RefreshIfOpen();
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
        // The renderer takes a new theme in place. Rebuilding the window here used to make every
        // opacity step flash the overlay away, which is unusable from a slider.
        var theme = _config.ToTheme();
        _overlay?.SetTheme(theme);
        _toast?.SetTheme(theme);
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

    private static void OpenFolder(string path)
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

    /// <summary>
    /// Show one page of the single shell window. The page being replaced hands over its screen
    /// rectangle, so the swap happens in place: one window is visible at a time and it does not
    /// move or resize just because the destination changed. The launcher rows and every title bar
    /// land here, so the two lists cannot drift.
    /// </summary>
    private void OpenShell(WindowDestination destination)
    {
        if (_overlay is null)
        {
            return;
        }

        EnsureShellWindow(destination);
        var bounds = ShellBounds();

        if (bounds is { } existing)
        {
            CreateOrPrepareShell(destination, existing);
        }

        switch (destination)
        {
            case WindowDestination.Lyrics:
                ShowLyricViewerWindow();
                break;

            case WindowDestination.Versions:
                ShowLyricPickerWindow();
                break;

            case WindowDestination.Diagnostics:
                ShowDiagnosticsWindow();
                break;

            case WindowDestination.Settings:
                ShowSettingsWindow();
                break;
        }

        HideInactiveShellWindows(destination);
        AfterShowShellWindow(destination);
        RefreshShellWindow(destination);
        _lastDestination = destination;

        if (ShellHandle(destination) is var handle && handle != nint.Zero && GetWindowRect(handle, out var actual))
        {
            _shellRect = actual;
        }
    }

    /// <summary>
    /// The rectangle the next page should take: the visible window's, or the last page's remembered
    /// rectangle when the shell is hidden.
    /// </summary>
    private RECT? ShellBounds()
    {
        var handle = VisibleShellHandle();

        if (handle == nint.Zero)
        {
            handle = ShellHandle(_lastDestination);
        }

        if (handle != nint.Zero && GetWindowRect(handle, out var rect) && rect.Width > 0 && rect.Height > 0)
        {
            return rect;
        }

        return _shellRect;
    }

    private nint VisibleShellHandle()
    {
        if (_viewer is { IsOpen: true })
        {
            return _viewer.Handle;
        }

        if (_picker is { IsOpen: true })
        {
            return _picker.Handle;
        }

        if (_diagnostics is { IsOpen: true })
        {
            return _diagnostics.Handle;
        }

        return _settings is { IsOpen: true } ? _settings.Handle : nint.Zero;
    }

    private nint ShellHandle(WindowDestination destination) => destination switch
    {
        WindowDestination.Lyrics => _viewer?.Handle ?? nint.Zero,
        WindowDestination.Versions => _picker?.Handle ?? nint.Zero,
        WindowDestination.Diagnostics => _diagnostics?.Handle ?? nint.Zero,
        WindowDestination.Settings => _settings?.Handle ?? nint.Zero,
        _ => nint.Zero
    };

    private void EnsureShellWindow(WindowDestination destination)
    {
        switch (destination)
        {
            case WindowDestination.Lyrics:
                EnsureLyricViewerWindow();
                break;

            case WindowDestination.Versions:
                EnsureLyricPickerWindow();
                break;

            case WindowDestination.Diagnostics:
                EnsureDiagnosticsWindow();
                break;

            case WindowDestination.Settings:
                EnsureSettingsWindow();
                break;
        }
    }

    /// <summary>
    /// Create the page hidden at the shell's rectangle if it is new, or move the hidden page there
    /// before it is shown. Either way the first frame lands in the right place.
    /// </summary>
    private void CreateOrPrepareShell(WindowDestination destination, RECT rect)
    {
        switch (destination)
        {
            case WindowDestination.Lyrics:
                _viewer?.CreateForShell(rect);
                break;

            case WindowDestination.Versions:
                _picker?.CreateForShell(rect);
                break;

            case WindowDestination.Diagnostics:
                _diagnostics?.CreateForShell(rect);
                break;

            case WindowDestination.Settings:
                _settings?.CreateForShell(rect);
                break;
        }
    }

    /// <summary>
    /// Work that belongs after the page is on screen and the previous one is off it: the selector
    /// asks lrclib for alternatives here, because that lookup can take long enough to keep the old
    /// page visible if it ran before the swap was finished.
    /// </summary>
    private void AfterShowShellWindow(WindowDestination destination)
    {
        if (destination == WindowDestination.Versions)
        {
            EnsureVersionsForPicker();
        }
    }

    /// <summary>Redraw after the other pages hid, so their rails disappear from the menu at once.</summary>
    private void RefreshShellWindow(WindowDestination destination)
    {
        switch (destination)
        {
            case WindowDestination.Lyrics:
                _viewer?.Invalidate();
                break;

            case WindowDestination.Versions:
                _picker?.Invalidate();
                break;

            case WindowDestination.Diagnostics:
                _diagnostics?.Invalidate();
                break;

            case WindowDestination.Settings:
                _settings?.RefreshIfOpen();
                break;
        }
    }

    /// <summary>One window at a time: the page that was showing steps aside before the next paints.</summary>
    private void HideInactiveShellWindows(WindowDestination keep)
    {
        if (keep != WindowDestination.Lyrics)
        {
            _viewer?.HidePanel();
        }

        if (keep != WindowDestination.Versions)
        {
            _picker?.HidePanel();
        }

        if (keep != WindowDestination.Diagnostics)
        {
            _diagnostics?.HidePanel();
        }

        if (keep != WindowDestination.Settings)
        {
            _settings?.Hide();
        }
    }

    /// <summary>The four entry points below are the public "go to this page" verbs; all route here.</summary>
    private void OpenSettings() => OpenShell(WindowDestination.Settings);

    private void OpenDiagnostics() => OpenShell(WindowDestination.Diagnostics);

    private void OpenLyricPicker() => OpenShell(WindowDestination.Versions);

    private void OpenLyricViewer() => OpenShell(WindowDestination.Lyrics);

    /// <summary>True while the destination's window is on screen; drives the OPEN tag and the rails.</summary>
    private bool IsWindowOpen(WindowDestination destination) => destination switch
    {
        WindowDestination.Lyrics => _viewer?.IsOpen == true,
        WindowDestination.Versions => _picker?.IsOpen == true,
        WindowDestination.Diagnostics => _diagnostics?.IsOpen == true,
        WindowDestination.Settings => _settings?.IsOpen == true,
        _ => false
    };

    /// <summary>
    /// The notification-area icon's left click: the launcher, the one place every window is opened
    /// from. Right-click still opens the playback and overlay menu.
    /// </summary>
    private void ShowLauncher()
    {
        if (_overlay is null)
        {
            return;
        }

        _launcher ??= new TrayLauncherWindow(
            BuildLauncherModel,
            _navigation,
            () => Execute(Commands.ToggleOverlay),
            () => Execute(Commands.ToggleLock),
            () => Execute(Commands.ToggleStage),
            Shutdown,
            () => _tray is not null && _tray.TryGetRect(out var rect) ? rect : (RECT?)null);

        _launcher.ShowLauncher();
    }

    /// <summary>Pulled on every show so the flyout never prints a track that has already changed.</summary>
    private TrayLauncherModel BuildLauncherModel()
    {
        var snapshot = _snapshot;

        return new TrayLauncherModel(
            snapshot.HasTrack,
            snapshot.Title,
            snapshot.Artist,
            snapshot.HasTrack ? snapshot.SourceAppFriendly : "",
            _overlayEnabled,
            _locked,
            _config.StageMode,
            typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "?");
    }

    /// <summary>Show the configuration window, creating it the first time it is asked for.</summary>
    private void EnsureSettingsWindow()
    {
        if (_settings is not null)
        {
            return;
        }

        _settings = new SettingsWindow(
                _config,
                ApplySettings,
                AssignShortcut,
                name => _unavailableShortcuts.Contains(name),
                () => OpenFolder(AppConfig.DataDirectory),
                () => OpenFolder(AppConfig.CacheDirectory),
                CacheStats,
                ClearLyricsCache,
                LocalLibraryStats,
                () => OpenFolder(AppConfig.LyricsDirectory),
                MonitorLabel,
                CycleMonitor,
                ForegroundAppLabel,
                AddForegroundApp,
                ClearHideApps,
                typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                () => _watcher?.LiveSources ?? [],
                new TimingCommands
                {
                    View = BuildTimingView,
                    Nudge = NudgeLyrics,
                    Reset = ResetLyricsOffset,
                    UseGlobal = UseGlobalTiming,
                    ClearTrack = ClearTrackTiming,
                    NudgeOutput = NudgeOutputTiming
                });

        _settings.Navigation = _navigation;
    }

    private void ShowSettingsWindow()
    {
        EnsureSettingsWindow();
        _settings!.Show();
    }

    /// <summary>Open the diagnostics window: what is being shown, and why, plus copy-as-JSON.</summary>
    private void EnsureDiagnosticsWindow()
    {
        _diagnostics ??= new DiagnosticsWindow(BuildDiagnosticsSnapshot, () => OpenFolder(AppConfig.DataDirectory));
        _diagnostics.Navigation = _navigation;
    }

    private void ShowDiagnosticsWindow()
    {
        EnsureDiagnosticsWindow();
        _diagnostics!.ShowDiagnostics();
    }

    /// <summary>The live facts the diagnostics window paints; pulled on each draw.</summary>
    private DiagnosticsSnapshot BuildDiagnosticsSnapshot()
    {
        NowPlayingSnapshot snapshot = _snapshot;
        LyricLine[] lines;
        string? status;
        bool synced;
        bool wordSynced;
        bool instrumental;
        bool complete;
        long? recordId;

        lock (_gate)
        {
            lines = _lines;
            status = _lyricsStatus;
            synced = _lyricsSynced;
            wordSynced = _lyricsWordSynced;
            instrumental = _isInstrumental;
            complete = _fetchCompleted;
            recordId = _lyricsRecordId;
        }

        var position = IsDemoActive ? DemoPosition() : _watcher?.Clock.Position ?? TimeSpan.Zero;
        var clock = _watcher?.Clock;
        var output = _audioOutput?.Name ?? "No output device";

        return new DiagnosticsSnapshot
        {
            CapturedAt = DateTimeOffset.Now,
            Version = typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "?",
            HasSession = snapshot.HasTrack,
            SourceApp = snapshot.HasTrack ? snapshot.SourceAppFriendly : null,
            Title = snapshot.Title,
            Artist = snapshot.Artist,
            Album = snapshot.Album,
            IsPlaying = snapshot.IsPlaying,
            DurationSeconds = snapshot.DurationSeconds,
            Position = LyricText.FormatTimestamp(position),
            ClockIsLive = clock?.TimelineIsLive ?? IsDemoActive,
            PositionIsUsable = clock?.PositionIsUsable ?? true,
            SeekCount = clock?.SeekCount ?? 0,
            LyricSource = complete ? _lyricsSource.ToString() : "looking up",
            RecordId = recordId,
            LineCount = lines.Length,
            Synced = synced,
            WordSynced = wordSynced,
            Instrumental = instrumental,
            LyricStatus = status,
            CacheEntries = CacheStats().Entries,
            CacheBytes = CacheStats().Bytes,
            LocalFiles = LocalLibraryStats().Entries,
            LocalBytes = LocalLibraryStats().Bytes,
            OffsetMs = _timing.OffsetMs,
            OffsetLabel = TimingProfiles.FormatOffset(_timing.OffsetMs),
            OffsetSource = TimingProfiles.SourceLabel(_timing.Source, _audioOutput),
            OutputDevice = output,
            OutputBluetooth = _audioOutput?.IsBluetooth ?? false,
            Monitor = MonitorLabel(),
            ForegroundApp = string.IsNullOrWhiteSpace(_foregroundPath) ? null : Path.GetFileName(_foregroundPath),
            HiddenForApp = PresenceRules.IsHiddenForApp(_config.HideApps, _foregroundPath),
            FullscreenNotice = _fullscreenNotice,
            MicrophoneInUse = _microphoneInUse,
            Log = _diagnosticLog.Recent(24)
        };
    }

    /// <summary>Open the settings window straight on the source list, from the tray menu.</summary>
    private void OpenSourceSettings()
    {
        OpenSettings();
        _settings?.ShowPlayersSection();
    }

    /// <summary>
    /// Open the lyric selector. It is created once and hidden on close, so reopening is instant; the
    /// monitor is the one the overlay is on, because that is the screen the click came from.
    /// </summary>
    private void EnsureLyricPickerWindow()
    {
        if (_picker is not null)
        {
            return;
        }

        _picker = new LyricPickerWindow(
            BuildPickerModel,
            ChooseVersion,
            RefreshVersions,
            OpenLyricViewer,
            ImportLyricFile,
            OpenLyricsOnLrclib,
            PinLyricFromClipboard,
            AcceptLyricImport,
            CancelLyricImport,
            SearchLyrics);

        _picker.Navigation = _navigation;
    }

    private void ShowLyricPickerWindow()
    {
        EnsureLyricPickerWindow();

        var monitor = _overlay is { IsVisible: true }
            ? MonitorFromWindow(_overlay.Handle, MONITOR_DEFAULTTONEAREST)
            : nint.Zero;

        _picker!.ShowPanel(monitor);
    }

    /// <summary>The sheet sends the listener to the selector rather than opening a second search UI.</summary>
    private void ChooseLyricsFromViewer()
    {
        _viewer?.HidePanel();
        OpenLyricPicker();
    }

    private bool IsOwnWindowOpen =>
        _picker is { IsOpen: true } || _viewer is { IsOpen: true } || _settings is { IsOpen: true }
        || _launcher is { IsOpen: true };

    /// <summary>Open the complete lyric sheet for the track that is playing.</summary>
    private void EnsureLyricViewerWindow()
    {
        if (_viewer is not null)
        {
            return;
        }

        _viewer = new LyricViewerWindow(
            SearchLyrics,
            id => ChooseVersion(id),
            ExportLyrics,
            OpenInExternalEditor,
            PublishLyrics,
            CurrentPosition,
            SaveEditedLyrics,
            ChooseLyricsFromViewer);

        _viewer.Navigation = _navigation;
    }

    private void ShowLyricViewerWindow()
    {
        EnsureLyricViewerWindow();
        _viewer!.ShowSheet(BuildSheet());
    }

    /// <summary>
    /// Ask lrclib for the alternative records when the selector opens without a usable list. A good
    /// synced sheet costs no extra request, because the lookup only fetches the list when the answer
    /// was missing or unsynced - which is exactly when someone is likely to open this window.
    /// </summary>
    private void EnsureVersionsForPicker()
    {
        var snapshot = _snapshot;
        if (IsDemoActive || !snapshot.HasTrack || _lyrics is null || IsVersionsLoading)
        {
            return;
        }

        bool current;
        lock (_gate)
        {
            // Revision is the whole answer: an empty list that was fetched for this track is also a
            // definitive "no alternatives", and re-asking every time the window opens would be rude
            // to lrclib. An attempt that failed also counts, or the live refresh would retry on every
            // tick; Look again is the explicit way to ask twice.
            current = _candidatesRevision == snapshot.Revision
                      || _versionsAttemptedRevision == snapshot.Revision;
        }

        if (!current)
        {
            // Not a "look again": a stored list from the last session is a perfectly good answer.
            RefreshVersions(refresh: false);
        }
    }

    /// <summary>
    /// The selector's snapshot. Pulled on the UI thread every time the window draws: it stays open
    /// while the lookup lands, the track changes and the listener picks another record.
    /// </summary>
    private LyricPickerModel BuildPickerModel()
    {
        var snapshot = _snapshot;

        IReadOnlyList<LyricsCandidate> candidates;
        long? recordId;
        bool complete;
        bool instrumental;
        bool synced;
        bool wordSynced;
        int lineCount;
        string? status;

        lock (_gate)
        {
            // A list memoised for the previous track is not an answer for this one.
            candidates = _candidatesRevision == snapshot.Revision ? _candidates : [];
            recordId = _lyricsRecordId;
            complete = _fetchCompleted;
            instrumental = _isInstrumental;
            synced = _lyricsSynced;
            wordSynced = _lyricsWordSynced;
            lineCount = _lines.Length;
            status = _lyricsStatus;
        }

        long? pinned = null;
        if (snapshot.HasTrack && _lyrics is not null
            && _lyrics.TryGetPinnedVersion(snapshot.Artist, snapshot.Title, snapshot.Album, out var pinnedId))
        {
            pinned = pinnedId;
        }

        var title = MetadataNormaliser.CleanTitle(snapshot.Title, snapshot.Artist);
        // The selector seeds its fields from the same words, so an untouched header is what "look
        // again" asks for: artist and title. The album joins only when the listener edits it.
        var query = string.Join(
            ' ',
            new[] { snapshot.Artist, title }.Where(static part => !string.IsNullOrWhiteSpace(part)));

        bool searchLoading;
        IReadOnlyList<LyricsCandidate> searchResults;
        string? searchMessage;

        lock (_gate)
        {
            // A search is an answer about the track it was asked for; another revision cannot use it.
            var current = _searchRevision == snapshot.Revision;
            searchLoading = current && _searchLoading;
            searchResults = current ? _searchResults : [];
            searchMessage = current ? _searchMessage : null;
        }

        return new LyricPickerModel
        {
            HasTrack = snapshot.HasTrack,
            IsDemo = IsDemoActive,
            Title = MetadataNormaliser.CleanTitle(snapshot.Title, snapshot.Artist),
            Artist = snapshot.Artist,
            Album = snapshot.Album,
            SourceApp = snapshot.SourceAppFriendly,
            DurationSeconds = snapshot.DurationSeconds,
            Revision = snapshot.Revision,
            Versions = candidates,
            PinnedId = pinned,
            Loading = IsVersionsLoading,
            LookupComplete = complete,
            IsInstrumental = instrumental,
            IsSynced = synced,
            HasWordTimings = wordSynced,
            LineCount = lineCount,
            CurrentRecordId = recordId,
            Source = _lyricsSource,
            ImportPreview = _importPreviewRevision == snapshot.Revision ? _importPreview : null,
            ImportMessage = _importPreviewRevision == snapshot.Revision ? _importMessage : null,
            Status = status,
            Query = query,
            Artwork = snapshot.Artwork,
            ArtworkWidth = snapshot.ArtworkWidth,
            ArtworkHeight = snapshot.ArtworkHeight,
            SearchLoading = searchLoading,
            SearchResults = searchResults,
            SearchMessage = searchMessage
        };
    }

    /// <summary>The immutable document the complete-lyrics window shows.</summary>
    private LyricSheet BuildSheet()
    {
        var snapshot = _snapshot;

        LyricLine[] lines;
        long? recordId;
        bool complete;
        bool instrumental;
        bool synced;
        bool wordSynced;
        string? status;
        bool searchLoading;
        IReadOnlyList<LyricsCandidate> searchResults;
        string? searchMessage;

        lock (_gate)
        {
            lines = _lines;
            recordId = _lyricsRecordId;
            complete = _fetchCompleted;
            instrumental = _isInstrumental;
            synced = _lyricsSynced;
            wordSynced = _lyricsWordSynced;
            status = _lyricsStatus;

            // A manual search is an answer about the track that was playing when it was asked, so a
            // result memoised for an older revision must not appear under a new one.
            var current = _searchRevision == snapshot.Revision;
            searchLoading = current && _searchLoading;
            searchResults = current ? _searchResults : [];
            searchMessage = current ? _searchMessage : null;
        }

        var position = IsDemoActive ? DemoPosition() : _watcher?.Clock.Position ?? TimeSpan.Zero;

        // A corrected body can arrive for the same record id and the same number of lines, so the
        // document key has to include the words, not only the shape of the sheet. The viewer rebuilds
        // its measured rows when this changes.
        var fingerprint = 17;
        foreach (var line in lines)
        {
            fingerprint = unchecked((fingerprint * 31) + line.Text.GetHashCode());
            fingerprint = unchecked((fingerprint * 31) + line.Start.GetHashCode());
        }

        var key = string.Concat(
            snapshot.Revision,
            "|",
            recordId?.ToString() ?? "-",
            "|",
            lines.Length,
            "|",
            synced ? "s" : "p",
            "|",
            wordSynced ? "w" : "-",
            "|",
            instrumental ? "i" : "-",
            "|",
            complete ? "c" : "-",
            "|",
            fingerprint);

        return new LyricSheet
        {
            Key = key,
            Title = MetadataNormaliser.CleanTitle(snapshot.Title, snapshot.Artist),
            Artist = snapshot.Artist,
            Album = snapshot.Album,
            SourceApp = snapshot.SourceAppFriendly,
            DurationSeconds = snapshot.DurationSeconds,
            Lines = lines,
            IsSynced = synced,
            HasWordTimings = wordSynced,
            IsInstrumental = instrumental,
            IsComplete = complete,
            Status = status,
            RecordId = recordId,
            Source = _lyricsSource,
            Artwork = snapshot.Artwork,
            ArtworkWidth = snapshot.ArtworkWidth,
            ArtworkHeight = snapshot.ArtworkHeight,
            Position = position,
            Revision = snapshot.Revision,
            IsDemo = IsDemoActive,
            SearchLoading = searchLoading,
            SearchResults = searchResults,
            SearchMessage = searchMessage
        };
    }

    /// <summary>
    /// Bring the open lyric windows up to date. The lookup finishes on a background thread, and both
    /// windows are Direct2D surfaces that must only be drawn on this one, so the stamps are read here
    /// rather than redrawing from the task.
    /// </summary>
    private void RefreshLyricWindows()
    {
        var lyricStamp = Volatile.Read(ref _lyricsStamp);
        var versionStamp = Volatile.Read(ref _versionsStamp);

        if (lyricStamp == _uiLyricsStamp && versionStamp == _uiVersionsStamp)
        {
            return;
        }

        var lyricsChanged = lyricStamp != _uiLyricsStamp;
        _uiLyricsStamp = lyricStamp;
        _uiVersionsStamp = versionStamp;

        _picker?.Invalidate();

        if (_picker is { IsOpen: true })
        {
            // A track that changed (or appeared after the panel opened) needs its own record list.
            EnsureVersionsForPicker();
        }

        if (lyricsChanged && _viewer is { IsOpen: true })
        {
            _viewer.UpdateSheet(BuildSheet());
        }
    }

    /// <summary>
    /// The source setting as the watcher should see it on one poll.
    /// </summary>
    /// <remarks>
    /// The priority list is copied rather than handed over: the polling thread enumerates it while the
    /// settings window may be reordering it on the UI thread, and a copy is the cheapest way to keep
    /// one from seeing the other mid-edit. The list is at most
    /// <see cref="SourceSelector.MaxPriority"/> entries, so the copy costs nothing worth measuring -
    /// and it is skipped entirely while the default, empty list is in use.
    /// </remarks>
    private SourceSettings ReadSourceSettings()
    {
        var priority = _config.SourcePriority;

        return new SourceSettings(
            priority.Count == 0 ? NoPriority : [.. priority],
            _config.SourcePinned);
    }

    /// <summary>
    /// The sweep needs the clock to follow the player more closely than line-level lyrics do: a
    /// 400 ms tolerance is invisible when a line changes every few seconds and obvious when a
    /// syllable is being highlighted.
    /// </summary>
    private void ApplyKaraokeTiming()
    {
        if (_watcher is not null)
        {
            _watcher.Clock.DriftSnapThresholdMs = _config.Karaoke ? 150.0 : 400.0;
        }
    }

    /// <summary>
    /// Push the persisted lyric correction into the clock. The clock clears its own manual offset on
    /// a track change, so this has to run again on every revision - otherwise a correction that is
    /// still shown on the control bar would silently stop applying after the first song.
    /// </summary>
    private void ApplyLyricOffset()
    {
        var resolved = TimingProfiles.Resolve(
            _config.LyricsOffsetMs,
            _config.TrackOffsets,
            _trackKey,
            _config.OutputOffsets,
            _audioOutput);

        _timing = TimingProfiles.AddSession(resolved, _sessionOffsetMs);

        if (_watcher is not null)
        {
            _watcher.Clock.ManualOffset = TimeSpan.FromMilliseconds(_timing.OffsetMs);
        }
    }

    /// <summary>The correction the overlay should display even before a player is attached.</summary>
    private TimingResolution TimingForVisual => _watcher is null
        ? new TimingResolution(TimingProfiles.Clamp(_config.LyricsOffsetMs), TimingSource.Global)
        : _timing;

    /// <summary>
    /// Re-read the default output on the slow cadence, or immediately when forced. Returns true when
    /// the endpoint or its label changed, which means the resolved profile has to be applied.
    /// </summary>
    private bool RefreshAudioOutput(bool force = false)
    {
        var now = Environment.TickCount64;

        if (!force && now < _nextAudioOutputCheckAt)
        {
            return false;
        }

        _nextAudioOutputCheckAt = now + AudioOutputPollMs;

        var output = AudioEndpoint.ReadDefault();
        if (output is null)
        {
            return false;
        }

        if (_audioOutput is not null
            && string.Equals(output.Id, _audioOutput.Id, StringComparison.Ordinal)
            && string.Equals(output.Name, _audioOutput.Name, StringComparison.Ordinal)
            && output.IsBluetooth == _audioOutput.IsBluetooth)
        {
            return false;
        }

        _audioOutput = output;

        // A device switch invalidates a session-only nudge: the number was for the old output.
        _sessionOffsetMs = 0;
        return true;
    }

    /// <summary>The Timing section's live values, read fresh on every draw.</summary>
    private TimingView BuildTimingView()
    {
        var resolved = TimingProfiles.Resolve(
            _config.LyricsOffsetMs,
            _config.TrackOffsets,
            _trackKey,
            _config.OutputOffsets,
            _audioOutput);
        var effective = TimingProfiles.AddSession(resolved, _sessionOffsetMs);

        var outputOffsetMs = 0;
        if (_audioOutput is { IsKnown: true }
            && _config.OutputOffsets.TryGetValue(_audioOutput.Id, out var profile)
            && profile is not null)
        {
            outputOffsetMs = profile.OffsetMs;
        }

        return new TimingView(
            OutputName: _audioOutput?.Name ?? "No output device",
            OutputIsBluetooth: _audioOutput?.IsBluetooth ?? false,
            OffsetValue: TimingProfiles.FormatOffset(effective.OffsetMs),
            SourceLabel: TimingProfiles.SourceLabel(effective.Source, _audioOutput),
            OutputOffsetMs: outputOffsetMs,
            HasTrackOffset: _trackKey is not null && _config.TrackOffsets.ContainsKey(_trackKey));
    }

    /// <summary>Remove this track's override, falling back to the output profile or the default.</summary>
    private void UseGlobalTiming()
    {
        if (_trackKey is null || !_config.TrackOffsets.Remove(_trackKey))
        {
            return;
        }

        _sessionOffsetMs = 0;
        ApplyLyricOffset();
        ForceRedraw();
        _settings?.RefreshIfOpen();
        _config.Save();
    }

    /// <summary>Store an explicit zero for this track, so the output profile does not apply to it.</summary>
    private void ClearTrackTiming()
    {
        if (_trackKey is null)
        {
            return;
        }

        _config.TrackOffsets[_trackKey] = 0;
        _sessionOffsetMs = 0;
        ApplyLyricOffset();
        ForceRedraw();
        _settings?.RefreshIfOpen();
        _config.Save();
    }

    /// <summary>Adjust the saved profile for the current output device.</summary>
    private void NudgeOutputTiming(int deltaMs)
    {
        if (_audioOutput is not { IsKnown: true })
        {
            return;
        }

        var profile = _config.OutputOffsets.TryGetValue(_audioOutput.Id, out var existing) && existing is not null
            ? existing
            : new OutputOffsetProfile { Name = _audioOutput.Name, Bluetooth = _audioOutput.IsBluetooth };

        profile.Name = _audioOutput.Name;
        profile.Bluetooth = _audioOutput.IsBluetooth;
        profile.OffsetMs = TimingProfiles.Clamp(profile.OffsetMs + deltaMs);
        _config.OutputOffsets[_audioOutput.Id] = profile;
        _sessionOffsetMs = 0;
        ApplyLyricOffset();
        ForceRedraw();
        _settings?.RefreshIfOpen();
        _config.Save();
    }

    private static bool ShiftHeld() => (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

    /// <summary>Apply a change made in the settings window to the live overlay.</summary>
    private void ApplySettings(SettingsChange change)
    {
        switch (change)
        {
            case SettingsChange.Appearance:
                ApplyTheme();
                _overlay?.SetStage(_config.StageMode);
                break;

            case SettingsChange.Layout:
                _overlay?.SetMode(_config.Mode);
                ForceRedraw();
                break;

            case SettingsChange.Visibility:
                ForceRedraw();
                break;

            case SettingsChange.Placement:
                _overlay?.SetPlacement(_config.Anchor, _config.OffsetX, _config.OffsetY, nint.Zero);
                ForceRedraw();
                break;

            case SettingsChange.Timing:
                ApplyLyricOffset();
                ForceRedraw();
                break;

            case SettingsChange.Startup:
                StartupRegistration.SetEnabled(_config.StartWithWindows);
                break;
        }

        ApplyKaraokeTiming();
        _config.Save();
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

        // The top level keeps what is worth reaching for while music is playing: the commands for the
        // current track, the player list that explains a silent overlay, then the state switches.
        // The four windows are deliberately absent: they open from the launcher (left-click), which
        // is one list shared with every title bar.
        var items = new List<TrayMenuItem>
        {
            // Informational, and previously selectable while doing nothing.
            TrayMenuItem.Disabled(trackLine),
            new(Commands.RefreshLyrics, "Refresh lyrics"),
            new(Commands.PreviewOverlay, "Preview overlay"),
            new(Commands.PlayPause, "Play / Pause"),
            new(Commands.NextTrack, "Next track"),
            TrayMenuItem.Submenu("Players", SourceMenuItems()),
            TrayMenuItem.Separator,
            new(Commands.ToggleOverlay, "Show overlay", _overlayEnabled),
            TrayMenuItem.Submenu("Layout", LayoutMenuItems()),
            new(Commands.ToggleLock, "Locked (click-through)", _locked),
            new(Commands.ToggleStage, "Stage mode", _config.StageMode),
            TrayMenuItem.Separator,
            new(Commands.Exit, "Exit")
        };

        // The explanation for a silent overlay belongs where the user comes looking for one.
        var warningItems = PlayerWarningMenuItems();
        if (warningItems.Count > 0)
        {
            items.InsertRange(1, warningItems);
        }

        if (_fullscreenNotice)
        {
            // Durable while the situation lasts, and worded as an action: the overlay cannot draw
            // there, so switching the game to borderless is the only fix.
            items.Insert(1, TrayMenuItem.Disabled("Game is in exclusive fullscreen - use Borderless"));
        }

        var selected = TrayIcon.ShowMenu(_messageWindow.Handle, items);
        if (selected != 0)
        {
            Execute(selected);
        }
    }

    /// <summary>
    /// The players publishing right now, as a menu: automatic first, one entry per live source, then
    /// the way into the priority order.
    /// </summary>
    /// <remarks>
    /// The list is the watcher's last poll, which is at most a second old - good enough for a menu
    /// that is rebuilt every time it is opened. The command id an entry carries is an index into the
    /// list it was built from, and that list is kept in <see cref="_menuSources"/>: the watcher keeps
    /// polling while the menu is open, so re-reading <see cref="MediaSessionWatcher.LiveSources"/> at
    /// click time could name a different player than the row that was clicked.
    /// </remarks>
    private List<TrayMenuItem> SourceMenuItems()
    {
        var pinned = (_config.SourcePinned ?? "").Trim();
        var sources = _watcher?.LiveSources ?? [];
        _menuSources = sources;

        var items = new List<TrayMenuItem>
        {
            // Clears the pin rather than pinning "whatever is current": automatic is a setting the
            // user comes back to, and it has to keep following Windows afterwards.
            new(Commands.SourceAutomatic, "Automatic (follow Windows)", pinned.Length == 0),
            TrayMenuItem.Separator
        };

        if (sources.Count == 0)
        {
            items.Add(TrayMenuItem.Disabled("Nothing playing"));
        }
        else
        {
            for (var i = 0; i < sources.Count; i++)
            {
                var source = sources[i];

                // A session with no readable app user model id has no name either. It is still a
                // player the user can pin, so it gets a row rather than an unclickable blank line.
                var label = string.IsNullOrWhiteSpace(source.Label) ? "Unknown player" : source.Label;

                // The marker is Windows' own opinion, which is what "current" means elsewhere in the
                // application - it is not necessarily the source being followed.
                var text = source.IsSystemCurrent ? $"{label} (current)" : label;

                items.Add(new TrayMenuItem(
                    Commands.PinSourceBase + i,
                    text,
                    string.Equals(pinned, source.Aumid, StringComparison.OrdinalIgnoreCase)));
            }
        }

        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(Commands.SourcePriorityOrder, "Priority order\u2026"));

        return items;
    }

    /// <summary>
    /// The three layouts as checkable rows. The old row cycled them blind: it named what a click would
    /// leave behind, and never showed that there were three or which one was in force.
    /// </summary>
    private IReadOnlyList<TrayMenuItem> LayoutMenuItems()
    {
        return
        [
            new(Commands.LayoutSingle, "Single line", _config.Mode == OverlayMode.SingleLine),
            new(Commands.LayoutPeek, "Single +1", _config.Mode == OverlayMode.SingleLineWithNext),
            new(Commands.LayoutMulti, "Multi line", _config.Mode == OverlayMode.MultiLine)
        ];
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "\u2026";

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
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        _launcher?.Dispose();
        _launcher = null;

        _settings?.Dispose();
        _settings = null;

        _picker?.Dispose();
        _picker = null;

        _viewer?.Dispose();
        _viewer = null;

        _toast?.Dispose();
        _toast = null;

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
