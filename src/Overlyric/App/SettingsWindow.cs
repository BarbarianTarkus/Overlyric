using System.Drawing;
using System.Numerics;
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
    Timing
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
    private enum Section
    {
        Overlay = 0,
        Shortcuts = 1,
        Oled = 2,
        Behaviour = 3,
        Timing = 4,
        Position = 5,
        Advanced = 6,

        /// <summary>
        /// Which player the overlay follows. Appended rather than slotted in next to Behaviour so the
        /// existing navigation ids keep their values; the order here is the order in the side bar.
        /// </summary>
        Source = 7
    }

    /// <summary>Identifiers for every clickable thing. Values must stay unique.</summary>
    private enum Control
    {
        NavOverlay = 1,
        NavShortcuts = 2,
        NavOled = 3,
        NavBehaviour = 4,
        NavTiming = 5,
        NavPosition = 6,
        NavAdvanced = 7,

        /// <summary>
        /// The Source section's navigation entry. It has to follow the others with no gap, because the
        /// click decoder turns a nav id back into a <see cref="Section"/> by subtracting NavOverlay.
        /// </summary>
        NavSource = 8,

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
        SourceRemoveBase = 100
    }

    private const float WidthDip = 780f;
    private const float HeightDip = 540f;
    private const float TitleHeightDip = 44f;
    private const float WindowHeightDip = HeightDip + TitleHeightDip;
    private const float NavWidthDip = 196f;
    private const float PadDip = 30f;
    private const float RowHeightDip = 46f;
    private const float RowsTopDip = 132f;
    private const float ContentLeft = NavWidthDip + PadDip;
    private const float ContentRight = WidthDip - PadDip;
    private const float ContentWidth = ContentRight - ContentLeft;

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
        ["Overlay", "Shortcuts", "OLED", "Behaviour", "Timing", "Position", "Advanced", "Source"];

    private static readonly string[] SectionHints =
    [
        "How the lyrics look on screen.",
        "Click a shortcut, then press the keys you want.",
        "Leave nothing static on the screen for long.",
        "When the overlay should get out of the way.",
        "Correct lyrics that run early or late.",
        "Where the overlay sits on the monitor.",
        "Folders and version information.",
        "A pinned player always wins, then this order, then Windows."
    ];

    private readonly AppConfig _config;
    private readonly Action<SettingsChange> _changed;
    private readonly Func<int, string, string?> _assignShortcut;
    private readonly Func<string, bool> _isUnavailable;
    private readonly Action _openDataFolder;
    private readonly Action _openCacheFolder;
    private readonly string _version;

    /// <summary>
    /// The players publishing to Windows at the moment the Source section is drawn. A callback rather
    /// than a list captured at construction: the window outlives any single session, so a snapshot
    /// would offer to add players that closed long ago.
    /// </summary>
    private readonly Func<IReadOnlyList<SourceCandidate>> _liveSources;

    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = [];
    private readonly Dictionary<(float Size, FontWeight Weight, TextAlignment Align), IDWriteTextFormat> _formats = [];
    private readonly List<(int Id, Rect Rect)> _hits = [];

    private CompositionHost? _host;
    private Section _section = Section.Overlay;
    private int _hover = -1;
    private bool _open;
    private bool _draggingOpacity;
    private float _sliderLeft;
    private float _sliderWidth;
    private float _dpi = 96f;

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
        string version,
        Func<IReadOnlyList<SourceCandidate>> liveSources)
    {
        _config = config;
        _changed = changed;
        _assignShortcut = assignShortcut;
        _isUnavailable = isUnavailable;
        _openDataFolder = openDataFolder;
        _openCacheFolder = openCacheFolder;
        _version = version;
        _liveSources = liveSources;
    }

    private static nint _classBrush;

    protected override string ClassName => "OverlyricSettingsWindow";

    protected override int WindowStyle => WS_POPUP;

    /// <summary>
    /// Frameless, like the overlay. A themed window frame does not get painted for a window whose
    /// content comes from DirectComposition - the desktop shows through a few pixels of each side -
    /// so the title bar is drawn here instead and the whole window is client area.
    /// </summary>
    protected override int WindowExtraStyle => WS_EX_NOREDIRECTIONBITMAP | WS_EX_APPWINDOW;

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

    public void Show()
    {
        if (Handle == IntPtr.Zero)
        {
            Create();
        }
        else if (!_open)
        {
            ApplyDpi();
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
    public void ShowSourceSection()
    {
        _section = Section.Source;
        Show();
    }

    private void Create()
    {
        CreateWindow("Overlyric Settings", (int)WidthDip, (int)HeightDip);

        // A fixed-size window: no maximise box, no resize border, nothing to reflow.
        var style = GetWindowLongPtrW(Handle, -16).ToInt64();
        style &= ~0x00010000L; // WS_MAXIMIZEBOX
        SetWindowLongPtrW(Handle, -16, new nint(style));

        ApplyDpi();

        // Dark title bar, so the window does not sit in a bright frame.
        var dark = 1;
        if (DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));
        }
    }

    private void ApplyDpi()
    {
        var dpi = GetDpiForWindow(Handle);
        _dpi = dpi > 0 ? dpi : 96f;

        // Frameless: the window is exactly the client area.
        var clientWidth = ToPixels(WidthDip);
        var clientHeight = ToPixels(WindowHeightDip);
        var windowWidth = clientWidth;
        var windowHeight = clientHeight;

        var monitor = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        var x = 0;
        var y = 0;

        if (GetMonitorInfoW(monitor, ref info))
        {
            x = info.rcWork.Left + ((info.rcWork.Width - windowWidth) / 2);
            y = info.rcWork.Top + ((info.rcWork.Height - windowHeight) / 2);
        }

        SetWindowPos(Handle, IntPtr.Zero, x, y, windowWidth, windowHeight, SWP_NOACTIVATE | SWP_NOZORDER);

        _host?.Resize(clientWidth, clientHeight, _dpi);
        EnsureHost(clientWidth, clientHeight);
        Render();
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
            EnsureHost(ToPixels(WidthDip), ToPixels(HeightDip));
            Render();
        }
        catch (Exception)
        {
            // Leave the window blank rather than take the process down.
        }
    }

    private void ReleaseDeviceResources()
    {
        foreach (var brush in _brushes.Values)
        {
            brush.Dispose();
        }

        _brushes.Clear();

        foreach (var format in _formats.Values)
        {
            format.Dispose();
        }

        _formats.Clear();
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

            case WM_MOUSELEAVE:
                if (_hover != -1)
                {
                    _hover = -1;
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
                ApplyDpi();
                return IntPtr.Zero;

            case WM_DISPLAYCHANGE:
                ApplyDpi();
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

        return new IntPtr(y < TitleHeightDip && !overClose ? HTCAPTION : HTCLIENT);
    }

    private void OnMouseDown(nint lParam)
    {
        var (x, y) = ToDip(lParam);

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

            Activate(id);
            return;
        }
    }

    private void OnMouseUp()
    {
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
            if (virtualKey == VK_ESCAPE)
            {
                Hide();
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

        switch ((Control)id)
        {
            case Control.NavOverlay:
            case Control.NavShortcuts:
            case Control.NavOled:
            case Control.NavBehaviour:
            case Control.NavTiming:
            case Control.NavPosition:
            case Control.NavAdvanced:
            case Control.NavSource:
                _section = (Section)(id - (int)Control.NavOverlay);
                Render();
                return;

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
                _config.LyricsOffsetMs = Math.Clamp(_config.LyricsOffsetMs - 250, -10000, 10000);
                Apply(SettingsChange.Timing);
                return;

            case Control.OffsetUp:
                _config.LyricsOffsetMs = Math.Clamp(_config.LyricsOffsetMs + 250, -10000, 10000);
                Apply(SettingsChange.Timing);
                return;

            case Control.OffsetReset:
                _config.LyricsOffsetMs = 0;
                Apply(SettingsChange.Timing);
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

            case Control.ResetPosition:
                SetAnchor(_config.Anchor);
                return;

            case Control.OpenData:
                _openDataFolder();
                return;

            case Control.OpenCache:
                _openCacheFolder();
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

            case Control.KaraokeToggle:
                _config.Karaoke = !_config.Karaoke;
                Apply(SettingsChange.Appearance);
                return;

            case Control.Close:
                Hide();
                return;
        }
    }

    /// <summary>
    /// A bundle for an OLED panel: no backdrop, no progress bar, the static header fades out, no
    /// highlight bar, dimmer surrounding lines, and the whole thing nudged around once a minute.
    /// </summary>
    private void ApplyOledPreset()
    {
        _config.BackgroundOpacity = 0.0;
        _config.ShowProgressBar = false;
        _config.HeaderAutoHideSeconds = 8;
        _config.ShowActiveLineHighlight = false;
        _config.InactiveLineOpacity = 0.4;
        _config.PixelShift = true;
        _config.TextOutline = true;
        _config.HideWhenPaused = true;

        _config.Save();
        _changed(SettingsChange.Appearance);
        _changed(SettingsChange.Visibility);
        Render();
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
        Fill(context, R(0f, 0f, WidthDip, WindowHeightDip), ColorWindow);

        DrawTitleBar(context);

        // Everything below the title bar is laid out in content coordinates.
        context.Transform = Matrix3x2.CreateTranslation(0f, TitleHeightDip);

        Fill(context, R(0f, 0f, NavWidthDip, HeightDip), ColorNav);
        Fill(context, R(NavWidthDip, 0f, NavWidthDip + 1f, HeightDip), ColorLine);

        DrawNavigation(context);

        Label(context, SectionTitles[(int)_section], ContentLeft, 44f, ContentWidth, 30f, 20f, FontWeight.SemiBold, ColorText);
        Label(context, SectionHints[(int)_section], ContentLeft, 76f, ContentWidth, 18f, 12.5f, FontWeight.Normal, ColorTextDim);
        Fill(context, R(ContentLeft, 106f, ContentRight, 107f), ColorLine);

        switch (_section)
        {
            case Section.Overlay:
                DrawOverlaySection(context);
                break;
            case Section.Shortcuts:
                DrawShortcutsSection(context);
                break;
            case Section.Oled:
                DrawOledSection(context);
                break;
            case Section.Behaviour:
                DrawBehaviourSection(context);
                break;
            case Section.Timing:
                DrawTimingSection(context);
                break;
            case Section.Position:
                DrawPositionSection(context);
                break;
            case Section.Source:
                DrawSourceSection(context);
                break;
            default:
                DrawAdvancedSection(context);
                break;
        }

        Label(
            context,
            $"Overlyric {_version}  ·  Esc to close",
            ContentLeft,
            HeightDip - 30f,
            ContentWidth,
            18f,
            11.5f,
            FontWeight.Normal,
            ColorTextFaint);

        context.Transform = Matrix3x2.Identity;
    }

    private void DrawTitleBar(ID2D1DeviceContext context)
    {
        Fill(context, R(0f, 0f, WidthDip, TitleHeightDip), ColorNav);
        Fill(context, R(0f, TitleHeightDip - 1f, WidthDip, TitleHeightDip), ColorLine);

        Label(
            context,
            "Overlyric Settings",
            22f,
            0f,
            WidthDip - 120f,
            TitleHeightDip,
            12.5f,
            FontWeight.SemiBold,
            ColorTextDim);

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

    private static Rect CloseRect() => R(WidthDip - 52f, 6f, WidthDip - 12f, TitleHeightDip - 6f);

    private void DrawNavigation(ID2D1DeviceContext context)
    {
        Label(context, "SETTINGS", 30f, 40f, NavWidthDip - 60f, 20f, 10.5f, FontWeight.SemiBold, ColorTextFaint);

        for (var i = 0; i < SectionTitles.Length; i++)
        {
            var id = (int)Control.NavOverlay + i;
            var top = 78f + (i * 42f);
            var selected = (int)_section == i;

            if (selected)
            {
                FillRound(context, 16f, top, NavWidthDip - 32f, 34f, 8f, ColorNavSelected);
            }
            else if (_hover == id)
            {
                FillRound(context, 16f, top, NavWidthDip - 32f, 34f, 8f, ColorNavHot);
            }

            Label(
                context,
                SectionTitles[i],
                30f,
                top,
                NavWidthDip - 60f,
                34f,
                13f,
                selected ? FontWeight.SemiBold : FontWeight.Normal,
                selected ? ColorText : ColorTextDim);

            Hit(id, R(16f, top, NavWidthDip - 16f, top + 34f));
        }
    }

    private void DrawOverlaySection(ID2D1DeviceContext context)
    {
        Row(context, 0, "Mode");
        Segmented(
            context,
            [
                ("Single line", Control.ModeSingle, _config.Mode == OverlayMode.SingleLine),
                ("Multi line", Control.ModeMulti, _config.Mode == OverlayMode.MultiLine)
            ]);

        Row(context, 1, "Lines shown");
        Stepper(context, _config.MultiLineCount.ToString(), Control.LinesDown, Control.LinesUp);

        Row(context, 2, "Show track info");
        Toggle(context, Control.Header, _config.ShowHeader);

        Row(context, 3, "Show progress bar");
        Toggle(context, Control.ProgressBar, _config.ShowProgressBar);

        Row(context, 4, "Lyric size");
        Stepper(context, _config.ActiveFontSize.ToString("0"), Control.FontActiveDown, Control.FontActiveUp);

        Row(context, 5, "Surrounding lines size");
        Stepper(context, _config.InactiveFontSize.ToString("0"), Control.FontInactiveDown, Control.FontInactiveUp);

        Row(context, 6, "Karaoke sweep");
        Toggle(context, Control.KaraokeToggle, _config.Karaoke);
    }

    private void DrawOledSection(ID2D1DeviceContext context)
    {
        Row(context, 0, "OLED preset");
        Button(context, ContentRight - 132f, RowTop(0) + 8f, 132f, 30f, "Apply preset", Control.OledPreset);

        Row(context, 1, "Backdrop opacity");
        Slider(context, Control.OledOpacitySlider, $"{Math.Round(_config.BackgroundOpacity * 100)}%");

        Row(context, 2, "Auto-hide track info");
        Stepper(
            context,
            _config.HeaderAutoHideSeconds == 0 ? "Never" : $"{_config.HeaderAutoHideSeconds} s",
            Control.OledAutoHideDown,
            Control.OledAutoHideUp);

        Row(context, 3, "Highlight the active line");
        Toggle(context, Control.OledHighlight, _config.ShowActiveLineHighlight);

        Row(context, 4, "Dim surrounding lines");
        Stepper(context, $"{Math.Round(_config.InactiveLineOpacity * 100)}%", Control.OledDimDown, Control.OledDimUp);

        Row(context, 5, "Pixel shift");
        Toggle(context, Control.OledPixelShift, _config.PixelShift);

        Row(context, 6, "Outline text");
        Toggle(context, Control.OledOutline, _config.TextOutline);
    }

    private void DrawShortcutsSection(ID2D1DeviceContext context)
    {
        for (var i = 0; i < ShortcutActions.All.Length; i++)
        {
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
        Row(context, 0, "Hide when paused");
        Toggle(context, Control.HidePaused, _config.HideWhenPaused);

        Row(context, 1, "Hide when no lyrics");
        Toggle(context, Control.HideNoLyrics, _config.HideWhenNoLyrics);

        Row(context, 2, "Hide instrumentals");
        Toggle(context, Control.HideInstrumental, _config.HideWhenInstrumental);

        Row(context, 3, "Warn when a player does not publish");
        Toggle(context, Control.WarnPlayer, _config.WarnWhenPlayerDoesNotPublish);
    }

    private void DrawTimingSection(ID2D1DeviceContext context)
    {
        Row(context, 0, "Lyric offset");
        Stepper(context, FormatOffset(_config.LyricsOffsetMs), Control.OffsetDown, Control.OffsetUp);

        Row(context, 1, "Reset to zero");
        Button(context, ContentRight - 132f, RowTop(1) + 8f, 132f, 30f, "Reset timing", Control.OffsetReset);

        Hint(context, 2, "Positive shows the lyrics later; use this when they run ahead of the music.");
    }

    private void DrawPositionSection(ID2D1DeviceContext context)
    {
        Row(context, 0, "Anchor");
        Segmented(
            context,
            [
                ("Bottom", Control.AnchorBottom, _config.Anchor == OverlayAnchor.BottomCenter),
                ("Top", Control.AnchorTop, _config.Anchor == OverlayAnchor.TopCenter),
                ("Centre", Control.AnchorCentre, _config.Anchor == OverlayAnchor.Center)
            ]);

        Row(context, 1, "Offset from the anchor");
        Label(
            context,
            $"{_config.OffsetX} , {_config.OffsetY} px",
            ContentRight - 200f,
            RowTop(1),
            200f,
            RowHeightDip,
            12.5f,
            FontWeight.Normal,
            ColorTextDim,
            TextAlignment.Trailing);

        Row(context, 2, "Reset position");
        Button(context, ContentRight - 132f, RowTop(2) + 8f, 132f, 30f, "Reset", Control.ResetPosition);

        Hint(context, 3, "Drag the overlay while it is unlocked to move it; the offset is measured from the anchored edge.");
    }

    private void DrawAdvancedSection(ID2D1DeviceContext context)
    {
        Row(context, 0, "Settings folder");
        Button(context, ContentRight - 132f, RowTop(0) + 8f, 132f, 30f, "Open", Control.OpenData);

        Row(context, 1, "Lyrics cache");
        Button(context, ContentRight - 132f, RowTop(1) + 8f, 132f, 30f, "Open", Control.OpenCache);

        Hint(context, 2, $"Version {_version}. Settings live in %LOCALAPPDATA%\\Overlyric.");
    }

    /// <summary>
    /// The source section: the pin, then the priority order, then the one button that can be pressed
    /// without knowing an app user model id. The grid is fixed at eight rows - one pin, six priority
    /// slots and one add row - because the window has no scrollbar and a list that grew would push the
    /// footer off the bottom.
    /// </summary>
    private void DrawSourceSection(ID2D1DeviceContext context)
    {
        // Asked once for the whole section: three separate calls could disagree if a player was closed
        // in between, and the pin, the slots and the add row all describe the same moment.
        var live = LiveSources();
        var following = live.Count > 0
            ? SourceSelector.Choose(live, _config.SourcePriority, _config.SourcePinned)
            : SourceChoice.None;

        var pinned = !string.IsNullOrWhiteSpace(_config.SourcePinned);

        // Row 0: the pin, drawn above the list because it outranks it. The value is the pinned
        // player's name derived from the app user model id - the player may well be closed while it is
        // pinned, so the name cannot be read off a live session.
        Row(context, 0, "Pin one player");
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
            Row(context, row, Fit(_config.SourcePriority[slot].Label, 36));

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

    private void Row(ID2D1DeviceContext context, int index, string label) =>
        Label(context, label, ContentLeft, RowTop(index), ContentWidth - 200f, RowHeightDip, 13.5f, FontWeight.Normal, ColorText);

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

        FillRound(context, left, top, width, height, height / 2f, value ? ColorAccent : (hot ? ColorControlHot : ColorControl));

        var knobLeft = value ? left + width - knob - 3f : left + 3f;
        FillRound(context, knobLeft, top + 3f, knob, knob, knob / 2f, enabled ? ColorKnob : ColorTextFaint);

        Hit((int)id, R(left - 8f, top - 8f, left + width + 8f, top + height + 8f));
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

    private void Stepper(ID2D1DeviceContext context, string value, Control down, Control up)
    {
        const float height = 30f;
        const float button = 30f;
        const float gap = 6f;
        const float valueWidth = 84f;

        var total = (button * 2) + valueWidth + (gap * 2);
        var left = ContentRight - total;
        var top = RowTop(RowOf(down)) + ((RowHeightDip - height) / 2f);

        Button(context, left, top, button, height, "\u2212", down);
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
            ColorText,
            TextAlignment.Center);

        Button(context, left + button + gap + valueWidth + gap, top, button, height, "+", up);
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
        Hit((int)id, R(left, top, left + width, top + height));
    }

    /// <summary>The row each control lives in, so a control can be positioned without a second table.</summary>
    private int RowOf(Control id) => id switch
    {
        Control.ModeSingle or Control.ModeMulti => 0,
        Control.LinesDown or Control.LinesUp => 1,
        Control.Header => 2,
        Control.ProgressBar => 3,
        Control.FontActiveDown or Control.FontActiveUp => 4,
        Control.FontInactiveDown or Control.FontInactiveUp => 5,
        Control.KaraokeToggle => 6,
        Control.OledPreset => 0,
        Control.OledOpacitySlider => 1,
        Control.OledAutoHideDown or Control.OledAutoHideUp => 2,
        Control.OledHighlight => 3,
        Control.OledDimDown or Control.OledDimUp => 4,
        Control.OledPixelShift => 5,
        Control.OledOutline => 6,
        Control.HidePaused => 0,
        Control.HideNoLyrics => 1,
        Control.HideInstrumental => 2,
        Control.WarnPlayer => 3,
        Control.OffsetDown or Control.OffsetUp => 0,
        Control.OffsetReset => 1,
        Control.AnchorBottom or Control.AnchorTop or Control.AnchorCentre => 0,
        Control.ResetPosition => 2,
        Control.OpenData => 0,
        Control.OpenCache => 1,
        Control.SourcePin => 0,
        _ => 0
    };

    private void Hit(int id, Rect rect) => _hits.Add((id, rect));

    private static string FormatOffset(int milliseconds)
    {
        if (milliseconds == 0)
        {
            return "0 s";
        }

        return (milliseconds / 1000.0).ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture) + " s";
    }

    private void Fill(ID2D1DeviceContext context, Rect rect, uint argb) =>
        context.FillRectangle(rect, GetBrush(context, argb));

    private void FillRound(ID2D1DeviceContext context, float left, float top, float width, float height, float radius, uint argb) =>
        context.FillRoundedRectangle(
            new RoundedRectangle(new RectangleF(left, top, width, height), radius, radius),
            GetBrush(context, argb));

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
        if (width <= 0f || height <= 0f)
        {
            return;
        }

        var format = GetFormat(size, weight, align);
        var layout = _host!.WriteFactory!.CreateTextLayout(text, format, width, height);
        context.DrawTextLayout(new Vector2(left, top), layout, GetBrush(context, argb));
        layout.Dispose();
    }

    private IDWriteTextFormat GetFormat(float size, FontWeight weight, TextAlignment align)
    {
        var key = (size, weight, align);
        if (_formats.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var format = _host!.WriteFactory!.CreateTextFormat(
            "Segoe UI Variable Display",
            null!,
            weight,
            FontStyle.Normal,
            FontStretch.Normal,
            size);

        format.TextAlignment = align;
        format.ParagraphAlignment = ParagraphAlignment.Center;
        format.WordWrapping = WordWrapping.NoWrap;

        _formats[key] = format;
        return format;
    }

    private ID2D1SolidColorBrush GetBrush(ID2D1DeviceContext context, uint argb)
    {
        if (_brushes.TryGetValue(argb, out var cached))
        {
            return cached;
        }

        var brush = context.CreateSolidColorBrush(OverlayTheme.ToColor4(argb));
        _brushes[argb] = brush;
        return brush;
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
