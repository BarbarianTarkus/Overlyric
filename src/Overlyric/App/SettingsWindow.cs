using System.Drawing;
using System.Numerics;
using Overlyric.Interop;
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
/// handling, no reflow, no scrollbars - five sections that each fit.
/// </para>
/// </summary>
internal sealed class SettingsWindow : WindowBase
{
    private enum Section
    {
        Overlay = 0,
        Behaviour = 1,
        Timing = 2,
        Position = 3,
        Advanced = 4
    }

    /// <summary>Identifiers for every clickable thing. Values must stay unique.</summary>
    private enum Control
    {
        NavOverlay = 1,
        NavBehaviour = 2,
        NavTiming = 3,
        NavPosition = 4,
        NavAdvanced = 5,

        ModeSingle = 10,
        ModeMulti = 11,
        LinesDown = 12,
        LinesUp = 13,
        OpacitySlider = 14,
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
        Close = 34
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

    private static readonly string[] SectionTitles = ["Overlay", "Behaviour", "Timing", "Position", "Advanced"];

    private static readonly string[] SectionHints =
    [
        "How the lyrics look on screen.",
        "When the overlay should get out of the way.",
        "Correct lyrics that run early or late.",
        "Where the overlay sits on the monitor.",
        "Folders and version information."
    ];

    private readonly AppConfig _config;
    private readonly Action<SettingsChange> _changed;
    private readonly Action _openDataFolder;
    private readonly Action _openCacheFolder;
    private readonly string _version;

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

    public SettingsWindow(
        AppConfig config,
        Action<SettingsChange> changed,
        Action openDataFolder,
        Action openCacheFolder,
        string version)
    {
        _config = config;
        _changed = changed;
        _openDataFolder = openDataFolder;
        _openCacheFolder = openCacheFolder;
        _version = version;
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

        if (Handle != IntPtr.Zero)
        {
            ShowWindow(Handle, SW_HIDE);
        }
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
                if (wParam.ToInt64() == VK_ESCAPE)
                {
                    Hide();
                    return IntPtr.Zero;
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

            if (id == (int)Control.OpacitySlider)
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
        var opacity = 0.15 + (t * (1.0 - 0.15));

        if (Math.Abs(opacity - _config.BackgroundOpacity) < 0.001)
        {
            return;
        }

        _config.BackgroundOpacity = opacity;
        _changed(SettingsChange.Appearance);
        Render();
    }

    private void Activate(int id)
    {
        switch ((Control)id)
        {
            case Control.NavOverlay:
            case Control.NavBehaviour:
            case Control.NavTiming:
            case Control.NavPosition:
            case Control.NavAdvanced:
                _section = (Section)(id - (int)Control.NavOverlay);
                Render();
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

            case Control.Close:
                Hide();
                return;
        }
    }

    private void SetAnchor(OverlayAnchor anchor)
    {
        _config.Anchor = anchor;
        _config.OffsetX = 0;
        _config.OffsetY = anchor == OverlayAnchor.Center ? 0 : 90;
        Apply(SettingsChange.Placement);
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
            case Section.Behaviour:
                DrawBehaviourSection(context);
                break;
            case Section.Timing:
                DrawTimingSection(context);
                break;
            case Section.Position:
                DrawPositionSection(context);
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

        Row(context, 2, "Backdrop opacity");
        Slider(context, Control.OpacitySlider, $"{Math.Round(_config.BackgroundOpacity * 100)}%");

        Row(context, 3, "Show track info");
        Toggle(context, Control.Header, _config.ShowHeader);

        Row(context, 4, "Show progress bar");
        Toggle(context, Control.ProgressBar, _config.ShowProgressBar);

        Row(context, 5, "Lyric size");
        Stepper(context, _config.ActiveFontSize.ToString("0"), Control.FontActiveDown, Control.FontActiveUp);

        Row(context, 6, "Surrounding lines size");
        Stepper(context, _config.InactiveFontSize.ToString("0"), Control.FontInactiveDown, Control.FontInactiveUp);
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

    private static float RowTop(int index) => RowsTopDip + (index * RowHeightDip);

    private void Row(ID2D1DeviceContext context, int index, string label) =>
        Label(context, label, ContentLeft, RowTop(index), ContentWidth - 200f, RowHeightDip, 13.5f, FontWeight.Normal, ColorText);

    private void Hint(ID2D1DeviceContext context, int index, string text) =>
        Label(context, text, ContentLeft, RowTop(index) + 12f, ContentWidth, 40f, 12f, FontWeight.Normal, ColorTextFaint);

    private void Toggle(ID2D1DeviceContext context, Control id, bool value)
    {
        const float width = 46f;
        const float height = 26f;
        const float knob = 20f;

        var left = ContentRight - width;
        var top = RowTop(RowOf(id)) + ((RowHeightDip - height) / 2f);
        var hot = _hover == (int)id;

        FillRound(context, left, top, width, height, height / 2f, value ? ColorAccent : (hot ? ColorControlHot : ColorControl));

        var knobLeft = value ? left + width - knob - 3f : left + 3f;
        FillRound(context, knobLeft, top + 3f, knob, knob, knob / 2f, ColorKnob);

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

        var t = (float)Math.Clamp((_config.BackgroundOpacity - 0.15) / (1.0 - 0.15), 0.0, 1.0);
        FillRound(context, left, top, Math.Max(knob, t * width), trackHeight, trackHeight / 2f, ColorAccent);
        FillRound(context, left + (t * width) - (knob / 2f), centerY - (knob / 2f), knob, knob, knob / 2f,
            _hover == (int)id || _draggingOpacity ? ColorKnobHot : ColorKnob);

        _sliderLeft = left;
        _sliderWidth = width;

        Hit((int)id, R(left - 10f, centerY - 14f, left + width + 10f, centerY + 14f));
    }

    private void Button(ID2D1DeviceContext context, float left, float top, float width, float height, string text, Control id)
    {
        var hot = _hover == (int)id;
        FillRound(context, left, top, width, height, 8f, hot ? ColorControlHot : ColorControl);
        Label(context, text, left, top, width, height, 12.5f, FontWeight.Normal, ColorText, TextAlignment.Center);
        Hit((int)id, R(left, top, left + width, top + height));
    }

    /// <summary>The row each control lives in, so a control can be positioned without a second table.</summary>
    private int RowOf(Control id) => id switch
    {
        Control.ModeSingle or Control.ModeMulti => 0,
        Control.LinesDown or Control.LinesUp => 1,
        Control.OpacitySlider => 2,
        Control.Header => 3,
        Control.ProgressBar => 4,
        Control.FontActiveDown or Control.FontActiveUp => 5,
        Control.FontInactiveDown or Control.FontInactiveUp => 6,
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
