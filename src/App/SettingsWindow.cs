using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Overlyric.Interop;
using Overlyric.Media;
using Overlyric.Overlay;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.App;

/// <summary>Which part of the configuration changed, so the host applies only that.</summary>
internal enum SettingsChange
{
    /// <summary>Colours, opacity, font sizes, line count: everything the theme bakes in.</summary>
    Appearance,

    /// <summary>Single-line versus multi-line.</summary>
    Layout,

    /// <summary>Header, progress bar, and the hide rules.</summary>
    Visibility,

    /// <summary>Anchor or offsets.</summary>
    Placement,

    /// <summary>The manual lyric offset.</summary>
    Timing,

    /// <summary>The per-user startup registration.</summary>
    Startup
}

/// <summary>
/// The timing state the Timing section prints, read fresh on every draw: the current output, the
/// correction in force, and which layer supplied it.
/// </summary>
/// <param name="OutputName">Friendly name of the current default output.</param>
/// <param name="OutputIsBluetooth">Whether Windows calls the endpoint Bluetooth.</param>
/// <param name="OffsetValue">The effective offset, formatted as the bar prints it.</param>
/// <param name="SourceLabel">Where that value came from: "this track", "default", ...</param>
/// <param name="OutputOffsetMs">The saved correction for the current output, if any.</param>
/// <param name="HasTrackOffset">Whether the current track has a stored override.</param>
internal readonly record struct TimingView(
    string OutputName,
    bool OutputIsBluetooth,
    string OffsetValue,
    string SourceLabel,
    int OutputOffsetMs,
    bool HasTrackOffset);

/// <summary>
/// The timing actions the settings window can take. The host owns them because only it knows the
/// current track key, the current endpoint and whether the value is session-only; the window decides
/// only which row was pressed.
/// </summary>
internal sealed class TimingCommands
{
    public required Func<TimingView> View { get; init; }

    public required Action<int> Nudge { get; init; }

    public required Action Reset { get; init; }

    public required Action UseGlobal { get; init; }

    public required Action ClearTrack { get; init; }

    public required Action<int> NudgeOutput { get; init; }
}

/// <summary>
/// The configuration window.
/// <para>
/// A tray menu is a poor place to configure anything: every click dismisses it, so a slider cannot
/// be dragged and a value cannot be read back. Everything that is set once and then left alone
/// lives here instead, and the tray keeps only what is reached for mid-song.
/// </para>
/// <para>
/// The window is a plain Win32 window drawn with the same Direct2D/DirectWrite stack as the overlay,
/// so it looks like the rest of the application and adds no dependency. It is fixed-size: no resize
/// handling, no reflow, no scrollbars - each section is laid out to fit the space it has.
/// </para>
/// </summary>
internal sealed class SettingsWindow : WindowBase
{
    /// <summary>
    /// The sections, in the order the side bar lists them. Appearance follows Lyrics because the two
    /// together decide what the overlay looks like; timing and position follow because they are the
    /// corrections made most often; the setup sections close the list because they are set once.
    /// </summary>
    private enum Section
    {
        Lyrics = 0,
        Appearance = 1,
        Timing = 2,
        Position = 3,
        Behaviour = 4,
        Players = 5,
        Shortcuts = 6,
        Advanced = 7
    }

    /// <summary>Identifiers for every clickable thing. Values must stay unique.</summary>
    private enum Control
    {
        // The navigation ids follow the Section order with no gap: the click decoder turns a nav id
        // back into a Section by subtracting NavLyrics, so these eight values are a table, not labels.
        NavLyrics = 1,
        NavAppearance = 2,
        NavTiming = 3,
        NavPosition = 4,
        NavBehaviour = 5,
        NavPlayers = 6,
        NavShortcuts = 7,
        NavAdvanced = 8,

        /// <summary>One active lyric line with the next one previewed underneath.</summary>
        ModePeek = 9,

        ModeSingle = 10,
        ModeMulti = 11,
        LinesDown = 12,
        LinesUp = 13,
        Header = 15,
        ProgressBar = 16,
        FontActiveDown = 17,
        FontActiveUp = 18,
        FontInactiveDown = 19,
        FontInactiveUp = 20,

        HidePaused = 21,
        HideNoLyrics = 22,
        HideInstrumental = 23,
        WarnPlayer = 24,

        OffsetDown = 25,
        OffsetUp = 26,
        OffsetReset = 27,

        AnchorBottom = 28,
        AnchorTop = 29,
        AnchorCentre = 30,
        ResetPosition = 31,

        OpenData = 32,
        OpenCache = 33,
        Close = 34,

        /// <summary>Shortcut chips are this base plus the index into <see cref="ShortcutActions.All"/>.</summary>
        ShortcutChipBase = 40,

        OledPreset = 60,
        OledAutoHideDown = 61,
        OledAutoHideUp = 62,
        OledHighlight = 63,
        OledDimDown = 64,
        OledDimUp = 65,
        OledPixelShift = 66,
        OledOutline = 67,
        OledOpacitySlider = 68,
        KaraokeToggle = 69,

        /// <summary>Pin the player being followed right now, or clear the pin when there is one.</summary>
        SourcePin = 70,

        /// <summary>Add the player publishing to Windows right now to the top of the priority list.</summary>
        SourceAdd = 71,

        /// <summary>
        /// The three buttons on every priority row. One base per button kind plus the slot index,
        /// because the grid holds a fixed number of slots: the click handler decodes the slot with a
        /// range check rather than the enum naming eighteen buttons.
        /// </summary>
        SourceUpBase = 80,

        /// <summary>Move the entry in that slot one place towards the front of the list.</summary>
        SourceDownBase = 90,

        /// <summary>Remove the entry in that slot from the list.</summary>
        SourceRemoveBase = 100,

        /// <summary>
        /// The two halves of the info pop-up stepper. A duration needs both directions, and 0 has to
        /// be reachable from either side: it is the value that turns the pop-up off, not a missing
        /// setting.
        /// </summary>
        InfoPopupDown = 72,

        /// <summary>The other half of the info pop-up stepper; the clamp is the one the config load uses.</summary>
        InfoPopupUp = 73,

        /// <summary>
        /// Say so when lrclib has nothing for the track. Appended past the source id ranges so those
        /// blocks keep decoding to the same slots.
        /// </summary>
        NotifyMissingLyrics = 74,

        /// <summary>The OLED section's pure-black sweep toggle, appended so the older ids keep their values.</summary>
        OledBlackout = 75,

        /// <summary>Remove every cached lyric answer from the Advanced section.</summary>
        ClearCache = 76,

        /// <summary>Write or remove the current user's Run entry.</summary>
        StartWithWindows = 77,

        /// <summary>
        /// Timing profiles: the remember switch, the two track buttons and the output stepper.
        /// Appended past the source id blocks so those keep decoding to the same slots.
        /// </summary>
        TimingRemember = 108,
        TimingUseGlobal = 109,
        TimingClearTrack = 110,
        TimingOutputDown = 111,
        TimingOutputUp = 112,

        /// <summary>Open the folder holding imported local lyric files.</summary>
        OpenLocal = 113,

        /// <summary>Cycle the overlay's display: cursor, primary, each connected monitor.</summary>
        MonitorCycle = 114,

        /// <summary>Add the foreground executable to the per-app hide list.</summary>
        HideAppAdd = 115,

        /// <summary>Empty the per-app hide list.</summary>
        HideAppClear = 116,

        /// <summary>The curated palette presets.</summary>
        ThemeDefault = 117,
        ThemeWarm = 118,
        ThemeCool = 119,
        ThemeMono = 120,

        /// <summary>Weight of the active lyric line.</summary>
        WeightNormal = 121,
        WeightSemiBold = 122,
        WeightBold = 123,

        /// <summary>Show the optional second-language sidecar under the active line.</summary>
        ShowTranslation = 124,

        /// <summary>Toggle the full-monitor audience surface.</summary>
        StageMode = 125,

        /// <summary>Use chroma-key green instead of black as the stage backdrop.</summary>
        StageChroma = 126,

        /// <summary>Hide while an application captures from the microphone.</summary>
        HideWhenMic = 127,

        /// <summary>
        /// The four offset nudges and the scrollbar. Their values fill the gap between the shortcut
        /// chips and the OLED block, the only stretch of the id space left; the enum's order documents
        /// the window, and only the navigation ids are ever decoded by arithmetic.
        /// </summary>
        OffsetXDown = 50,
        OffsetXUp = 51,
        OffsetYDown = 52,
        OffsetYUp = 53,
        Scrollbar = 54
    }

    private const float DefaultWidthDip = 780f;
    private const float DefaultHeightDip = 540f;
    private const float TitleHeightDip = 44f;

    /// <summary>The narrowest window whose rows still keep their labels and controls apart.</summary>
    private const float MinWidthDip = 700f;

    private const float MinWindowHeightDip = 400f;

    /// <summary>How close to an edge the pointer has to be, in DIP, to start a resize.</summary>
    private const float ResizeBorderDip = 6f;

    private const float NavWidthDip = 196f;
    private const float PadDip = 30f;
    private const float RowHeightDip = 46f;
    private const float RowsTopDip = 132f;
    private const float ContentLeft = NavWidthDip + PadDip;

    /// <summary>
    /// The cluster of up, down and remove buttons at the right of a priority row, sized and spaced
    /// like the other grouped controls so the rows read as one column.
    /// </summary>
    private const float RankButtonWidth = 30f;
    private const float RankButtonGap = 6f;
    private const float RankButtonsWidth = (RankButtonWidth * 3f) + (RankButtonGap * 2f);

    private const uint ColorWindow = 0xFF0F1116;
    private const uint ColorNav = 0xFF0A0C10;
    private const uint ColorNavSelected = 0xFF1C222D;
    private const uint ColorNavHot = 0xFF151A22;
    private const uint ColorLine = 0xFF1E222B;
    private const uint ColorText = 0xFFE9EDF5;
    private const uint ColorTextDim = 0xFF8A93A6;
    private const uint ColorTextFaint = 0xFF5A6273;
    private const uint ColorControl = 0xFF1A1F28;
    private const uint ColorControlHot = 0xFF232A35;
    private const uint ColorSelected = 0xFF23303F;
    private const uint ColorAccent = 0xFF7CC4FF;
    private const uint ColorKnob = 0xFFD8DEE9;
    private const uint ColorKnobHot = 0xFFFFFFFF;
    private const uint ColorWarning = 0xFFE0A458;

    private static readonly string[] SectionTitles =
        ["Lyrics", "Appearance", "Timing", "Position", "Behaviour", "Players", "Shortcuts", "Advanced"];

    private static readonly string[] SectionHints =
    [
        "How the words look, line by line.",
        "The surface behind the words, and what keeps an OLED panel clean.",
        "Correct lyrics that run early or late.",
        "Where the overlay sits on the monitor.",
        "When the overlay should get out of the way.",
        "Which music client Overlyric follows, and in what order.",
        "Click a shortcut, then press the keys you want.",
        "Folders, cache and version information."
    ];

    /// <summary>
    /// The caption above the navigation entry at the same index, or empty for an entry that follows
    /// one. Two captions split eight sections into what the overlay does and how it is wired, which is
    /// the distinction the flat list was missing.
    /// </summary>
    private static readonly string[] NavCaptions =
        ["ON SCREEN", "", "", "", "", "SETUP", "", ""];

    /// <summary>
    /// One past the last row index each section draws. The viewport needs the height before the section
    /// is drawn, so this is a table rather than something measured twice.
    /// </summary>
    private static readonly int[] SectionRows = [11, 10, 8, 5, 9, 8, ShortcutActions.All.Length + 1, 4];

    private readonly AppConfig _config;
    private readonly Action<SettingsChange> _changed;
    private readonly Func<int, string, string?> _assignShortcut;
    private readonly Func<string, bool> _isUnavailable;
    private readonly Action _openDataFolder;
    private readonly Action _openCacheFolder;
    private readonly Func<(int Entries, long Bytes)> _cacheStats;
    private readonly Func<(int Entries, long Bytes)> _localStats;
    private readonly Action _openLocalFolder;
    private readonly Func<string> _monitorLabel;
    private readonly Action _cycleMonitor;
    private readonly Func<string> _foregroundApp;
    private readonly Action _addHideApp;
    private readonly Action _clearHideApps;
    private readonly Action _clearCache;
    private readonly string _version;

    /// <summary>
    /// The players publishing to Windows at the moment the Source section is drawn. A callback rather
    /// than a list captured at construction: the window outlives any single session, so a snapshot
    /// would offer to add players that closed long ago.
    /// </summary>
    private readonly Func<IReadOnlyList<SourceCandidate>> _liveSources;

    /// <summary>The live timing state and the actions that change it, both owned by the host.</summary>
    private readonly TimingCommands _timing;

    private readonly List<(int Id, Rect Rect)> _hits = [];

    /// <summary>Brushes and formats for this host; created on first draw, dropped with the device.</summary>
    private TextCache? _text;

    private CompositionHost? _host;
    private Section _section = Section.Lyrics;
    private int _hover = -1;
    private WindowDestination? _menuHover;
    private bool _open;
    private bool _draggingOpacity;
    private float _sliderLeft;
    private float _sliderWidth;
    private float _dpi = 96f;

    /// <summary>How far the open section is scrolled, in DIP. Zero whenever the section fits.</summary>
    private float _scroll;

    /// <summary>Translation applied to the hit rectangles being recorded; zero outside the scrolled body.</summary>
    private float _hitScroll;

    private bool _draggingScroll;
    private float _scrollTrackTop;
    private float _scrollTrackLength;
    private float _scrollThumbLength;

    /// <summary>The control the keyboard is on, or -1 when the window has not been tabbed into.</summary>
    private int _focus = -1;

    /// <summary>
    /// The live window size in DIP. The window opens at 780x584 but the user can resize it, and it
    /// clamps to the monitor's work area so a 200 % window stays reachable instead of growing past
    /// the screen. The layout reads these two values rather than constants, which is what makes the
    /// navigation stay put while the content columns take the new width.
    /// </summary>
    private float _widthDip = DefaultWidthDip;

    private float _windowHeightDip = DefaultHeightDip + TitleHeightDip;

    /// <summary>The content area below the title bar, in DIP.</summary>
    private float _clientHeightDip => _windowHeightDip - TitleHeightDip;

    private float ContentRight => _widthDip - PadDip;

    private float ContentWidth => ContentRight - ContentLeft;

    private static float ViewportTop => 108f;

    private float ViewportBottom => _clientHeightDip - 40f;

    /// <summary>Index into <see cref="ShortcutActions.All"/> whose chip is waiting for keys, or -1.</summary>
    private int _capturing = -1;

    /// <summary>Why the last shortcut assignment was refused, shown under the list.</summary>
    private string? _shortcutMessage;

    public SettingsWindow(
        AppConfig config,
        Action<SettingsChange> changed,
        Func<int, string, string?> assignShortcut,
        Func<string, bool> isUnavailable,
        Action openDataFolder,
        Action openCacheFolder,
        Func<(int Entries, long Bytes)> cacheStats,
        Action clearCache,
        Func<(int Entries, long Bytes)> localStats,
        Action openLocalFolder,
        Func<string> monitorLabel,
        Action cycleMonitor,
        Func<string> foregroundApp,
        Action addHideApp,
        Action clearHideApps,
        string version,
        Func<IReadOnlyList<SourceCandidate>> liveSources,
        TimingCommands timing)
    {
        _config = config;
        _changed = changed;
        _assignShortcut = assignShortcut;
        _isUnavailable = isUnavailable;
        _openDataFolder = openDataFolder;
        _openCacheFolder = openCacheFolder;
        _cacheStats = cacheStats;
        _clearCache = clearCache;
        _localStats = localStats;
        _openLocalFolder = openLocalFolder;
        _monitorLabel = monitorLabel;
        _cycleMonitor = cycleMonitor;
        _foregroundApp = foregroundApp;
        _addHideApp = addHideApp;
        _clearHideApps = clearHideApps;
        _version = version;
        _liveSources = liveSources;
        _timing = timing;
    }

    private static nint _classBrush;

    protected override string ClassName => "OverlyricSettingsWindow";

    protected override int WindowStyle => WS_POPUP;

    /// <summary>
    /// Frameless, like the overlay: a themed window frame is not painted for a window whose content
    /// comes from DirectComposition, so the title bar is drawn here instead. Topmost, also like the
    /// overlay, because a settings window that slips underneath the lyrics is a window the user cannot
    /// read; it is only created on request.
    /// </summary>
    protected override int WindowExtraStyle => WS_EX_NOREDIRECTIONBITMAP | WS_EX_APPWINDOW | WS_EX_TOPMOST;

    /// <summary>COLORREF is 0x00BBGGRR; this is the window's #0F1116.</summary>
    protected override IntPtr BackgroundBrush
    {
        get
        {
            if (_classBrush == nint.Zero)
            {
                _classBrush = CreateSolidBrush(0x0016110F);
            }

            return _classBrush;
        }
    }

    public bool IsOpen => _open;

    /// <summary>
    /// Host callbacks shared by every window. Assigned after construction because the host owns the
    /// navigation object that opens or raises the four windows.
    /// </summary>
    public WindowNavigation? Navigation { get; set; }

    /// <summary>
    /// Repaint an open window when the host changed state it displays without a click here - the
    /// default output device changing is the case that matters for Timing.
    /// </summary>
    public void RefreshIfOpen()
    {
        if (_open)
        {
            Render();
        }
    }

    public void Show()
    {
        if (Handle == IntPtr.Zero)
        {
            Create();
        }
        else if (!_open)
        {
            Resume();
        }

        _open = true;
        ShowWindow(Handle, SW_SHOW);
        SetForegroundWindow(Handle);
        Render();
    }

    public void Hide()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        _hover = -1;
        _capturing = -1;
        _shortcutMessage = null;

        if (Handle != IntPtr.Zero)
        {
            ShowWindow(Handle, SW_HIDE);
        }
    }

    /// <summary>
    /// Open the window on the Source section. The tray deep-links here from its source menu, and it
    /// shows the window itself rather than assuming the caller has already done so: a section that is
    /// selected but invisible looks exactly like the deep link not working.
    /// </summary>
    public void ShowPlayersSection()
    {
        _section = Section.Players;
        Show();
    }

    private void Create()
    {
        CreateWindow("Overlyric Settings", (int)DefaultWidthDip, (int)(DefaultHeightDip + TitleHeightDip));

        // Resizable, but not maximizable: the content is a single column of rows, and a maximise box
        // would stretch it across the screen with no way back except restore.
        var style = GetWindowLongPtrW(Handle, -16).ToInt64();
        style &= ~0x00010000L; // WS_MAXIMIZEBOX
        SetWindowLongPtrW(Handle, -16, new nint(style));

        _dpi = ResolveDpi();
        FitToWorkArea();
        ApplyBounds(center: true);

        // Dark title bar, so the window does not sit in a bright frame.
        var dark = 1;
        if (DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
        {
            // Pre-20H1 builds only know the old attribute. If this one is refused too there is no
            // third fallback, and a light title bar is not worth failing the window over.
            _ = DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));
        }
    }

    private float ResolveDpi()
    {
        var dpi = GetDpiForWindow(Handle);
        return DpiOverride.For(dpi > 0 ? dpi : 96f);
    }

    /// <summary>
    /// Reopening keeps the size and position the user left the window at. The DPI is re-read because
    /// the window may have been hidden while the monitor changed, and the size is clamped so a
    /// window left on a monitor that has since gone away is still reachable.
    /// </summary>
    private void Resume()
    {
        _dpi = ResolveDpi();
        FitToWorkArea();
        ApplyBounds(center: false);
    }

    /// <summary>
    /// Shrink to the monitor's work area. At 200 % on a 1080p panel the work area is only about
    /// 520 DIP tall, shorter than the window wants to be; shrinking keeps the footer and the last
    /// row on screen, and the rows scroll instead of the window growing past the display.
    /// </summary>
    private void FitToWorkArea()
    {
        var monitor = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

        if (!GetMonitorInfoW(monitor, ref info))
        {
            return;
        }

        var scale = _dpi / 96f;
        var maxWidth = MathF.Max(320f, (info.rcWork.Width / scale) - 12f);
        var maxHeight = MathF.Max(240f, (info.rcWork.Height / scale) - 12f);

        // On a screen smaller than the minimum the clamp has to allow the maximum: a window that
        // cannot fit is better than one that cannot be reached.
        _widthDip = Math.Clamp(_widthDip, MathF.Min(MinWidthDip, maxWidth), maxWidth);
        _windowHeightDip = Math.Clamp(_windowHeightDip, MathF.Min(MinWindowHeightDip, maxHeight), maxHeight);
    }

    /// <summary>
    /// Match the window to the stored DIP size. Centring is for the first show; after that the
    /// top-left corner is kept, because a window that jumps to the middle whenever it is reopened
    /// is a window that cannot be placed.
    /// </summary>
    private void ApplyBounds(bool center)
    {
        var width = ToPixels(_widthDip);
        var height = ToPixels(_windowHeightDip);
        var monitor = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var placed = GetWindowRect(Handle, out var current);
        var x = 0;
        var y = 0;

        if (GetMonitorInfoW(monitor, ref info))
        {
            if (center || !placed)
            {
                x = info.rcWork.Left + ((info.rcWork.Width - width) / 2);
                y = info.rcWork.Top + ((info.rcWork.Height - height) / 2);
            }
            else
            {
                x = Math.Clamp(current.Left, info.rcWork.Left, Math.Max(info.rcWork.Left, info.rcWork.Right - width));
                y = Math.Clamp(current.Top, info.rcWork.Top, Math.Max(info.rcWork.Top, info.rcWork.Bottom - height));
            }
        }
        else if (placed)
        {
            x = current.Left;
            y = current.Top;
        }

        SetWindowPos(Handle, IntPtr.Zero, x, y, width, height, SWP_NOACTIVATE | SWP_NOZORDER);

        _host?.Resize(width, height, _dpi);
        EnsureHost(width, height);
        Render();
    }

    /// <summary>
    /// Follow a live resize. Windows has already moved the frame by the time WM_SIZE arrives, so
    /// the size is read back from the client rectangle; the composition surface follows in the same
    /// message, which keeps a dragged border from showing a stretched row or a blank strip.
    /// </summary>
    private void OnClientSizeChanged()
    {
        if (!GetClientRect(Handle, out var client) || client.Width <= 0 || client.Height <= 0)
        {
            return;
        }

        var scale = _dpi / 96f;
        _widthDip = client.Width / scale;
        _windowHeightDip = client.Height / scale;

        _host?.Resize(client.Width, client.Height, _dpi);

        // A taller viewport can make the old scroll position past the end of the section.
        _scroll = Math.Clamp(_scroll, 0f, MaxScroll);
        Render();
    }

    /// <summary>
    /// Adopt the rectangle Windows recommends for a new DPI. It carries the old size scaled by the
    /// DPI ratio, so the window keeps its visual size instead of jumping a third of the screen.
    /// </summary>
    private void OnDpiChanged(nint lParam)
    {
        _dpi = ResolveDpi();
        var suggested = Marshal.PtrToStructure<RECT>(lParam);
        var scale = _dpi / 96f;

        if (suggested.Width > 0 && suggested.Height > 0)
        {
            _widthDip = suggested.Width / scale;
            _windowHeightDip = suggested.Height / scale;

            SetWindowPos(
                Handle,
                IntPtr.Zero,
                suggested.Left,
                suggested.Top,
                suggested.Width,
                suggested.Height,
                SWP_NOACTIVATE | SWP_NOZORDER);

            _host?.Resize(suggested.Width, suggested.Height, _dpi);
            Render();
            return;
        }

        FitToWorkArea();
        ApplyBounds(center: false);
    }

    private void OnDisplayChanged()
    {
        _dpi = ResolveDpi();
        FitToWorkArea();
        ApplyBounds(center: false);
    }

    private void EnsureHost(int pixelWidth, int pixelHeight)
    {
        if (_host is not null)
        {
            return;
        }

        _host = CompositionHost.Create(Handle, pixelWidth, pixelHeight, _dpi);
    }

    private void RebuildHost()
    {
        ReleaseDeviceResources();
        _host?.Dispose();
        _host = null;

        try
        {
            EnsureHost(ToPixels(_widthDip), ToPixels(_windowHeightDip));
            Render();
        }
        catch (Exception)
        {
            // Leave the window blank rather than take the process down.
        }
    }

    private void ReleaseDeviceResources()
    {
        _text?.Dispose();
        _text = null;
    }

    private int ToPixels(float dip) => (int)MathF.Round(dip * _dpi / 96f);

    protected override IntPtr WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return new IntPtr(1);

            case WM_PAINT:
                BeginPaint(hWnd, out var paint);
                EndPaint(hWnd, ref paint);
                Render();
                return IntPtr.Zero;

            case WM_SIZE:
                OnClientSizeChanged();
                return IntPtr.Zero;

            case WM_GETMINMAXINFO:
                ApplyMinimumTrackSize(lParam, _dpi, MinWidthDip, MinWindowHeightDip);
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                OnMouseDown(lParam);
                return IntPtr.Zero;

            case WM_NCHITTEST:
                return HitTest(lParam);

            case WM_LBUTTONUP:
                OnMouseUp();
                return IntPtr.Zero;

            case WM_MOUSEMOVE:
                OnMouseMove(lParam);
                return IntPtr.Zero;

            case WM_MOUSEWHEEL:
                // The delta lives in the high word of wParam; the pointer's position is in lParam.
                OnMouseWheel(wParam);
                return IntPtr.Zero;

            case WM_MOUSELEAVE:
                if (_hover != -1 || _menuHover is not null)
                {
                    _hover = -1;
                    _menuHover = null;
                    Render();
                }

                return IntPtr.Zero;

            case WM_KEYDOWN:
                return OnKeyDown((int)wParam.ToInt64());

            case WM_SYSKEYDOWN:
                // Alt combinations arrive here. Only swallow them while a shortcut is being
                // captured, otherwise Alt+Tab and Alt+F4 would stop working in this window.
                if (_capturing >= 0)
                {
                    return OnKeyDown((int)wParam.ToInt64());
                }

                break;

            case WM_CLOSE:
                // Closing hides the window; the host keeps it alive so reopening is instant.
                Hide();
                return IntPtr.Zero;

            case WM_DPICHANGED:
                OnDpiChanged(lParam);
                return IntPtr.Zero;

            case WM_DISPLAYCHANGE:
                OnDisplayChanged();
                return IntPtr.Zero;

            case WM_DESTROY:
                Unregister(hWnd);
                return IntPtr.Zero;
        }

        return DefaultWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>The title bar drags the window; the close button is the only live part of it.</summary>
    private IntPtr HitTest(nint lParam)
    {
        // The edges size the window and win over the title bar; the close button is inside the
        // title bar but far enough from the right edge that the two never overlap.
        var resize = ResizeFrameHitTest(Handle, lParam, _dpi, ResizeBorderDip);

        if (resize.ToInt64() != HTCLIENT)
        {
            return resize;
        }

        var raw = lParam.ToInt64();
        var point = new POINT
        {
            X = (short)(raw & 0xFFFF),
            Y = (short)((raw >> 16) & 0xFFFF)
        };

        if (!ScreenToClient(Handle, ref point))
        {
            return new IntPtr(HTCLIENT);
        }

        var scale = _dpi / 96f;
        var x = point.X / scale;
        var y = point.Y / scale;
        var close = CloseRect();

        var overClose = x >= close.Left && x < close.Right && y >= close.Top && y < close.Bottom;

        if (y < TitleHeightDip)
        {
            // The menu and close are real controls; the rest of the bar drags the window.
            return new IntPtr(overClose || WindowMenu.TryHit(x, y, out _) ? HTCLIENT : HTCAPTION);
        }

        return new IntPtr(HTCLIENT);
    }

    private void OnMouseDown(nint lParam)
    {
        var (x, y) = ToDip(lParam);

        if (y < TitleHeightDip && WindowMenu.TryHit(x, y, out var destination))
        {
            _menuHover = destination;
            Navigation?.Open(destination);
            return;
        }

        // Hits inside the content are recorded in content coordinates, which start below the
        // title bar; the close button is the one control that lives in window coordinates.
        var contentY = y - TitleHeightDip;

        foreach (var (id, rect) in _hits)
        {
            var testY = id == (int)Control.Close ? y : contentY;

            if (x < rect.Left || x >= rect.Right || testY < rect.Top || testY >= rect.Bottom)
            {
                continue;
            }

            if (id == (int)Control.OledOpacitySlider)
            {
                _draggingOpacity = true;
                SetCapture(Handle);
                SetOpacityFrom(x);
                return;
            }

            if (id == (int)Control.Scrollbar)
            {
                _draggingScroll = true;
                SetCapture(Handle);
                SetScrollFrom(contentY);
                Render();
                return;
            }

            Activate(id);
            return;
        }
    }

    private void OnMouseUp()
    {
        if (_draggingScroll)
        {
            _draggingScroll = false;
            ReleaseCapture();
            Render();
        }

        if (!_draggingOpacity)
        {
            return;
        }

        _draggingOpacity = false;
        ReleaseCapture();
        _config.Save();
        Render();
    }

    private void OnMouseMove(nint lParam)
    {
        var (x, y) = ToDip(lParam);

        if (_draggingOpacity)
        {
            SetOpacityFrom(x);
            return;
        }

        if (_draggingScroll)
        {
            SetScrollFrom(y - TitleHeightDip);
            return;
        }

        var menuHover = y < TitleHeightDip && WindowMenu.TryHit(x, y, out var destination)
            ? destination
            : (WindowDestination?)null;

        if (menuHover != _menuHover)
        {
            _menuHover = menuHover;
            Render();
        }

        var hover = -1;
        foreach (var (id, rect) in _hits)
        {
            var testY = id == (int)Control.Close ? y : y - TitleHeightDip;

            if (x >= rect.Left && x < rect.Right && testY >= rect.Top && testY < rect.Bottom)
            {
                hover = id;
                break;
            }
        }

        if (hover != _hover)
        {
            _hover = hover;
            Render();
        }

        var track = new TRACKMOUSEEVENT
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = TME_LEAVE,
            hwndTrack = Handle
        };

        TrackMouseEvent(ref track);
    }

    /// <summary>Three rows a notch, which is the same feel as the lyric sheet's wheel.</summary>
    private void OnMouseWheel(nint wParam)
    {
        var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);

        if (delta == 0 || MaxScroll <= 0f)
        {
            return;
        }

        SetScroll(_scroll - (Math.Sign(delta) * 3f * RowHeightDip));
    }

    private void SetOpacityFrom(float x)
    {
        if (_sliderWidth <= 0f)
        {
            return;
        }

        var t = Math.Clamp((x - _sliderLeft) / _sliderWidth, 0f, 1f);
        var opacity = (double)t;

        if (Math.Abs(opacity - _config.BackgroundOpacity) < 0.001)
        {
            return;
        }

        _config.BackgroundOpacity = opacity;
        _changed(SettingsChange.Appearance);
        Render();
    }

    /// <summary>While a shortcut chip is armed, the next real key becomes the chord.</summary>
    private IntPtr OnKeyDown(int virtualKey)
    {
        if (_capturing < 0)
        {
            switch (virtualKey)
            {
                case VK_ESCAPE:
                    Hide();
                    return IntPtr.Zero;

                case VK_TAB:
                    // Shift is what makes it a reverse cycle, and the message itself carries no
                    // modifier state, so the keyboard is asked directly.
                    MoveFocus((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 ? -1 : 1);
                    return IntPtr.Zero;

                case VK_RETURN:
                case VK_SPACE:
                    if (_focus >= 0)
                    {
                        Activate(_focus);
                    }

                    return IntPtr.Zero;

                case VK_PRIOR:
                    SetScroll(_scroll - (ViewportBottom - ViewportTop));
                    return IntPtr.Zero;

                case VK_NEXT:
                    SetScroll(_scroll + (ViewportBottom - ViewportTop));
                    return IntPtr.Zero;

                case VK_HOME:
                    SetScroll(0f);
                    return IntPtr.Zero;

                case VK_END:
                    SetScroll(MaxScroll);
                    return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        if (virtualKey == VK_ESCAPE)
        {
            _capturing = -1;
            _shortcutMessage = null;
            Render();
            return IntPtr.Zero;
        }

        // A modifier on its own is not a chord: wait for the key it modifies.
        if (virtualKey is VK_CONTROL or VK_SHIFT or VK_MENU or VK_LWIN or VK_RWIN)
        {
            return IntPtr.Zero;
        }

        var chord = new HotKeyChord(CurrentModifiers(), (uint)virtualKey);
        var action = ShortcutActions.All[_capturing];
        var error = _assignShortcut(action.Command, chord.ToString());

        if (error is null)
        {
            _capturing = -1;
            _shortcutMessage = null;
        }
        else
        {
            _shortcutMessage = error;
        }

        Render();
        return IntPtr.Zero;
    }

    /// <summary>
    /// The modifiers held right now. Async state rather than <c>GetKeyState</c>, because what
    /// matters is the keyboard as it is when the key arrives, not as it was for the last message
    /// this thread happened to dequeue.
    /// </summary>
    private static uint CurrentModifiers()
    {
        var modifiers = 0u;

        if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0)
        {
            modifiers |= MOD_CONTROL;
        }

        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0)
        {
            modifiers |= MOD_SHIFT;
        }

        if ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
        {
            modifiers |= MOD_ALT;
        }

        if ((GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0)
        {
            modifiers |= MOD_WIN;
        }

        return modifiers;
    }

    private void BeginCapture(int index)
    {
        _capturing = index;
        _shortcutMessage = null;
        Render();
    }

    private void Activate(int id)
    {
        if (id >= (int)Control.ShortcutChipBase && id < (int)Control.ShortcutChipBase + ShortcutActions.All.Length)
        {
            BeginCapture(id - (int)Control.ShortcutChipBase);
            return;
        }

        // The priority rows are a fixed grid, so their buttons are three contiguous id blocks rather
        // than a case per button: the offset into the block is the slot the button belongs to.
        if (id >= (int)Control.SourceUpBase && id < (int)Control.SourceUpBase + SourceSelector.MaxPriority)
        {
            MovePriority(id - (int)Control.SourceUpBase, up: true);
            return;
        }

        if (id >= (int)Control.SourceDownBase && id < (int)Control.SourceDownBase + SourceSelector.MaxPriority)
        {
            MovePriority(id - (int)Control.SourceDownBase, up: false);
            return;
        }

        if (id >= (int)Control.SourceRemoveBase && id < (int)Control.SourceRemoveBase + SourceSelector.MaxPriority)
        {
            RemovePriority(id - (int)Control.SourceRemoveBase);
            return;
        }

        // The nav ids are contiguous and in Section order, so the range check is the decoder.
        if (id >= (int)Control.NavLyrics && id <= (int)Control.NavAdvanced)
        {
            _section = (Section)(id - (int)Control.NavLyrics);

            // A section opens at its top, and the keyboard starts over: keeping either would land the
            // reader halfway down a section they have just chosen.
            _scroll = 0f;
            _focus = -1;
            Render();
            return;
        }

        switch ((Control)id)
        {
            case Control.SourcePin:
                TogglePinned();
                return;

            case Control.SourceAdd:
                AddPlaying();
                return;

            case Control.ModeSingle:
                _config.Mode = OverlayMode.SingleLine;
                Apply(SettingsChange.Layout);
                return;

            case Control.ModePeek:
                _config.Mode = OverlayMode.SingleLineWithNext;
                Apply(SettingsChange.Layout);
                return;

            case Control.ModeMulti:
                _config.Mode = OverlayMode.MultiLine;
                Apply(SettingsChange.Layout);
                return;

            case Control.LinesDown:
                _config.MultiLineCount = Math.Clamp(_config.MultiLineCount - 1, 1, 11);
                Apply(SettingsChange.Appearance);
                return;

            case Control.LinesUp:
                _config.MultiLineCount = Math.Clamp(_config.MultiLineCount + 1, 1, 11);
                Apply(SettingsChange.Appearance);
                return;

            case Control.Header:
                _config.ShowHeader = !_config.ShowHeader;
                Apply(SettingsChange.Visibility);
                return;

            case Control.ProgressBar:
                _config.ShowProgressBar = !_config.ShowProgressBar;
                Apply(SettingsChange.Visibility);
                return;

            case Control.ShowTranslation:
                _config.ShowTranslation = !_config.ShowTranslation;
                Apply(SettingsChange.Visibility);
                return;

            case Control.FontActiveDown:
                _config.ActiveFontSize = Math.Clamp(_config.ActiveFontSize - 2f, 12f, 72f);
                Apply(SettingsChange.Appearance);
                return;

            case Control.FontActiveUp:
                _config.ActiveFontSize = Math.Clamp(_config.ActiveFontSize + 2f, 12f, 72f);
                Apply(SettingsChange.Appearance);
                return;

            case Control.FontInactiveDown:
                _config.InactiveFontSize = Math.Clamp(_config.InactiveFontSize - 2f, 10f, 72f);
                Apply(SettingsChange.Appearance);
                return;

            case Control.FontInactiveUp:
                _config.InactiveFontSize = Math.Clamp(_config.InactiveFontSize + 2f, 10f, 72f);
                Apply(SettingsChange.Appearance);
                return;

            case Control.HidePaused:
                _config.HideWhenPaused = !_config.HideWhenPaused;
                Apply(SettingsChange.Visibility);
                return;

            case Control.HideNoLyrics:
                _config.HideWhenNoLyrics = !_config.HideWhenNoLyrics;
                Apply(SettingsChange.Visibility);
                return;

            case Control.HideInstrumental:
                _config.HideWhenInstrumental = !_config.HideWhenInstrumental;
                Apply(SettingsChange.Visibility);
                return;

            case Control.WarnPlayer:
                _config.WarnWhenPlayerDoesNotPublish = !_config.WarnWhenPlayerDoesNotPublish;
                Apply(SettingsChange.Visibility);
                return;

            case Control.OffsetDown:
                _timing.Nudge(-250);
                Render();
                return;

            case Control.OffsetUp:
                _timing.Nudge(250);
                Render();
                return;

            case Control.OffsetReset:
                _timing.Reset();
                Render();
                return;

            case Control.TimingRemember:
                _config.RememberTrackOffset = !_config.RememberTrackOffset;
                Apply(SettingsChange.Timing);
                return;

            case Control.TimingUseGlobal:
                _timing.UseGlobal();
                Render();
                return;

            case Control.TimingClearTrack:
                _timing.ClearTrack();
                Render();
                return;

            case Control.TimingOutputDown:
                _timing.NudgeOutput(-250);
                Render();
                return;

            case Control.TimingOutputUp:
                _timing.NudgeOutput(250);
                Render();
                return;

            case Control.AnchorBottom:
                SetAnchor(OverlayAnchor.BottomCenter);
                return;

            case Control.AnchorTop:
                SetAnchor(OverlayAnchor.TopCenter);
                return;

            case Control.AnchorCentre:
                SetAnchor(OverlayAnchor.Center);
                return;

            case Control.MonitorCycle:
                _cycleMonitor();
                Render();
                return;

            case Control.ResetPosition:
                SetAnchor(_config.Anchor);
                return;

            case Control.OffsetXDown:
                NudgeOffset(-5, 0);
                return;

            case Control.OffsetXUp:
                NudgeOffset(5, 0);
                return;

            case Control.OffsetYDown:
                NudgeOffset(0, -5);
                return;

            case Control.OffsetYUp:
                NudgeOffset(0, 5);
                return;

            case Control.OpenData:
                _openDataFolder();
                return;

            case Control.OpenCache:
                _openCacheFolder();
                return;

            case Control.OpenLocal:
                _openLocalFolder();
                return;

            case Control.ClearCache:
                _clearCache();
                Render();
                return;

            case Control.StartWithWindows:
                _config.StartWithWindows = !_config.StartWithWindows;
                Apply(SettingsChange.Startup);
                return;

            case Control.HideAppAdd:
                _addHideApp();
                Render();
                return;

            case Control.HideAppClear:
                _clearHideApps();
                Render();
                return;

            case Control.HideWhenMic:
                _config.HideWhenMicrophoneInUse = !_config.HideWhenMicrophoneInUse;
                Apply(SettingsChange.Visibility);
                return;

            case Control.ThemeDefault:
            case Control.ThemeWarm:
            case Control.ThemeCool:
            case Control.ThemeMono:
                _config.Theme = (Control)id switch
                {
                    Control.ThemeWarm => ThemePreset.Warm,
                    Control.ThemeCool => ThemePreset.Cool,
                    Control.ThemeMono => ThemePreset.Mono,
                    _ => ThemePreset.Default
                };
                Apply(SettingsChange.Appearance);
                return;

            case Control.WeightNormal:
            case Control.WeightSemiBold:
            case Control.WeightBold:
                _config.ActiveLyricWeight = (Control)id switch
                {
                    Control.WeightNormal => LyricWeight.Normal,
                    Control.WeightBold => LyricWeight.Bold,
                    _ => LyricWeight.SemiBold
                };
                Apply(SettingsChange.Appearance);
                return;

            case Control.StageMode:
                _config.StageMode = !_config.StageMode;
                Apply(SettingsChange.Appearance);
                return;

            case Control.StageChroma:
                _config.StageChroma = !_config.StageChroma;
                Apply(SettingsChange.Appearance);
                return;

            case Control.OledPreset:
                ApplyOledPreset();
                return;

            case Control.OledAutoHideDown:
                _config.HeaderAutoHideSeconds = Math.Clamp(_config.HeaderAutoHideSeconds - 2, 0, 30);
                Apply(SettingsChange.Visibility);
                return;

            case Control.OledAutoHideUp:
                _config.HeaderAutoHideSeconds = Math.Clamp(_config.HeaderAutoHideSeconds + 2, 0, 30);
                Apply(SettingsChange.Visibility);
                return;

            case Control.OledHighlight:
                _config.ShowActiveLineHighlight = !_config.ShowActiveLineHighlight;
                Apply(SettingsChange.Appearance);
                return;

            case Control.OledDimDown:
                _config.InactiveLineOpacity = Math.Clamp(Math.Round(_config.InactiveLineOpacity - 0.1, 2), 0.1, 1.0);
                Apply(SettingsChange.Appearance);
                return;

            case Control.OledDimUp:
                _config.InactiveLineOpacity = Math.Clamp(Math.Round(_config.InactiveLineOpacity + 0.1, 2), 0.1, 1.0);
                Apply(SettingsChange.Appearance);
                return;

            case Control.OledPixelShift:
                _config.PixelShift = !_config.PixelShift;
                Apply(SettingsChange.Appearance);
                return;

            case Control.OledOutline:
                _config.TextOutline = !_config.TextOutline;
                Apply(SettingsChange.Appearance);
                return;

            case Control.OledBlackout:
                _config.KaraokeBlackout = !_config.KaraokeBlackout;
                Apply(SettingsChange.Appearance);
                return;

            case Control.KaraokeToggle:
                _config.Karaoke = !_config.Karaoke;
                Apply(SettingsChange.Appearance);
                return;

            case Control.InfoPopupDown:
                _config.InfoPopupSeconds = Math.Clamp(_config.InfoPopupSeconds - 1, 0, 20);
                Apply(SettingsChange.Visibility);
                return;

            case Control.InfoPopupUp:
                _config.InfoPopupSeconds = Math.Clamp(_config.InfoPopupSeconds + 1, 0, 20);
                Apply(SettingsChange.Visibility);
                return;

            case Control.NotifyMissingLyrics:
                _config.NotifyWhenLyricsMissing = !_config.NotifyWhenLyricsMissing;
                Apply(SettingsChange.Visibility);
                return;

            case Control.Close:
                Hide();
                return;
        }
    }

    private void ApplyOledPreset()
    {
        // The bundle itself lives on the config, so it can be tested without a window.
        _config.ApplyOledPreset();

        _config.Save();
        _changed(SettingsChange.Appearance);
        _changed(SettingsChange.Visibility);
        Render();
    }

    /// <summary>
    /// Move the stored offset by a few pixels. Dragging the overlay is the fast path; this is the one
    /// that works from a keyboard, and it is why the offsets stopped being a read-only label.
    /// </summary>
    private void NudgeOffset(int dx, int dy)
    {
        _config.OffsetX = Math.Clamp(_config.OffsetX + dx, -4000, 4000);
        _config.OffsetY = Math.Clamp(_config.OffsetY + dy, -4000, 4000);
        Apply(SettingsChange.Placement);
    }

    private void SetAnchor(OverlayAnchor anchor)
    {
        _config.Anchor = anchor;
        _config.OffsetX = 0;
        _config.OffsetY = anchor == OverlayAnchor.Center ? 0 : 90;
        Apply(SettingsChange.Placement);
    }

    /// <summary>
    /// The pin: on pins whoever is being followed this instant, off goes back to automatic. Pinning
    /// the followed session rather than opening a picker is deliberate - the list and Windows have
    /// already answered "which one now", and a second picker would be a second place to answer it
    /// differently.
    /// </summary>
    private void TogglePinned()
    {
        if (!string.IsNullOrWhiteSpace(_config.SourcePinned))
        {
            // Empty means automatic: follow the list, and Windows when nothing in it is running.
            _config.SourcePinned = "";
            ApplySource();
            return;
        }

        var live = LiveSources();
        if (live.Count == 0)
        {
            return;
        }

        var following = SourceSelector.Choose(live, _config.SourcePriority, _config.SourcePinned);
        if (!following.HasChoice)
        {
            return;
        }

        _config.SourcePinned = following.Aumid;
        ApplySource();
    }

    /// <summary>
    /// Adds the player publishing right now to the top of the list. Adding means "prefer this", which
    /// is why it is a promotion rather than an append: an entry added at the bottom of a six-slot list
    /// is one the user then has to move up themselves.
    /// </summary>
    private void AddPlaying()
    {
        var live = LiveSources();
        if (live.Count == 0)
        {
            return;
        }

        var following = SourceSelector.Choose(live, _config.SourcePriority, _config.SourcePinned);
        if (!following.HasChoice)
        {
            return;
        }

        // A full list refuses rather than growing past the six rows the grid draws: an entry the
        // window cannot show is an entry the user cannot remove again.
        if (SourceSelector.IndexOf(_config.SourcePriority, following.Aumid) < 0
            && _config.SourcePriority.Count >= SourceSelector.MaxPriority)
        {
            return;
        }

        SourceSelector.Promote(_config.SourcePriority, following.Aumid, NameFor(live, following));
        ApplySource();
    }

    /// <summary>Moves one priority entry a slot, and saves only when it really moved.</summary>
    private void MovePriority(int slot, bool up)
    {
        if (slot < 0 || slot >= _config.SourcePriority.Count)
        {
            return;
        }

        // The helpers answer with the index the entry ended up at, which is also how an entry at
        // either end of the list reports that there was nowhere to go.
        var moved = up
            ? SourceSelector.MoveUp(_config.SourcePriority, slot)
            : SourceSelector.MoveDown(_config.SourcePriority, slot);

        if (moved != slot)
        {
            ApplySource();
        }
    }

    /// <summary>
    /// Removes one priority entry. Removal is by app user model id, as the tray menu does it. The pin
    /// is deliberately left alone: it is a separate statement about which player to follow, and a
    /// player can be pinned without being in the list at all.
    /// </summary>
    private void RemovePriority(int slot)
    {
        if (slot < 0 || slot >= _config.SourcePriority.Count)
        {
            return;
        }

        if (SourceSelector.Remove(_config.SourcePriority, _config.SourcePriority[slot].Aumid))
        {
            ApplySource();
        }
    }

    /// <summary>
    /// Persist a source edit. No <see cref="SettingsChange"/> is raised on purpose: the host reads the
    /// priority list and the pin fresh on every poll, so the next tick picks the change up and there is
    /// nothing about the overlay to reapply here.
    /// </summary>
    private void ApplySource()
    {
        _config.Save();
        Render();
    }

    private void Apply(SettingsChange change)
    {
        _config.Save();
        _changed(change);
        Render();
    }

    private void Render()
    {
        if (_host is null || !_open)
        {
            return;
        }

        try
        {
            var context = _host.BeginDraw();
            _hits.Clear();
            Draw(context);

            if (!_host.EndDraw())
            {
                RebuildHost();
            }
        }
        catch (Exception)
        {
            RebuildHost();
        }
    }

    private void Draw(ID2D1DeviceContext context)
    {
        Fill(context, R(0f, 0f, _widthDip, _windowHeightDip), ColorWindow);

        DrawTitleBar(context);

        // Everything below the title bar is laid out in content coordinates.
        context.Transform = Matrix3x2.CreateTranslation(0f, TitleHeightDip);

        Fill(context, R(0f, 0f, NavWidthDip, _clientHeightDip), ColorNav);
        Fill(context, R(NavWidthDip, 0f, NavWidthDip + 1f, _clientHeightDip), ColorLine);

        DrawNavigation(context);

        Label(context, SectionTitles[(int)_section], ContentLeft, 44f, ContentWidth, 30f, 20f, FontWeight.SemiBold, ColorText);
        Label(context, SectionHints[(int)_section], ContentLeft, 76f, ContentWidth, 18f, 12.5f, FontWeight.Normal, ColorTextDim);
        Fill(context, R(ContentLeft, 106f, ContentRight, 107f), ColorLine);

        // Clip first, in unscrolled coordinates, then move the rows. The clip is stored in device
        // space, so a translation applied after the push does not drag it along with the content.
        context.PushAxisAlignedClip(
            R(NavWidthDip + 1f, ViewportTop, _widthDip, ViewportBottom),
            AntialiasMode.Aliased);

        context.Transform = Matrix3x2.CreateTranslation(0f, TitleHeightDip - _scroll);
        _hitScroll = _scroll;

        DrawSectionBody(context);

        _hitScroll = 0f;
        context.Transform = Matrix3x2.CreateTranslation(0f, TitleHeightDip);
        context.PopAxisAlignedClip();

        DrawScrollbar(context);

        Label(
            context,
            $"Overlyric {_version}  ·  Esc to close",
            ContentLeft,
            _clientHeightDip - 30f,
            ContentWidth,
            18f,
            11.5f,
            FontWeight.Normal,
            ColorTextFaint);

        DrawFocusRing(context);

        context.Transform = Matrix3x2.Identity;
    }

    /// <summary>The rows of the open section. Drawn inside the scroll clip, so it may be taller than the viewport.</summary>
    private void DrawSectionBody(ID2D1DeviceContext context)
    {
        switch (_section)
        {
            case Section.Lyrics:
                DrawLyricsSection(context);
                break;
            case Section.Appearance:
                DrawAppearanceSection(context);
                break;
            case Section.Timing:
                DrawTimingSection(context);
                break;
            case Section.Position:
                DrawPositionSection(context);
                break;
            case Section.Behaviour:
                DrawBehaviourSection(context);
                break;
            case Section.Players:
                DrawPlayersSection(context);
                break;
            case Section.Shortcuts:
                DrawShortcutsSection(context);
                break;
            default:
                DrawAdvancedSection(context);
                break;
        }
    }

    private void DrawTitleBar(ID2D1DeviceContext context)
    {
        Fill(context, R(0f, 0f, _widthDip, TitleHeightDip), ColorNav);
        Fill(context, R(0f, TitleHeightDip - 1f, _widthDip, TitleHeightDip), ColorLine);

        WindowMenu.Draw(
            WindowDestination.Settings,
            Navigation?.IsOpen,
            _menuHover,
            (left, top, width, height, radius, argb) => FillRound(context, left, top, width, height, radius, argb),
            (text, left, top, width, height, size, weight, argb, align) =>
                Label(context, text, left, top, width, height, size, weight, argb, align));

        var close = CloseRect();
        var hot = _hover == (int)Control.Close;

        if (hot)
        {
            FillRound(context, close.Left, close.Top, close.Right - close.Left, close.Bottom - close.Top, 7f, ColorControlHot);
        }

        Label(
            context,
            "\u2715",
            close.Left,
            close.Top,
            close.Right - close.Left,
            close.Bottom - close.Top,
            12f,
            FontWeight.Normal,
            hot ? ColorText : ColorTextDim,
            TextAlignment.Center);

        Hit((int)Control.Close, close);
    }

    private Rect CloseRect() => R(_widthDip - 52f, 6f, _widthDip - 12f, TitleHeightDip - 6f);

    private void DrawNavigation(ID2D1DeviceContext context)
    {
        Label(context, "SETTINGS", 30f, 40f, NavWidthDip - 60f, 20f, 10.5f, FontWeight.SemiBold, ColorTextFaint);

        // Laid out as a running offset rather than index arithmetic: a caption costs height the item
        // grid does not know about, and two captions of eighteen pixels still leave the list 90 DIP
        // clear of the footer.
        var top = 70f;

        for (var i = 0; i < SectionTitles.Length; i++)
        {
            var caption = NavCaptions[i];

            if (caption.Length > 0)
            {
                Label(context, caption, 30f, top, NavWidthDip - 60f, 18f, 10.5f, FontWeight.SemiBold, ColorTextFaint);
                top += 24f;
            }

            var id = (int)Control.NavLyrics + i;
            var selected = (int)_section == i;

            if (selected)
            {
                FillRound(context, 16f, top, NavWidthDip - 32f, 34f, 8f, ColorNavSelected);

                // A rail as well as the fill: the fill alone is a shade of the panel behind it, and at
                // 100 % scaling it is the rail that says which section is open.
                FillRound(context, 16f, top + 7f, 2f, 20f, 1f, ColorAccent);
            }
            else if (_hover == id)
            {
                FillRound(context, 16f, top, NavWidthDip - 32f, 34f, 8f, ColorNavHot);
            }

            Label(
                context,
                SectionTitles[i],
                32f,
                top,
                NavWidthDip - 64f,
                34f,
                13f,
                selected ? FontWeight.SemiBold : FontWeight.Normal,
                selected ? ColorText : ColorTextDim);

            Hit(id, R(16f, top, NavWidthDip - 16f, top + 34f));
            top += 42f;
        }
    }

    /// <summary>
    /// The words: how many lines, how big, and the sweep that follows the voice. Grouped layout, type,
    /// motion and chrome, with the two rows that only mean something because of another row indented
    /// beneath it and dead until that row is on.
    /// </summary>
    private void DrawLyricsSection(ID2D1DeviceContext context)
    {
        var multi = _config.Mode == OverlayMode.MultiLine;

        Row(context, 0, "Mode");
        Segmented(
            context,
            [
                ("Single line", Control.ModeSingle, _config.Mode == OverlayMode.SingleLine),
                ("Single +1", Control.ModePeek, _config.Mode == OverlayMode.SingleLineWithNext),
                ("Multi line", Control.ModeMulti, _config.Mode == OverlayMode.MultiLine)
            ]);

        // A line count is only a question in multi-line mode, and it used to sit live in single-line
        // mode where changing it does nothing at all.
        Row(context, 1, "Lines shown", child: true, enabled: multi);
        Stepper(context, _config.MultiLineCount.ToString(), Control.LinesDown, Control.LinesUp, multi);

        Group(context, 2, "TYPE");

        Row(context, 2, "Lyric size");
        Stepper(context, _config.ActiveFontSize.ToString("0"), Control.FontActiveDown, Control.FontActiveUp);

        Row(context, 3, "Surrounding lines size");
        Stepper(context, _config.InactiveFontSize.ToString("0"), Control.FontInactiveDown, Control.FontInactiveUp);

        Group(context, 4, "MOTION");

        Row(context, 4, "Karaoke sweep");
        Toggle(context, Control.KaraokeToggle, _config.Karaoke);

        // The one part of a swept line that is both static and lit is the half waiting to be sung.
        // Pure black is an unlit pixel on the panel, so the words appear only as the sweep reaches
        // them - which is why this row belongs to the sweep and dies without it.
        Row(context, 5, "Black out unsung words", child: true, enabled: _config.Karaoke);
        Toggle(context, Control.OledBlackout, _config.KaraokeBlackout, _config.Karaoke);

        Group(context, 6, "CHROME");

        Row(context, 6, "Show track info");
        Toggle(context, Control.Header, _config.ShowHeader);

        // The pop-up and the auto-hide are the header's lifetime, not sibling settings rows apart.
        Row(context, 7, "Info pop-up", child: true, enabled: _config.ShowHeader);
        Stepper(
            context,
            _config.InfoPopupSeconds == 0 ? "Off" : $"{_config.InfoPopupSeconds} s",
            Control.InfoPopupDown,
            Control.InfoPopupUp,
            _config.ShowHeader);

        Row(context, 8, "Auto-hide track info", child: true, enabled: _config.ShowHeader);
        Stepper(
            context,
            _config.HeaderAutoHideSeconds == 0 ? "Never" : $"{_config.HeaderAutoHideSeconds} s",
            Control.OledAutoHideDown,
            Control.OledAutoHideUp,
            _config.ShowHeader);

        Row(context, 9, "Show progress bar");
        Toggle(context, Control.ProgressBar, _config.ShowProgressBar);

        Row(context, 10, "Show translation");
        Toggle(context, Control.ShowTranslation, _config.ShowTranslation);
    }

    /// <summary>
    /// The surface behind the words. The preset leads and states what it overwrites, then the levers
    /// that decide what stays lit, then the two that are set once and forgotten.
    /// </summary>
    private void DrawAppearanceSection(ID2D1DeviceContext context)
    {
        RowWithNote(context, 0, "OLED preset", "backdrop 0 · progress off · dim 40 % · blackout · pixel shift");
        Button(context, ContentRight - 132f, RowTop(0) + 8f, 132f, 30f, "Apply preset", Control.OledPreset);

        Row(context, 1, "Backdrop opacity");
        Slider(context, Control.OledOpacitySlider, $"{Math.Round(_config.BackgroundOpacity * 100)}%");

        Group(context, 2, "WHAT STAYS LIT");

        Row(context, 2, "Highlight the active line");
        Toggle(context, Control.OledHighlight, _config.ShowActiveLineHighlight);

        Row(context, 3, "Dim surrounding lines");
        Stepper(context, $"{Math.Round(_config.InactiveLineOpacity * 100)}%", Control.OledDimDown, Control.OledDimUp);

        Group(context, 4, "BURN-IN TOOLS");

        Row(context, 4, "Pixel shift");
        Toggle(context, Control.OledPixelShift, _config.PixelShift);

        Row(context, 5, "Outline text");
        Toggle(context, Control.OledOutline, _config.TextOutline);

        Group(context, 6, "PALETTE");

        Row(context, 6, "Palette");
        Segmented(
            context,
            [
                ("Default", Control.ThemeDefault, _config.Theme == ThemePreset.Default),
                ("Warm", Control.ThemeWarm, _config.Theme == ThemePreset.Warm),
                ("Cool", Control.ThemeCool, _config.Theme == ThemePreset.Cool),
                ("Mono", Control.ThemeMono, _config.Theme == ThemePreset.Mono)
            ]);

        Row(context, 7, "Active line weight");
        Segmented(
            context,
            [
                ("Normal", Control.WeightNormal, _config.ActiveLyricWeight == LyricWeight.Normal),
                ("Semibold", Control.WeightSemiBold, _config.ActiveLyricWeight == LyricWeight.SemiBold),
                ("Bold", Control.WeightBold, _config.ActiveLyricWeight == LyricWeight.Bold)
            ]);

        Group(context, 8, "STAGE");

        RowWithNote(context, 8, "Stage mode", "full monitor, large centred lyrics, no bar");
        Toggle(context, Control.StageMode, _config.StageMode);

        Row(context, 9, "Chroma-key green", child: true, enabled: _config.StageMode);
        Toggle(context, Control.StageChroma, _config.StageChroma && _config.StageMode, enabled: _config.StageMode);
    }

    private void DrawShortcutsSection(ID2D1DeviceContext context)
    {
        for (var i = 0; i < ShortcutActions.All.Length; i++)
        {
            // Caption at the first row of each group; eight rows still fit under the section header.
            if (i == 0)
            {
                Group(context, i, "OVERLAY");
            }
            else if (i == 3)
            {
                Group(context, i, "TIMING");
            }
            else if (i == 6)
            {
                Group(context, i, "WINDOWS");
            }

            var action = ShortcutActions.All[i];
            Row(context, i, action.Label);
            ShortcutChip(context, i, action);
        }

        var line = ShortcutActions.All.Length;

        // A refusal is more important than the prompt: it is why the capture is still armed.
        if (_shortcutMessage is not null)
        {
            Label(
                context,
                _shortcutMessage,
                ContentLeft,
                RowTop(line) + 10f,
                ContentWidth,
                36f,
                12f,
                FontWeight.Normal,
                ColorWarning);
        }
        else if (_capturing >= 0)
        {
            Hint(context, line, "Press the new combination, or Esc to cancel.");
        }
        else
        {
            Hint(context, line, "Shortcuts work system-wide, even when Overlyric is not focused.");
        }
    }

    private void ShortcutChip(ID2D1DeviceContext context, int index, ShortcutAction action)
    {
        const float width = 190f;
        const float height = 30f;

        var left = ContentRight - width;
        var top = RowTop(index) + ((RowHeightDip - height) / 2f);
        var id = (int)Control.ShortcutChipBase + index;
        var capturing = _capturing == index;
        var text = ShortcutActions.Text(_config, action);
        var hot = _hover == id;

        FillRound(context, left, top, width, height, 8f, capturing ? ColorSelected : (hot ? ColorControlHot : ColorControl));

        var label = capturing ? "Press keys\u2026" : text.Length == 0 ? "Unassigned" : text;

        var color = capturing
            ? ColorAccent
            : _isUnavailable(action.Name)
                ? ColorWarning
                : text.Length == 0
                    ? ColorTextFaint
                    : ColorText;

        Label(context, label, left, top, width, height, 12.5f, FontWeight.SemiBold, color, TextAlignment.Center);
        Hit(id, R(left, top, left + width, top + height));
    }

    private void DrawBehaviourSection(ID2D1DeviceContext context)
    {
        Group(context, 0, "HIDE");

        Row(context, 0, "Hide when paused");
        Toggle(context, Control.HidePaused, _config.HideWhenPaused);

        Row(context, 1, "Hide when no lyrics");
        Toggle(context, Control.HideNoLyrics, _config.HideWhenNoLyrics);

        Row(context, 2, "Hide instrumentals");
        Toggle(context, Control.HideInstrumental, _config.HideWhenInstrumental);

        Group(context, 3, "WARN");

        Row(context, 3, "Warn when a player does not publish");
        Toggle(context, Control.WarnPlayer, _config.WarnWhenPlayerDoesNotPublish);

        Row(context, 4, "Notify when lyrics are missing");
        Toggle(context, Control.NotifyMissingLyrics, _config.NotifyWhenLyricsMissing);

        // Moved out of Advanced: starting with Windows is a behaviour, not housekeeping.
        Group(context, 5, "STARTUP");

        Row(context, 5, "Start with Windows");
        Toggle(context, Control.StartWithWindows, _config.StartWithWindows);

        // The hide list is the one rule whose value is not a switch: the row shows what is on it and
        // offers to add whatever owns the foreground window right now, without a text field.
        var hideList = _config.HideApps.Count == 0
            ? "none"
            : LyricText.Fit(string.Join(", ", _config.HideApps), 56);

        RowWithNote(context, 6, "Hide for apps", string.Concat(hideList, " \u00b7 ", _foregroundApp()));
        Button(context, ContentRight - 160f, RowTop(6) + 8f, 160f, 30f, "Add current app", Control.HideAppAdd);

        Row(context, 7, "Clear the hide list", enabled: _config.HideApps.Count > 0);
        Button(context, ContentRight - 132f, RowTop(7) + 8f, 132f, 30f, "Clear", Control.HideAppClear, _config.HideApps.Count > 0);

        Row(context, 8, "Hide while the microphone is in use");
        Toggle(context, Control.HideWhenMic, _config.HideWhenMicrophoneInUse);
    }

    private void DrawTimingSection(ID2D1DeviceContext context)
    {
        var timing = _timing.View();

        // The output is the context for the two saved layers below it: a correction belongs to a
        // track or to a device, and the row has to say which device that is.
        RowWithNote(context, 0, "Current output", timing.OutputName + (timing.OutputIsBluetooth ? " \u00b7 Bluetooth" : ""));

        // The value leads and is set at twenty pixels, because it is the one number this section is
        // about; the nudges either side are the same quarter second as the bar and the hotkeys.
        RowWithNote(context, 1, "Lyric offset", $"from {timing.SourceLabel}");
        TimingReadout(context, 1, timing.OffsetValue);

        Row(context, 2, "Remember this track", child: false);
        Toggle(context, Control.TimingRemember, _config.RememberTrackOffset);

        // Use default removes the override (falls back to the output or the global value); Clear
        // stores an explicit zero. Both are needed: "forget my correction" and "this track needs no
        // correction even though the output has one" are different intentions.
        Row(context, 3, "Use the global default", child: true, enabled: timing.HasTrackOffset);
        Button(context, ContentRight - 160f, RowTop(3) + 8f, 160f, 30f, "Use default", Control.TimingUseGlobal, timing.HasTrackOffset);

        Row(context, 4, "Offset for this output", child: true);
        Stepper(
            context,
            ShortcutFeedback.FormatOffset(timing.OutputOffsetMs),
            Control.TimingOutputDown,
            Control.TimingOutputUp,
            enabled: !timing.HasTrackOffset);

        Row(context, 5, "Clear this track's offset", child: true, enabled: timing.HasTrackOffset);
        Button(context, ContentRight - 190f, RowTop(5) + 8f, 190f, 30f, "Clear track offset", Control.TimingClearTrack, timing.HasTrackOffset);

        Row(context, 6, "Reset all timing for this track and output");
        Button(context, ContentRight - 132f, RowTop(6) + 8f, 132f, 30f, "Reset timing", Control.OffsetReset);

        Hint(context, 7, timing.OutputIsBluetooth && timing.OutputOffsetMs == 0 && !timing.HasTrackOffset
            ? "Bluetooth latency is not reported: nudge by ear; kept for this output."
            : "Track beats output, output beats global. Shift keeps a nudge for this session.");
    }

    private void DrawPositionSection(ID2D1DeviceContext context)
    {
        // The display is chosen first: the anchor and the offsets below only mean something on one
        // screen, and a docked laptop has different work areas for the same numbers.
        RowWithNote(context, 0, "Display", "cursor, primary, or a specific monitor");
        Button(context, ContentRight - 200f, RowTop(0) + 8f, 200f, 30f, _monitorLabel(), Control.MonitorCycle);

        // Three monitors instead of three words: the anchor is a position, and the offsets below are
        // measured from the edge this draws.
        Row(context, 1, "Anchor");
        AnchorDiagram(context, 1);

        // Two steppers instead of a read-only label: the offsets were visible but not editable, and
        // dragging the overlay is not possible from the keyboard.
        Row(context, 2, "Offset from the anchor");
        OffsetSteppers(context, 2);

        Row(context, 3, "Reset position");
        Button(context, ContentRight - 132f, RowTop(3) + 8f, 132f, 30f, "Reset", Control.ResetPosition);

        Hint(context, 4, "Drag the overlay while it is unlocked to move it; the offset is remembered per display.");
    }

    private void DrawAdvancedSection(ID2D1DeviceContext context)
    {
        Row(context, 0, "Settings folder");
        Button(context, ContentRight - 132f, RowTop(0) + 8f, 132f, 30f, "Open", Control.OpenData);

        // The size belongs on the row it describes, not in a footer sentence that also carried the
        // version number.
        var (entries, bytes) = _cacheStats();
        RowWithNote(context, 1, "Lyrics cache", $"{entries} entries \u00b7 {FormatBytes(bytes)}");
        Button(context, ContentRight - 132f, RowTop(1) + 8f, 132f, 30f, "Open", Control.OpenCache);

        // Imported files are the listener's own; they are not the cache and Clear must not touch them.
        var (localEntries, localBytes) = _localStats();
        RowWithNote(context, 2, "Local lyric files", $"{localEntries} files \u00b7 {FormatBytes(localBytes)}");
        Button(context, ContentRight - 132f, RowTop(2) + 8f, 132f, 30f, "Open", Control.OpenLocal);

        Row(context, 3, "Clear lyrics cache");
        Button(context, ContentRight - 132f, RowTop(3) + 8f, 132f, 30f, "Clear", Control.ClearCache);

        // Start with Windows left for Behaviour; what is left here is housekeeping, and the one
        // destructive action closes the section. The version is in the footer, said once.
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        }

        return $"{Math.Max(0, bytes) / 1024.0:0} KB";
    }

    /// <summary>
    /// The players: who is being followed, then the priority order, then the one button that can be
    /// pressed without knowing an app user model id. The grid is fixed at eight rows - one status row,
    /// six priority slots and one add row - because the window has no scrollbar and a list that grew
    /// would push the footer off the bottom.
    /// </summary>
    private void DrawPlayersSection(ID2D1DeviceContext context)
    {
        // Asked once for the whole section: three separate calls could disagree if a player was closed
        // in between, and the pin, the slots and the add row all describe the same moment.
        var live = LiveSources();
        var following = live.Count > 0
            ? SourceSelector.Choose(live, _config.SourcePriority, _config.SourcePinned)
            : SourceChoice.None;

        var pinned = !string.IsNullOrWhiteSpace(_config.SourcePinned);

        // Row 0: the state and its switch. The value is the pinned player's name derived from the app
        // user model id - the player may well be closed while it is pinned, so the name cannot be read
        // off a live session. It reads as one line: Following, the name, the switch that pins it.
        Row(context, 0, "Following");
        Label(
            context,
            pinned ? Fit(NowPlayingSnapshot.FriendlyNameFor(_config.SourcePinned), 28) : "Automatic",
            ContentRight - 46f - 12f - 220f,
            RowTop(0),
            220f,
            RowHeightDip,
            12.5f,
            FontWeight.Normal,
            pinned ? ColorAccent : ColorTextDim,
            TextAlignment.Trailing);

        // Clearing a pin has to stay possible with nothing playing, because that is exactly when a
        // stale pin gets noticed; turning one on needs a player to name.
        Toggle(context, Control.SourcePin, pinned, pinned || live.Count > 0);

        // Rows 1..6: one slot per priority entry. Empty slots draw nothing but keep their height, so
        // the buttons never move out from under the mouse as entries are removed.
        var count = _config.SourcePriority.Count;

        for (var slot = 0; slot < SourceSelector.MaxPriority; slot++)
        {
            if (slot >= count)
            {
                continue;
            }

            var row = slot + 1;
            var top = RowTop(row) + ((RowHeightDip - RankButtonWidth) / 2f);
            var left = ContentRight - RankButtonsWidth;

            // The label is never blank: NormalisePriority fills in the app user model id on load.
            // The number is part of the label: six rows only read as an order if the order is written.
            Row(context, row, Fit($"{slot + 1} \u00b7 {_config.SourcePriority[slot].Label}", 36));

            // The arrow that cannot do anything is drawn dimmed rather than left out: a row with a
            // different number of buttons would shift the column as the list changes length.
            Button(
                context,
                left,
                top,
                RankButtonWidth,
                RankButtonWidth,
                "\u2191",
                (Control)((int)Control.SourceUpBase + slot),
                slot > 0);

            Button(
                context,
                left + RankButtonWidth + RankButtonGap,
                top,
                RankButtonWidth,
                RankButtonWidth,
                "\u2193",
                (Control)((int)Control.SourceDownBase + slot),
                slot < count - 1);

            Button(
                context,
                left + ((RankButtonWidth + RankButtonGap) * 2f),
                top,
                RankButtonWidth,
                RankButtonWidth,
                "\u2715",
                (Control)((int)Control.SourceRemoveBase + slot),
                true);
        }

        // Row 7: add the player publishing right now. It adds whoever is being followed, so the
        // button, the pin and the list all mean the same thing by "what is playing".
        const float addWidth = 190f;

        var known = following.HasChoice && SourceSelector.IndexOf(_config.SourcePriority, following.Aumid) >= 0;
        var full = count >= SourceSelector.MaxPriority;
        var canAdd = following.HasChoice && (known || !full);

        Label(
            context,
            !following.HasChoice
                ? "No player is publishing to Windows right now."
                : canAdd
                    ? $"Following: {Fit(NameFor(live, following), 20)}. Adding puts it first."
                    : "The list is full. Remove one before adding another.",
            ContentLeft,
            RowTop(7),
            ContentWidth - addWidth - 24f,
            RowHeightDip,
            12f,
            FontWeight.Normal,
            ColorTextFaint);

        Button(
            context,
            ContentRight - addWidth,
            RowTop(7) + 8f,
            addWidth,
            30f,
            "Add what is playing",
            Control.SourceAdd,
            canAdd);
    }

    /// <summary>
    /// The players publishing to Windows right now, or nothing. The host owns the media sessions, so
    /// the window asks for a fresh answer every time it draws rather than caching one: a stale list
    /// would offer to add a player that has already been closed.
    /// </summary>
    private IReadOnlyList<SourceCandidate> LiveSources()
    {
        try
        {
            return _liveSources() ?? [];
        }
        catch (Exception)
        {
            // Asking the host must never be what breaks the window. An empty answer draws as "nothing
            // is publishing", which is a state the section already has to handle.
            return [];
        }
    }

    /// <summary>
    /// The friendliest name for the session the selector picked: the label the candidate carries when
    /// it has one, and the app user model id mapping otherwise. Matched by key, because two sessions
    /// can share an app user model id and only the key says which one was meant.
    /// </summary>
    private static string NameFor(IReadOnlyList<SourceCandidate> live, SourceChoice choice)
    {
        foreach (var candidate in live)
        {
            if (string.Equals(candidate.Key, choice.Key, StringComparison.Ordinal))
            {
                var label = candidate.Label.Trim();
                return label.Length > 0 ? label : NowPlayingSnapshot.FriendlyNameFor(candidate.Aumid);
            }
        }

        return NowPlayingSnapshot.FriendlyNameFor(choice.Aumid);
    }

    /// <summary>
    /// A list label short enough for one row. An app user model id the friendly-name mapping does not
    /// know is stored as-is, and forty characters of it would draw under the reorder buttons.
    /// </summary>
    private static string Fit(string text, int limit) =>
        text.Length <= limit ? text : text[..(limit - 1)] + "\u2026";

    private static float RowTop(int index) => RowsTopDip + (index * RowHeightDip);

    private void Row(ID2D1DeviceContext context, int index, string label, bool child = false, bool enabled = true)
    {
        if (child)
        {
            // The rail marks a row that exists because of the row above it; the same 40 % accent the
            // navigation uses for the section that is open.
            FillRound(context, ContentLeft, RowTop(index) + 14f, 2f, 18f, 1f, ColorAccent & 0x66FFFFFF);
        }

        Label(
            context,
            label,
            child ? ContentLeft + 14f : ContentLeft,
            RowTop(index),
            ContentWidth - 200f,
            RowHeightDip,
            13.5f,
            FontWeight.Normal,
            enabled ? ColorText : ColorTextFaint);
    }

    /// <summary>
    /// A rule and a caption at the top of a row. It costs no height: the row's own gap is the space it
    /// uses, which is what keeps the eight-row budget intact while the section gains four groups.
    /// </summary>
    private void Group(ID2D1DeviceContext context, int index, string caption)
    {
        var y = RowTop(index) + 1f;

        Label(context, caption, ContentLeft, y - 8f, 104f, 18f, 9.5f, FontWeight.SemiBold, ColorTextFaint);
        Fill(context, R(ContentLeft + 112f, y, ContentRight, y + 1f), ColorLine);
    }

    /// <summary>A row whose label carries a second, quieter line: what the preset sets, how big the cache is.</summary>
    private void RowWithNote(ID2D1DeviceContext context, int index, string label, string note)
    {
        Label(context, label, ContentLeft, RowTop(index) - 7f, ContentWidth - 200f, RowHeightDip, 13.5f, FontWeight.Normal, ColorText);
        Label(context, note, ContentLeft, RowTop(index) + 22f, ContentWidth - 180f, 16f, 11f, FontWeight.Normal, ColorTextFaint);
    }

    private void Hint(ID2D1DeviceContext context, int index, string text) =>
        Label(context, text, ContentLeft, RowTop(index) + 12f, ContentWidth, 40f, 12f, FontWeight.Normal, ColorTextFaint);

    /// <summary>
    /// A switch. <paramref name="enabled"/> false draws it dimmed and drops the hover state: a toggle
    /// that cannot do anything should not look like it will.
    /// </summary>
    private void Toggle(ID2D1DeviceContext context, Control id, bool value, bool enabled = true)
    {
        const float width = 46f;
        const float height = 26f;
        const float knob = 20f;

        var left = ContentRight - width;
        var top = RowTop(RowOf(id)) + ((RowHeightDip - height) / 2f);
        var hot = enabled && _hover == (int)id;

        // A switch that cannot act stays neutral rather than lit: an accent track with a faint knob
        // reads as "on but broken", where a grey track reads as "not now".
        FillRound(context, left, top, width, height, height / 2f,
            value && enabled ? ColorAccent : (hot ? ColorControlHot : ColorControl));

        var knobLeft = value ? left + width - knob - 3f : left + 3f;
        FillRound(context, knobLeft, top + 3f, knob, knob, knob / 2f, enabled ? ColorKnob : ColorTextFaint);

        if (enabled)
        {
            Hit((int)id, R(left - 8f, top - 8f, left + width + 8f, top + height + 8f));
        }
    }

    private void Segmented(ID2D1DeviceContext context, (string Text, Control Id, bool Selected)[] options)
    {
        const float height = 30f;
        const float gap = 6f;

        var width = options.Length == 3 ? 84f : 100f;
        var total = (width * options.Length) + (gap * (options.Length - 1));
        var left = ContentRight - total;
        var top = RowTop(RowOf(options[0].Id)) + ((RowHeightDip - height) / 2f);

        foreach (var option in options)
        {
            var hot = _hover == (int)option.Id;
            var background = option.Selected ? ColorSelected : (hot ? ColorControlHot : ColorControl);

            FillRound(context, left, top, width, height, 8f, background);
            Label(
                context,
                option.Text,
                left,
                top,
                width,
                height,
                12.5f,
                option.Selected ? FontWeight.SemiBold : FontWeight.Normal,
                option.Selected ? ColorAccent : ColorTextDim,
                TextAlignment.Center);

            Hit((int)option.Id, R(left, top, left + width, top + height));
            left += width + gap;
        }
    }

    private void Stepper(ID2D1DeviceContext context, string value, Control down, Control up, bool enabled = true)
    {
        const float height = 30f;
        const float button = 30f;
        const float gap = 6f;
        const float valueWidth = 84f;

        var total = (button * 2) + valueWidth + (gap * 2);
        var left = ContentRight - total;
        var top = RowTop(RowOf(down)) + ((RowHeightDip - height) / 2f);

        Button(context, left, top, button, height, "\u2212", down, enabled);
        FillRound(context, left + button + gap, top, valueWidth, height, 8f, ColorControl);
        Label(
            context,
            value,
            left + button + gap,
            top,
            valueWidth,
            height,
            12.5f,
            FontWeight.SemiBold,
            enabled ? ColorText : ColorTextFaint,
            TextAlignment.Center);

        Button(context, left + button + gap + valueWidth + gap, top, button, height, "+", up, enabled);
    }

    /// <summary>
    /// The offset as the bar prints it: the total at twenty pixels with the two nudges either side. The
    /// three gestures - this, the overlay bar and the hotkeys - are one step of a quarter second.
    /// </summary>
    private void TimingReadout(ID2D1DeviceContext context, int index, string value)
    {
        const float button = 30f;
        const float valueWidth = 96f;
        const float gap = 6f;

        var total = (button * 2) + valueWidth + (gap * 2);
        var left = ContentRight - total;
        var top = RowTop(index) + 8f;

        Button(context, left, top, button, 30f, "\u2212", Control.OffsetDown);
        Label(context, value, left + button + gap, top, valueWidth, 30f, 20f, FontWeight.SemiBold, ColorText, TextAlignment.Center);
        Button(context, left + button + gap + valueWidth + gap, top, button, 30f, "+", Control.OffsetUp);
    }

    /// <summary>
    /// The stored offset in pixels, editable. Dragging the overlay stays the fast path; this is the one
    /// that works from a keyboard, and it is why the offsets stopped being a read-only label.
    /// </summary>
    private void OffsetSteppers(ID2D1DeviceContext context, int index)
    {
        const float button = 26f;
        const float valueWidth = 54f;
        const float gap = 4f;
        const float pairGap = 14f;

        var pairWidth = 18f + button + gap + valueWidth + gap + button;
        var left = ContentRight - (pairWidth * 2f) - pairGap;
        var top = RowTop(index) + 10f;

        OffsetPair(context, left, top, "X", _config.OffsetX, Control.OffsetXDown, Control.OffsetXUp, button, valueWidth, gap);
        OffsetPair(context, left + pairWidth + pairGap, top, "Y", _config.OffsetY, Control.OffsetYDown, Control.OffsetYUp, button, valueWidth, gap);
    }

    private void OffsetPair(
        ID2D1DeviceContext context,
        float left,
        float top,
        string axis,
        int value,
        Control down,
        Control up,
        float button,
        float valueWidth,
        float gap)
    {
        Label(context, axis, left, top - 8f, 16f, 42f, 9.5f, FontWeight.SemiBold, ColorTextFaint);

        var x = left + 18f;

        Button(context, x, top, button, 26f, "\u2212", down);
        x += button + gap;

        FillRound(context, x, top, valueWidth, 26f, 7f, ColorControl);
        Label(context, value.ToString(), x, top, valueWidth, 26f, 12f, FontWeight.SemiBold, ColorText, TextAlignment.Center);
        x += valueWidth + gap;

        Button(context, x, top, button, 26f, "+", up);
    }

    /// <summary>
    /// Three monitors, one per anchor, with a bar where the overlay would sit. A position is easier to
    /// recognise than to read, and the offsets below are measured from the edge this shows.
    /// </summary>
    private void AnchorDiagram(ID2D1DeviceContext context, int index)
    {
        const float tileWidth = 54f;
        const float tileHeight = 34f;
        const float gap = 8f;

        var total = (tileWidth * 3f) + (gap * 2f);
        var left = ContentRight - total;
        var top = RowTop(index) + 6f;

        for (var i = 0; i < 3; i++)
        {
            var anchor = i switch
            {
                0 => OverlayAnchor.BottomCenter,
                1 => OverlayAnchor.TopCenter,
                _ => OverlayAnchor.Center
            };

            var id = i switch
            {
                0 => Control.AnchorBottom,
                1 => Control.AnchorTop,
                _ => Control.AnchorCentre
            };

            // Nine pixels in from either side, so the bar reads as the overlay's own width.
            var barTop = i switch
            {
                0 => top + 24f,
                1 => top + 6f,
                _ => top + 15f
            };

            var selected = _config.Anchor == anchor;
            var tileLeft = left + (i * (tileWidth + gap));

            FillRound(context, tileLeft, top, tileWidth, tileHeight, 6f, selected ? ColorSelected : ColorControl);
            FillRound(context, tileLeft + 9f, barTop, tileWidth - 18f, 4f, 2f, selected ? ColorAccent : ColorTextFaint);
            Hit((int)id, R(tileLeft, top, tileLeft + tileWidth, top + tileHeight));
        }
    }

    private void Slider(ID2D1DeviceContext context, Control id, string value)
    {
        const float width = 150f;
        const float trackHeight = 6f;
        const float knob = 16f;

        var left = ContentRight - width;
        var centerY = RowTop(RowOf(id)) + (RowHeightDip / 2f);
        var top = centerY - (trackHeight / 2f);

        Label(
            context,
            value,
            left - 70f,
            RowTop(RowOf(id)),
            60f,
            RowHeightDip,
            12.5f,
            FontWeight.Normal,
            ColorTextDim,
            TextAlignment.Trailing);

        FillRound(context, left, top, width, trackHeight, trackHeight / 2f, ColorControl);

        var t = (float)Math.Clamp(_config.BackgroundOpacity, 0.0, 1.0);
        FillRound(context, left, top, Math.Max(knob, t * width), trackHeight, trackHeight / 2f, ColorAccent);
        FillRound(context, left + (t * width) - (knob / 2f), centerY - (knob / 2f), knob, knob, knob / 2f,
            _hover == (int)id || _draggingOpacity ? ColorKnobHot : ColorKnob);

        _sliderLeft = left;
        _sliderWidth = width;

        Hit((int)id, R(left - 10f, centerY - 14f, left + width + 10f, centerY + 14f));
    }

    /// <summary>
    /// A labelled pill button. <paramref name="enabled"/> false draws the text faint and drops the hover
    /// state, which is how the add row says that there is nothing to add without hiding the button and
    /// leaving a gap where the user expects one.
    /// </summary>
    private void Button(
        ID2D1DeviceContext context,
        float left,
        float top,
        float width,
        float height,
        string text,
        Control id,
        bool enabled = true)
    {
        var hot = enabled && _hover == (int)id;
        FillRound(context, left, top, width, height, 8f, hot ? ColorControlHot : ColorControl);
        Label(context, text, left, top, width, height, 12.5f, FontWeight.Normal, enabled ? ColorText : ColorTextFaint, TextAlignment.Center);

        // A dead button takes no hover and no click: the row that depends on another row has to look
        // and behave like it is waiting for it.
        if (enabled)
        {
            Hit((int)id, R(left, top, left + width, top + height));
        }
    }

    /// <summary>The row each control lives in, so a control can be positioned without a second table.</summary>
    private static int RowOf(Control id) => id switch
    {
        // Lyrics
        Control.ModeSingle or Control.ModePeek or Control.ModeMulti => 0,
        Control.LinesDown or Control.LinesUp => 1,
        Control.FontActiveDown or Control.FontActiveUp => 2,
        Control.FontInactiveDown or Control.FontInactiveUp => 3,
        Control.KaraokeToggle => 4,
        Control.OledBlackout => 5,
        Control.Header => 6,
        Control.InfoPopupDown or Control.InfoPopupUp => 7,
        Control.OledAutoHideDown or Control.OledAutoHideUp => 8,
        Control.ProgressBar => 9,
        Control.ShowTranslation => 10,

        // Appearance
        Control.OledPreset => 0,
        Control.OledOpacitySlider => 1,
        Control.OledHighlight => 2,
        Control.OledDimDown or Control.OledDimUp => 3,
        Control.OledPixelShift => 4,
        Control.OledOutline => 5,
        Control.ThemeDefault or Control.ThemeWarm or Control.ThemeCool or Control.ThemeMono => 6,
        Control.WeightNormal or Control.WeightSemiBold or Control.WeightBold => 7,
        Control.StageMode => 8,
        Control.StageChroma => 9,

        // Behaviour
        Control.HidePaused => 0,
        Control.HideNoLyrics => 1,
        Control.HideInstrumental => 2,
        Control.WarnPlayer => 3,
        Control.NotifyMissingLyrics => 4,
        Control.StartWithWindows => 5,
        Control.HideAppAdd => 6,
        Control.HideAppClear => 7,
        Control.HideWhenMic => 8,

        // Timing
        Control.OffsetDown or Control.OffsetUp => 1,
        Control.TimingRemember => 2,
        Control.TimingUseGlobal => 3,
        Control.TimingOutputDown or Control.TimingOutputUp => 4,
        Control.TimingClearTrack => 5,
        Control.OffsetReset => 6,

        // Position
        Control.MonitorCycle => 0,
        Control.AnchorBottom or Control.AnchorTop or Control.AnchorCentre => 1,
        Control.OffsetXDown or Control.OffsetXUp or Control.OffsetYDown or Control.OffsetYUp => 2,
        Control.ResetPosition => 3,

        // Advanced
        Control.OpenData => 0,
        Control.OpenCache => 1,
        Control.OpenLocal => 2,
        Control.ClearCache => 3,

        // Players
        Control.SourcePin => 0,
        _ => 0
    };

    /// <summary>
    /// Record a clickable area. Inside the scrolled body the rectangle moves with the row it belongs
    /// to and is cut to the viewport, so a row that has scrolled under the header cannot answer a click
    /// aimed at the header above it.
    /// </summary>
    private void Hit(int id, Rect rect)
    {
        if (_hitScroll == 0f)
        {
            _hits.Add((id, rect));
            return;
        }

        var top = MathF.Max(rect.Top - _hitScroll, ViewportTop);
        var bottom = MathF.Min(rect.Bottom - _hitScroll, ViewportBottom);

        if (bottom > top)
        {
            _hits.Add((id, R(rect.Left, top, rect.Right, bottom)));
        }
    }

    /// <summary>Height of the open section's rows, in DIP.</summary>
    private float ContentHeight => SectionRows[(int)_section] * RowHeightDip;

    // The rows start at RowTop(0) = RowsTopDip, which is below ViewportTop: a scroll range of
    // ContentHeight - viewport would leave that 24 DIP gap unreachable and clip the last rows.
    private float MaxScroll => MathF.Max(0f, ContentHeight + (RowsTopDip - ViewportTop) - (ViewportBottom - ViewportTop));

    /// <summary>
    /// A scrollbar only while the section is taller than the viewport, proportional with a floor so a
    /// long section still leaves something to grab.
    /// </summary>
    private void DrawScrollbar(ID2D1DeviceContext context)
    {
        var max = MaxScroll;

        if (max <= 0f)
        {
            _scroll = 0f;
            return;
        }

        _scroll = Math.Clamp(_scroll, 0f, max);

        const float width = 4f;

        var viewport = ViewportBottom - ViewportTop;

        // Eighteen DIP from the edge rather than fourteen: the resize border owns the outer six,
        // and a scrollbar whose last pixels start a resize is a scrollbar that misses.
        var left = _widthDip - 18f;
        var top = ViewportTop + 2f;
        var length = viewport - 4f;
        var thumb = MathF.Max(36f, length * (viewport / ContentHeight));
        var offset = (length - thumb) * (_scroll / max);
        var hot = _hover == (int)Control.Scrollbar || _draggingScroll;

        FillRound(context, left, top, width, length, 2f, ColorControl);
        FillRound(context, left, top + offset, width, thumb, 2f, hot ? ColorAccent : ColorTextFaint);

        _scrollTrackTop = top;
        _scrollTrackLength = length;
        _scrollThumbLength = thumb;

        Hit((int)Control.Scrollbar, R(left - 6f, ViewportTop, left + width + 6f, ViewportBottom));
    }

    /// <summary>Drag the thumb: where it is on the track is the fraction of the section being shown.</summary>
    private void SetScrollFrom(float y)
    {
        if (MaxScroll <= 0f || _scrollTrackLength <= _scrollThumbLength)
        {
            return;
        }

        var t = Math.Clamp(
            (y - _scrollTrackTop - (_scrollThumbLength / 2f)) / (_scrollTrackLength - _scrollThumbLength),
            0f,
            1f);

        SetScroll(t * MaxScroll);
    }

    private void SetScroll(float value)
    {
        var clamped = Math.Clamp(value, 0f, MaxScroll);

        if (MathF.Abs(clamped - _scroll) < 0.5f)
        {
            return;
        }

        _scroll = clamped;
        Render();
    }

    /// <summary>
    /// The keyboard's focus ring, drawn from the recorded rectangles: it lands exactly on the control the
    /// next Tab would reach, and follows the scroll because those rectangles already did.
    /// </summary>
    private void DrawFocusRing(ID2D1DeviceContext context)
    {
        if (_focus < 0)
        {
            return;
        }

        foreach (var (id, rect) in _hits)
        {
            if (id != _focus)
            {
                continue;
            }

            const float radius = 9f;

            context.DrawRoundedRectangle(
                new RoundedRectangle(
                    new RectangleF(rect.Left - 3f, rect.Top - 3f, (rect.Right - rect.Left) + 6f, (rect.Bottom - rect.Top) + 6f),
                    radius,
                    radius),
                Text.Brush(context, 0x807CC4FF),
                1.5f);

            return;
        }
    }

    /// <summary>
    /// Move the focus to the next control the window draws, in draw order - which is reading order - and
    /// scroll it into view. Tab and Shift+Tab wrap, so the whole window is reachable from the keyboard.
    /// </summary>
    private void MoveFocus(int direction)
    {
        if (_hits.Count == 0)
        {
            return;
        }

        var index = -1;

        for (var i = 0; i < _hits.Count; i++)
        {
            if (_hits[i].Id == _focus)
            {
                index = i;
                break;
            }
        }

        // The close button is a window control rather than a setting: Esc already closes the window,
        // and a tab stop that dismisses it is a trap in the middle of the cycle.
        for (var step = 0; step < _hits.Count; step++)
        {
            index = index < 0
                ? (direction > 0 ? 0 : _hits.Count - 1)
                : (index + direction + _hits.Count) % _hits.Count;

            if (_hits[index].Id != (int)Control.Close)
            {
                break;
            }
        }

        _focus = _hits[index].Id;
        Reveal(_hits[index].Rect);
        Render();
    }

    /// <summary>Scroll just enough for a rectangle to be fully inside the viewport.</summary>
    private void Reveal(Rect rect)
    {
        // Only the body scrolls. The navigation sits above the viewport, so revealing one of its rows
        // would drag the section sideways under a header that did not move.
        if (rect.Left < ContentLeft)
        {
            return;
        }

        if (rect.Top < ViewportTop)
        {
            _scroll = MathF.Max(0f, _scroll - (ViewportTop - rect.Top) - 8f);
        }
        else if (rect.Bottom > ViewportBottom)
        {
            _scroll += rect.Bottom - ViewportBottom + 8f;
        }

        _scroll = Math.Clamp(_scroll, 0f, MaxScroll);
    }

    private TextCache Text => _text ??= new TextCache(_host!.WriteFactory!);

    private void Fill(ID2D1DeviceContext context, Rect rect, uint argb) =>
        Text.Fill(context, rect, argb);

    private void FillRound(ID2D1DeviceContext context, float left, float top, float width, float height, float radius, uint argb) =>
        Text.FillRound(context, left, top, width, height, radius, argb);

    private void Label(
        ID2D1DeviceContext context,
        string text,
        float left,
        float top,
        float width,
        float height,
        float size,
        FontWeight weight,
        uint argb,
        TextAlignment align = TextAlignment.Leading)
    {
        Text.Label(context, text, left, top, width, height, size, weight, argb, align);
    }

    private static Rect R(float left, float top, float right, float bottom) =>
        new(RectangleF.FromLTRB(left, top, right, bottom));

    private (float X, float Y) ToDip(nint lParam)
    {
        var raw = lParam.ToInt64();
        var scale = _dpi / 96f;

        return (
            (short)(raw & 0xFFFF) / scale,
            (short)((raw >> 16) & 0xFFFF) / scale);
    }

    public override void Dispose()
    {
        ReleaseDeviceResources();
        _host?.Dispose();
        _host = null;

        base.Dispose();
    }
}
