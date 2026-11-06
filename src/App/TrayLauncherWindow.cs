using System.Drawing;
using System.Runtime.InteropServices;
using Overlyric.Interop;
using Overlyric.Overlay;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.App;

/// <summary>Everything the launcher prints: the track, the overlay switches and the version.</summary>
internal sealed record TrayLauncherModel(
    bool HasTrack,
    string Track,
    string Artist,
    string Source,
    bool OverlayOn,
    bool Locked,
    bool Stage,
    string Version);

/// <summary>
/// The window hub: a frameless flyout anchored above the notification-area icon. Every window is
/// opened from here, and the four title bars repeat this list in the same order.
/// </summary>
/// <remarks>
/// <para>
/// The launcher reads its state through <see cref="TrayLauncherModel"/> on every show, never from a
/// copy taken at construction: the track, the overlay switches and the visible windows can all change
/// while the application is running. It is created lazily and hidden on dismiss, like the panels.
/// </para>
/// <para>
/// It is a tool window (no taskbar entry) and topmost (the panels are topmost too). Losing activation
/// hides it, which is what makes an outside click dismiss it without a second window or a hook.
/// </para>
/// </remarks>
internal sealed class TrayLauncherWindow : WindowBase
{
    private const float WidthDip = 340f;
    private const float HeightDip = 552f;

    private const float RowsTop = 156f;
    private const float RowHeight = 46f;
    private const float RowsLeft = 8f;
    private const float RowsWidth = 324f;

    private const int RowControl = 10;
    private const int OverlayControl = 20;
    private const int LockedControl = 21;
    private const int StageControl = 22;
    private const int ExitControl = 30;

    private const uint ColorWindow = 0xFF0F1116;
    private const uint ColorBorder = 0xFF262C36;
    private const uint ColorLine = 0xFF1E222B;
    private const uint ColorText = 0xFFE9EDF5;
    private const uint ColorTextDim = 0xFF8A93A6;
    private const uint ColorTextFaint = 0xFF5A6273;
    private const uint ColorAccent = 0xFF7CC4FF;
    private const uint ColorCard = 0xFF141820;
    private const uint ColorCardBorder = 0xFF1B2230;
    private const uint ColorRowHover = 0xFF1A2029;
    private const uint ColorRowIcon = 0xFF171C24;
    private const uint ColorRowIconBorder = 0xFF232B36;
    private const uint ColorChip = 0xFF18242F;
    private const uint ColorOpenPill = 0xFF16212B;
    private const uint ColorOpenBorder = 0xFF23303F;
    private const uint ColorToggleTrack = 0xFF1A1F28;
    private const uint ColorKnob = 0xFFD8DEE9;
    private const uint ColorSwitchText = 0xFFC7CEDA;
    private const uint ColorChevron = 0xFF4A5466;

    private readonly Func<TrayLauncherModel> _modelProvider;
    private readonly WindowNavigation _navigation;
    private readonly Action _toggleOverlay;
    private readonly Action _toggleLocked;
    private readonly Action _toggleStage;
    private readonly Action _exit;
    private readonly Func<RECT?> _anchor;

    private readonly List<(int Id, Rect Rect)> _hits = [];

    /// <summary>Brushes and formats for this host; created on first draw, dropped with the device.</summary>
    private TextCache? _text;

    private CompositionHost? _host;
    private TrayLauncherModel _model;
    private float _dpi = 96f;
    private bool _open;
    private int _hover = -1;

    public TrayLauncherWindow(
        Func<TrayLauncherModel> modelProvider,
        WindowNavigation navigation,
        Action toggleOverlay,
        Action toggleLocked,
        Action toggleStage,
        Action exit,
        Func<RECT?> anchor)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(toggleOverlay);
        ArgumentNullException.ThrowIfNull(toggleLocked);
        ArgumentNullException.ThrowIfNull(toggleStage);
        ArgumentNullException.ThrowIfNull(exit);
        ArgumentNullException.ThrowIfNull(anchor);

        _modelProvider = modelProvider;
        _navigation = navigation;
        _toggleOverlay = toggleOverlay;
        _toggleLocked = toggleLocked;
        _toggleStage = toggleStage;
        _exit = exit;
        _anchor = anchor;
        _model = modelProvider();
    }

    protected override string ClassName => "OverlyricTrayLauncher";

    protected override int WindowStyle => WS_POPUP;

    protected override int WindowExtraStyle =>
        WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP;

    public bool IsOpen => _open;

    public void ShowLauncher()
    {
        _model = _modelProvider();

        if (Handle == nint.Zero)
        {
            CreateWindow("Overlyric", ToPixels(WidthDip), ToPixels(HeightDip));
        }

        var dpi = GetDpiForWindow(Handle);
        _dpi = DpiOverride.For(dpi > 0 ? dpi : 96f);

        ResizeHost();
        Place();

        _open = true;
        _hover = -1;
        ShowWindow(Handle, SW_SHOW);
        SetForegroundWindow(Handle);
        Render();
    }

    public void HideLauncher()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        _hover = -1;

        if (Handle != nint.Zero)
        {
            ShowWindow(Handle, SW_HIDE);
        }
    }

    private int ToPixels(float dip) => (int)MathF.Round(dip * _dpi / 96f);

    private void ResizeHost()
    {
        var width = ToPixels(WidthDip);
        var height = ToPixels(HeightDip);

        if (_host is null)
        {
            _host = CompositionHost.Create(Handle, width, height, _dpi);
            return;
        }

        _host.Resize(width, height, _dpi);
    }

    /// <summary>
    /// Anchor to the notification area when the shell can name its rectangle, and to the work-area
    /// corner otherwise (a hidden overflow icon or an Explorer restart is the normal case).
    /// </summary>
    private void Place()
    {
        var width = ToPixels(WidthDip);
        var height = ToPixels(HeightDip);
        var scale = _dpi / 96f;
        var anchor = _anchor();

        nint monitor;

        if (anchor is { } rect)
        {
            monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
        }
        else if (GetCursorPos(out var cursor))
        {
            monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        }
        else
        {
            monitor = nint.Zero;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

        if (!GetMonitorInfoW(monitor, ref info))
        {
            return;
        }

        var right = info.rcWork.Right - (int)MathF.Round(12f * scale);
        var bottom = (anchor?.Top ?? info.rcWork.Bottom) - (int)MathF.Round(8f * scale);

        var x = Math.Max(info.rcWork.Left, right - width);
        var y = Math.Max(info.rcWork.Top + (int)MathF.Round(8f * scale), bottom - height);

        SetWindowPos(Handle, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);
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

    private void RebuildHost()
    {
        ReleaseDeviceResources();
        _host?.Dispose();
        _host = null;

        try
        {
            ResizeHost();
            Render();
        }
        catch (Exception)
        {
            // A lost device leaves the flyout blank rather than taking the process down.
        }
    }

    private void Draw(ID2D1DeviceContext context)
    {
        _hits.Clear();

        // Two rounded rectangles make the 1 DIP border without needing a stroke helper.
        FillRound(context, 0f, 0f, WidthDip, HeightDip, 12f, ColorBorder);
        FillRound(context, 1f, 1f, WidthDip - 2f, HeightDip - 2f, 11f, ColorWindow);

        DrawHeader(context);
        DrawNowPlaying(context);
        DrawWindows(context);
        DrawSwitches(context);
        DrawFooter(context);
    }

    private void DrawHeader(ID2D1DeviceContext context)
    {
        FillRound(context, 16f, 16f, 26f, 26f, 7f, ColorBorder);
        FillRound(context, 17f, 17f, 24f, 24f, 6f, 0xFF18212B);
        FillRound(context, 22f, 24f, 2f, 10f, 1f, ColorAccent);
        FillRound(context, 26f, 22f, 2f, 14f, 1f, ColorAccent);
        FillRound(context, 30f, 24.5f, 2f, 9f, 1f, ColorAccent);

        Label(context, "Overlyric", 52f, 13f, 220f, 20f, 14f, FontWeight.SemiBold, ColorText);
        Label(
            context,
            string.Concat(_model.Version, "  ·  notification area"),
            52f,
            33f,
            240f,
            14f,
            10.5f,
            FontWeight.Normal,
            ColorTextFaint);
    }

    private void DrawNowPlaying(ID2D1DeviceContext context)
    {
        FillRound(context, 16f, 60f, 308f, 64f, 8f, ColorCardBorder);
        FillRound(context, 17f, 61f, 306f, 62f, 7f, ColorCard);

        Label(context, "NOW PLAYING", 28f, 68f, 120f, 12f, 9.5f, FontWeight.SemiBold, ColorAccent);

        var track = _model.HasTrack ? _model.Track : "Nothing playing";
        var artist = _model.HasTrack ? _model.Artist : "Start playback in any player";

        Label(
            context,
            Ellipsize(track, 196f, 13f, FontWeight.SemiBold),
            28f,
            86f,
            196f,
            16f,
            13f,
            FontWeight.SemiBold,
            ColorText);
        Label(
            context,
            Ellipsize(artist, 196f, 10.5f, FontWeight.Normal),
            28f,
            104f,
            196f,
            14f,
            10.5f,
            FontWeight.Normal,
            ColorTextDim);

        if (!_model.HasTrack || _model.Source.Length == 0)
        {
            return;
        }

        var source = _model.Source.ToUpperInvariant();
        var width = Math.Clamp(Measure(source, 9f, FontWeight.SemiBold) + 18f, 44f, 84f);
        var left = 308f - width;

        FillRound(context, left, 68f, width, 18f, 9f, ColorChip);
        Label(context, source, left, 68f, width, 18f, 9f, FontWeight.SemiBold, ColorAccent, TextAlignment.Center);
    }

    private void DrawWindows(ID2D1DeviceContext context)
    {
        Label(context, "WINDOWS", 20f, 138f, 160f, 14f, 10.5f, FontWeight.SemiBold, ColorTextFaint);

        var destinations = WindowMenu.Destinations;

        for (var i = 0; i < destinations.Count; i++)
        {
            var destination = destinations[i];
            var top = RowsTop + (i * RowHeight);
            var id = RowControl + i;
            var hot = _hover == id;

            if (hot)
            {
                FillRound(context, RowsLeft, top, RowsWidth, RowHeight - 2f, 8f, ColorRowHover);
            }

            DrawRowIcon(context, destination, 20f, top + 9f, hot);

            Label(context, WindowMenu.Label(destination), 56f, top + 5f, 200f, 16f, 13f, FontWeight.SemiBold, ColorText);
            Label(context, WindowMenu.Hint(destination), 56f, top + 23f, 200f, 13f, 10.5f, FontWeight.Normal, ColorTextFaint);

            if (_navigation.IsOpen(destination))
            {
                FillRound(context, 280f, top + 14f, 44f, 16f, 8f, ColorOpenBorder);
                FillRound(context, 281f, top + 15f, 42f, 14f, 7f, ColorOpenPill);
                Label(context, "OPEN", 280f, top + 14f, 44f, 16f, 8.5f, FontWeight.SemiBold, ColorAccent, TextAlignment.Center);
            }
            else
            {
                Label(context, "\u203A", 300f, top + 11f, 16f, 18f, 14f, FontWeight.Normal, ColorChevron);
            }

            Hit(id, RowRect(top));
        }
    }

    private void DrawRowIcon(ID2D1DeviceContext context, WindowDestination destination, float left, float top, bool hot)
    {
        FillRound(context, left, top, 26f, 26f, 7f, ColorRowIconBorder);
        FillRound(context, left + 1f, top + 1f, 24f, 24f, 6f, ColorRowIcon);

        var color = hot ? ColorAccent : ColorTextDim;
        var x = left + 7f;

        switch (destination)
        {
            case WindowDestination.Lyrics:
                FillRound(context, x, top + 8f, 12f, 2f, 1f, color);
                FillRound(context, x, top + 12f, 8f, 2f, 1f, color);
                FillRound(context, x, top + 16f, 12f, 2f, 1f, color);
                break;

            case WindowDestination.Versions:
                FillRound(context, x, top + 8f, 12f, 6f, 1.5f, color);
                FillRound(context, x + 2f, top + 15f, 10f, 6f, 1.5f, color);
                break;

            case WindowDestination.Diagnostics:
                FillRound(context, x, top + 13f, 2f, 6f, 1f, color);
                FillRound(context, x + 4f, top + 8f, 2f, 11f, 1f, color);
                FillRound(context, x + 8f, top + 11f, 2f, 8f, 1f, color);
                FillRound(context, x + 12f, top + 15f, 2f, 4f, 1f, color);
                break;

            default:
                FillRound(context, x, top + 8f, 12f, 1.5f, 1f, color);
                FillRound(context, x, top + 13f, 12f, 1.5f, 1f, color);
                FillRound(context, x, top + 18f, 12f, 1.5f, 1f, color);
                FillRound(context, x + 8f, top + 6f, 4f, 4f, 1f, color);
                FillRound(context, x + 3f, top + 11f, 4f, 4f, 1f, color);
                FillRound(context, x + 10f, top + 16f, 4f, 4f, 1f, color);
                break;
        }
    }

    private void DrawSwitches(ID2D1DeviceContext context)
    {
        Fill(context, 16f, 352f, 308f, 1f, ColorLine);
        Label(context, "OVERLAY", 20f, 366f, 160f, 14f, 10.5f, FontWeight.SemiBold, ColorTextFaint);

        Switch(context, OverlayControl, "Show overlay", 388f, _model.OverlayOn);
        Switch(context, LockedControl, "Locked (click-through)", 422f, _model.Locked);
        Switch(context, StageControl, "Stage mode", 456f, _model.Stage);
    }

    private void Switch(ID2D1DeviceContext context, int id, string text, float top, bool value)
    {
        var hot = _hover == id;

        Label(context, text, 20f, top + 7f, 230f, 16f, 12.5f, FontWeight.Normal, hot ? ColorText : ColorSwitchText);

        FillRound(context, 270f, top + 2f, 46f, 26f, 13f, value ? ColorAccent : (hot ? ColorRowHover : ColorToggleTrack));
        FillRound(context, value ? 293f : 273f, top + 5f, 20f, 20f, 10f, ColorKnob);

        Hit(id, R(12f, top, 328f, top + 30f));
    }

    private void DrawFooter(ID2D1DeviceContext context)
    {
        Fill(context, 16f, 500f, 308f, 1f, ColorLine);

        var hot = _hover == ExitControl;

        if (hot)
        {
            FillRound(context, RowsLeft, 506f, RowsWidth, 38f, 8f, ColorRowHover);
        }

        Label(context, "Exit Overlyric", 20f, 514f, 200f, 18f, 12.5f, FontWeight.Normal, hot ? ColorText : ColorTextDim);
        Label(context, "\u23FB", 298f, 512f, 20f, 20f, 13f, FontWeight.Normal, ColorTextFaint, TextAlignment.Center);

        Hit(ExitControl, R(8f, 506f, 332f, 544f));
    }

    private static Rect RowRect(float top) => R(RowsLeft, top, RowsLeft + RowsWidth, top + RowHeight - 2f);

    private static Rect R(float left, float top, float right, float bottom) =>
        new(RectangleF.FromLTRB(left, top, right, bottom));

    private void Hit(int id, Rect rect) => _hits.Add((id, rect));

    private int HitTest(float x, float y)
    {
        foreach (var (id, rect) in _hits)
        {
            if (x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom)
            {
                return id;
            }
        }

        return -1;
    }

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

            case WM_ACTIVATE:
                if ((int)wParam == WA_INACTIVE)
                {
                    HideLauncher();
                }

                return IntPtr.Zero;

            case WM_KEYDOWN:
                if ((int)wParam == VK_ESCAPE)
                {
                    HideLauncher();
                }

                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                OnClick(lParam);
                return IntPtr.Zero;

            case WM_MOUSEMOVE:
                OnMove(lParam);
                return IntPtr.Zero;

            case WM_MOUSELEAVE:
                if (_hover != -1)
                {
                    _hover = -1;
                    Render();
                }

                return IntPtr.Zero;

            case WM_DPICHANGED:
                var changedDpi = GetDpiForWindow(hWnd);
                _dpi = DpiOverride.For(changedDpi > 0 ? changedDpi : _dpi);
                ResizeHost();
                Place();
                Render();
                return IntPtr.Zero;

            case WM_CLOSE:
                HideLauncher();
                return IntPtr.Zero;

            case WM_DESTROY:
                Unregister(hWnd);
                return IntPtr.Zero;
        }

        return DefaultWindowProc(hWnd, msg, wParam, lParam);
    }

    private void OnClick(nint lParam)
    {
        var (x, y) = ToDip(lParam);
        var id = HitTest(x, y);

        if (id >= RowControl && id < RowControl + WindowMenu.Destinations.Count)
        {
            var destination = WindowMenu.Destinations[id - RowControl];
            HideLauncher();
            _navigation.Open(destination);
            return;
        }

        switch (id)
        {
            case OverlayControl:
                _toggleOverlay();
                break;

            case LockedControl:
                _toggleLocked();
                break;

            case StageControl:
                _toggleStage();
                break;

            case ExitControl:
                HideLauncher();
                _exit();
                return;

            default:
                return;
        }

        _model = _modelProvider();
        Render();
    }

    private void OnMove(nint lParam)
    {
        var (x, y) = ToDip(lParam);
        var hover = HitTest(x, y);

        if (hover != _hover)
        {
            _hover = hover;
            Render();
        }

        var track = new TRACKMOUSEEVENT
        {
            cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = TME_LEAVE,
            hwndTrack = Handle
        };

        TrackMouseEvent(ref track);
    }

    private (float X, float Y) ToDip(nint lParam)
    {
        var raw = lParam.ToInt64();
        var scale = _dpi / 96f;

        return (
            (short)(raw & 0xFFFF) / scale,
            (short)((raw >> 16) & 0xFFFF) / scale);
    }

    private TextCache Text => _text ??= new TextCache(_host!.WriteFactory!);

    private void Fill(ID2D1DeviceContext context, float left, float top, float width, float height, uint argb) =>
        Text.Fill(context, new Rect(RectangleF.FromLTRB(left, top, left + width, top + height)), argb);

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

    private string Ellipsize(string text, float width, float size, FontWeight weight)
    {
        if (text.Length == 0 || Measure(text, size, weight) <= width)
        {
            return text;
        }

        var cut = text.Length;

        while (cut > 1 && Measure(text[..cut] + "\u2026", size, weight) > width)
        {
            cut--;
        }

        return text[..cut] + "\u2026";
    }

    private float Measure(string text, float size, FontWeight weight) =>
        Text.Measure(text, size, weight);

    private void ReleaseDeviceResources()
    {
        _text?.Dispose();
        _text = null;
    }

    public override void Dispose()
    {
        ReleaseDeviceResources();
        _host?.Dispose();
        _host = null;

        base.Dispose();
    }
}