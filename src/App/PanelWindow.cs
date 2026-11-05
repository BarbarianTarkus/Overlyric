using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Overlyric.Interop;
using Overlyric.Overlay;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.App;

/// <summary>
/// Shared plumbing for the two lyric windows: a frameless, fixed-size, Direct2D-drawn panel with a
/// draggable title bar, a close button, hover state and a hit-test list.
/// </summary>
/// <remarks>
/// <para>
/// The settings window predates this and keeps its own copy of the drawing helpers. These two windows
/// were written together and share the parts that would otherwise have been duplicated twice: the
/// composition host lifecycle, the dark title bar, the palette, the text formats and the hit test.
/// </para>
/// <para>
/// Subclasses draw in body coordinates - the origin is below the title bar - and register clickable
/// rectangles in the same space. That means a row can be a hit target without the base class needing
/// to know what a row is.
/// </para>
/// </remarks>
internal abstract class PanelWindow : WindowBase
{
    protected const float TitleHeight = 44f;
    protected const int CloseControl = -1;

    private const int WmSetCursor = 0x0020;
    private const int WmCaptureChanged = 0x0215;
    private const int IdcHand = 32649;

    /// <summary>How close to an edge the pointer has to be, in DIP, to start a resize.</summary>
    private const float ResizeBorderDip = 6f;

    protected const uint ColorWindow = 0xFF0F1116;
    protected const uint ColorTitle = 0xFF0A0C10;
    protected const uint ColorLine = 0xFF1E222B;
    protected const uint ColorText = 0xFFE9EDF5;
    protected const uint ColorTextDim = 0xFF8A93A6;
    protected const uint ColorTextFaint = 0xFF5A6273;
    protected const uint ColorControl = 0xFF1A1F28;
    protected const uint ColorControlHot = 0xFF232A35;
    protected const uint ColorSelected = 0xFF23303F;
    protected const uint ColorAccent = 0xFF7CC4FF;
    protected const uint ColorGood = 0xFF8FD6A0;
    protected const uint ColorWarm = 0xFFE0A458;
    protected const uint ColorWarning = 0xFFE0A458;
    protected const uint ColorKnob = 0xFFD8DEE9;

    private static nint _classBrush;
    private static nint _handCursor;

    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = [];
    private readonly Dictionary<(float Size, FontWeight Weight, TextAlignment Align, ParagraphAlignment Paragraph, WordWrapping Wrap), IDWriteTextFormat> _formats = [];
    private readonly Dictionary<(string Text, float Size, FontWeight Weight), float> _widths = [];
    private readonly List<(int Id, Rect Rect)> _hits = [];

    private CompositionHost? _host;
    private float _dpi = 96f;
    private bool _open;
    private int _hover = -1;
    private int _dragControl;
    private bool _dragging;

    /// <summary>The live size in DIP. It starts at the subclass default and follows the window border.</summary>
    private float _widthDip = 900f;
    private float _heightDip = 560f;

    protected float WidthDip => _widthDip;

    protected float HeightDip => _heightDip;

    /// <summary>The size a panel opens at; the user can then resize it down to the minimums below.</summary>
    protected abstract float DefaultWidthDip { get; }

    protected abstract float DefaultHeightDip { get; }

    /// <summary>The smallest body the layout can still draw without overlapping its own columns.</summary>
    protected virtual float MinWidthDip => 640f;

    protected virtual float MinHeightDip => 380f;

    protected abstract string Title { get; }

    protected abstract void DrawBody(ID2D1DeviceContext context);

    protected override string ClassName => "OverlyricPanelWindow";

    protected override int WindowStyle => WS_POPUP;

    /// <summary>
    /// Topmost because the lyric overlay is: two ordinary windows would leave the panel underneath the
    /// lyrics it was opened from. The panel is only created when the user asks for it.
    /// </summary>
    protected override int WindowExtraStyle =>
        WS_EX_NOREDIRECTIONBITMAP | WS_EX_APPWINDOW | WS_EX_TOPMOST;

    /// <summary>COLORREF is 0x00BBGGRR; #0F1116 is 0x0016110F.</summary>
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

    protected float Dpi => _dpi;

    protected int HoverId => _hover;

    protected bool IsDragging => _dragging;

    /// <summary>Drop the hover highlight before a redraw that moves the rows under the pointer.</summary>
    protected void ClearHover() => _hover = -1;

    protected IDWriteFactory WriteFactory => _host!.WriteFactory!;

    /// <summary>Body size in device-independent pixels, for layouts that fill the panel.</summary>
    protected float BodyWidth => WidthDip;

    protected float BodyHeight => HeightDip;

    /// <summary>The window is the body plus the title bar; body coordinates start below it.</summary>
    private float WindowHeight => HeightDip + TitleHeight;

    public void ShowPanel(nint monitor = 0)
    {
        if (Handle == nint.Zero)
        {
            CreatePanel(monitor);
        }
        else if (!_open)
        {
            // A hidden panel keeps its last position and the size the user left it at; only the
            // first show centres it. The DPI is re-read because the panel may have been hidden
            // while the monitor changed, and the size is clamped so a window dragged onto a
            // smaller screen does not reopen with its edges off screen.
            _dpi = ResolveDpi();
            FitToMonitor(MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST));
            ApplyBounds(center: false);
        }

        _open = true;
        ShowWindow(Handle, SW_SHOW);
        SetForegroundWindow(Handle);
        Render();
    }

    /// <summary>Hide without destroying: reopening is instant and composition resources survive.</summary>
    public void HidePanel()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        _hover = -1;
        _dragging = false;
        _dragControl = 0;

        if (Handle != nint.Zero)
        {
            ShowWindow(Handle, SW_HIDE);
        }
    }

    /// <summary>Redraw after the model behind the window changed.</summary>
    public void Invalidate()
    {
        if (_open)
        {
            Render();
        }
    }

    private void CreatePanel(nint monitor)
    {
        _widthDip = DefaultWidthDip;
        _heightDip = DefaultHeightDip;

        CreateWindow(Title, (int)WidthDip, (int)WindowHeight);

        var dark = 1;
        if (DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
        {
            // Pre-20H1 builds only know the old attribute. If this one is refused too there is no
            // third fallback, and a light title bar is not worth failing the window over.
            _ = DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));
        }

        _dpi = ResolveDpi();
        FitToMonitor(monitor != nint.Zero ? monitor : MonitorUnderCursor());
        ApplyBounds(center: true, monitor);
        EnsureHost();
        OnPanelCreated();
    }

    /// <summary>
    /// Keep a panel inside the monitor's work area. The defaults were chosen for a 1080p desktop at
    /// 100 %; at 200 % the same DIP size can be taller than the screen, and the footer is the part
    /// that would fall off. Clamping in DIP keeps the layout and the window in the same unit.
    /// </summary>
    private void FitToMonitor(nint monitor)
    {
        var target = monitor != nint.Zero
            ? monitor
            : MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);

        if (target == nint.Zero)
        {
            return;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

        if (!GetMonitorInfoW(target, ref info))
        {
            return;
        }

        var scale = _dpi / 96f;
        var maxWidth = MathF.Max(320f, (info.rcWork.Width / scale) - 12f);

        // The body is what the subclass sizes; the title bar is added on top of it, so it has to
        // come out of the work area as well or the footer lands just below the screen.
        var maxHeight = MathF.Max(240f, (info.rcWork.Height / scale) - 12f - TitleHeight);
        var minWidth = MathF.Min(MinWidthDip, maxWidth);
        var minHeight = MathF.Min(MinHeightDip, maxHeight);

        _widthDip = Math.Clamp(_widthDip, minWidth, maxWidth);
        _heightDip = Math.Clamp(_heightDip, minHeight, maxHeight);
    }

    protected virtual void OnPanelCreated()
    {
    }

    private float ResolveDpi()
    {
        var dpi = GetDpiForWindow(Handle);
        return DpiOverride.For(dpi > 0 ? dpi : 96f);
    }

    private int ToPixels(float dip) => (int)MathF.Round(dip * _dpi / 96f);

    private void ApplyBounds(bool center, nint monitor = 0)
    {
        var width = ToPixels(WidthDip);
        var height = ToPixels(WindowHeight);
        var x = 0;
        var y = 0;

        if (center)
        {
            var target = monitor != nint.Zero ? monitor : MonitorUnderCursor();

            if (target == nint.Zero)
            {
                target = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
            }

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

            if (GetMonitorInfoW(target, ref info))
            {
                x = info.rcWork.Left + ((info.rcWork.Width - width) / 2);
                y = info.rcWork.Top + ((info.rcWork.Height - height) / 2);
            }
        }
        else if (GetWindowRect(Handle, out var rect))
        {
            x = rect.Left;
            y = rect.Top;
        }

        SetWindowPos(
            Handle,
            HWND_TOPMOST,
            x,
            y,
            width,
            height,
            SWP_NOACTIVATE | (_open ? SWP_SHOWWINDOW : 0));

        _host?.Resize(width, height, _dpi);
        OnPanelResized();
    }

    /// <summary>
    /// Follow a live resize. The frame has already moved by the time WM_SIZE arrives, so the new
    /// size is read back from the client rectangle; the composition surface follows in the same
    /// message, which is what keeps the panel from showing a stretched frame or a blank strip
    /// while the border is being dragged.
    /// </summary>
    private void OnClientSizeChanged()
    {
        if (Handle == nint.Zero
            || !GetClientRect(Handle, out var client)
            || client.Width <= 0
            || client.Height <= 0)
        {
            return;
        }

        var scale = _dpi / 96f;
        _widthDip = client.Width / scale;
        _heightDip = MathF.Max(0f, (client.Height / scale) - TitleHeight);

        _host?.Resize(client.Width, client.Height, _dpi);
        OnPanelResized();
        Render();
    }

    /// <summary>The monitor under the cursor, which is where the click that opened the panel happened.</summary>
    private static nint MonitorUnderCursor()
    {
        if (GetCursorPos(out var point))
        {
            return MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        }

        return nint.Zero;
    }

    protected virtual void OnPanelResized()
    {
    }

    private void EnsureHost()
    {
        _host ??= CompositionHost.Create(Handle, ToPixels(WidthDip), ToPixels(WindowHeight), _dpi);
    }

    private void RebuildHost()
    {
        ReleaseDeviceResources();
        _host?.Dispose();
        _host = null;

        try
        {
            EnsureHost();
            Render();
        }
        catch (Exception)
        {
            // A lost device leaves the window blank rather than taking the process down.
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
        _widths.Clear();
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
            DrawChrome(context);

            context.Transform = Matrix3x2.CreateTranslation(0f, TitleHeight);
            DrawBody(context);
            context.Transform = Matrix3x2.Identity;

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

    private void DrawChrome(ID2D1DeviceContext context)
    {
        Fill(context, R(0f, 0f, WidthDip, HeightDip + TitleHeight), ColorWindow);
        Fill(context, R(0f, 0f, WidthDip, TitleHeight), ColorTitle);
        Fill(context, R(0f, TitleHeight - 1f, WidthDip, TitleHeight), ColorLine);

        Label(
            context,
            Title,
            22f,
            0f,
            WidthDip - 120f,
            TitleHeight,
            12.5f,
            FontWeight.SemiBold,
            ColorTextDim);

        var close = CloseRect();
        var hot = _hover == CloseControl;

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

        Hit(CloseControl, close);
    }

    private Rect CloseRect() => R(WidthDip - 52f, 6f, WidthDip - 12f, TitleHeight - 6f);

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
                // The minimum is a body size in DIP; the native sizing loop wants window pixels,
                // and a panel's window is its body plus the title bar.
                ApplyMinimumTrackSize(lParam, _dpi, MinWidthDip, MinHeightDip + TitleHeight);
                return IntPtr.Zero;

            case WM_NCHITTEST:
                return HitTest(lParam);

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

            case WM_LBUTTONDOWN:
                OnMouseDown(lParam);
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                OnMouseUp(lParam);
                return IntPtr.Zero;

            case WmCaptureChanged:
                _dragging = false;
                _dragControl = 0;
                return IntPtr.Zero;

            case WmSetCursor:
                return OnSetCursor(hWnd, wParam, lParam);

            case WM_MOUSEWHEEL:
                // The delta lives in the high word of wParam; lParam holds the screen point.
                OnBodyWheel((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                return IntPtr.Zero;

            case WM_KEYDOWN:
                return OnKeyDown((int)wParam.ToInt64());

            case WM_CHAR:
                OnBodyChar((char)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;

            case WM_CLOSE:
                HidePanel();
                return IntPtr.Zero;

            case WM_DPICHANGED:
                // Keep the top-left corner: a resizable window that jumps to the middle of the
                // screen whenever it crosses a monitor boundary is worse than one that does not.
                _dpi = ResolveDpi();
                FitToMonitor(MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST));
                ApplyBounds(center: false);
                Render();
                return IntPtr.Zero;

            case WM_DISPLAYCHANGE:
                _dpi = ResolveDpi();
                FitToMonitor(MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST));
                ApplyBounds(center: true);
                Render();
                return IntPtr.Zero;

            case WM_DESTROY:
                Unregister(hWnd);
                return IntPtr.Zero;
        }

        return DefaultWindowProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr HitTest(nint lParam)
    {
        // The border wins over the title bar and the body: a press there must size the window,
        // not drag it, and Windows turns the returned HT* code into the resize cursor for free.
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

        return new IntPtr(y < TitleHeight && !overClose ? HTCAPTION : HTCLIENT);
    }

    private void OnMouseDown(nint lParam)
    {
        var (x, y) = ToDip(lParam);

        if (TryHit(x, y, out var id))
        {
            if (id == CloseControl)
            {
                HidePanel();
                return;
            }

            Activate(id);
        }

        OnBodyMouseDown(x, y - TitleHeight);
    }

    private void OnMouseUp(nint lParam)
    {
        var (x, y) = ToDip(lParam);
        OnBodyMouseUp(x, y - TitleHeight);

        if (_dragging)
        {
            _dragging = false;
            _dragControl = 0;
            ReleaseCapture();
        }
    }

    private void OnMouseMove(nint lParam)
    {
        var (x, y) = ToDip(lParam);
        OnBodyMouseMove(x, y - TitleHeight);

        if (_dragging)
        {
            return;
        }

        var hover = TryHit(x, y, out var id) ? id : -1;

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

    private IntPtr OnSetCursor(nint hWnd, nint wParam, nint lParam)
    {
        if ((lParam.ToInt64() & 0xFFFF) != HTCLIENT)
        {
            return DefaultWindowProc(hWnd, WmSetCursor, wParam, lParam);
        }

        var (x, y) = ToDipFromScreen();

        if (!TryHit(x, y, out _))
        {
            return DefaultWindowProc(hWnd, WmSetCursor, wParam, lParam);
        }

        _handCursor = _handCursor == nint.Zero
            ? LoadCursorW(nint.Zero, new nint(IdcHand))
            : _handCursor;

        if (_handCursor == nint.Zero)
        {
            return DefaultWindowProc(hWnd, WmSetCursor, wParam, lParam);
        }

        SetCursor(_handCursor);
        return new IntPtr(1);
    }

    private IntPtr OnKeyDown(int virtualKey)
    {
        if (OnBodyKey(virtualKey))
        {
            return IntPtr.Zero;
        }

        if (virtualKey == VK_ESCAPE)
        {
            HidePanel();
        }

        return IntPtr.Zero;
    }

    private bool TryHit(float x, float y, out int id)
    {
        var bodyY = y - TitleHeight;

        foreach (var (hitId, rect) in _hits)
        {
            var testY = hitId == CloseControl ? y : bodyY;

            if (x >= rect.Left && x < rect.Right && testY >= rect.Top && testY < rect.Bottom)
            {
                id = hitId;
                return true;
            }
        }

        id = 0;
        return false;
    }

    private void Activate(int id)
    {
        // A press on a drag target starts the drag before the subclass hears about it, so its own
        // mouse-move handler can simply ask whether it is dragging.
        if (IsDragTarget(id))
        {
            BeginDrag(id);
        }

        OnActivate(id);
    }

    /// <summary>Ids for which a press should capture the mouse and report movement.</summary>
    protected virtual bool IsDragTarget(int id) => false;

    protected void BeginDrag(int id)
    {
        _dragging = true;
        _dragControl = id;
        SetCapture(Handle);
    }

    protected int DragControl => _dragControl;

    protected virtual void OnActivate(int id)
    {
    }

    protected virtual void OnBodyMouseDown(float x, float y)
    {
    }

    protected virtual void OnBodyMouseMove(float x, float y)
    {
    }

    protected virtual void OnBodyMouseUp(float x, float y)
    {
    }

    protected virtual void OnBodyWheel(int notches)
    {
    }

    /// <summary>Return true to consume a key the base would otherwise use for Escape.</summary>
    protected virtual bool OnBodyKey(int virtualKey) => false;

    /// <summary>
    /// A character Windows translated from the keyboard, dead keys and all. The complete-lyrics
    /// window's search field uses this; the base ignores it.
    /// </summary>
    protected virtual void OnBodyChar(char character)
    {
    }

    // ---- drawing helpers -------------------------------------------------------------------

    protected void Hit(int id, Rect rect) => _hits.Add((id, rect));

    protected static Rect R(float left, float top, float right, float bottom) =>
        new(RectangleF.FromLTRB(left, top, right, bottom));

    protected void Fill(ID2D1DeviceContext context, Rect rect, uint argb) =>
        context.FillRectangle(rect, GetBrush(context, argb));

    protected void FillRound(
        ID2D1DeviceContext context,
        float left,
        float top,
        float width,
        float height,
        float radius,
        uint argb) =>
        context.FillRoundedRectangle(
            new RoundedRectangle(new RectangleF(left, top, width, height), radius, radius),
            GetBrush(context, argb));

    protected void Label(
        ID2D1DeviceContext context,
        string text,
        float left,
        float top,
        float width,
        float height,
        float size,
        FontWeight weight,
        uint argb,
        TextAlignment align = TextAlignment.Leading,
        ParagraphAlignment paragraph = ParagraphAlignment.Center,
        WordWrapping wrap = WordWrapping.NoWrap)
    {
        if (width <= 0f || height <= 0f)
        {
            return;
        }

        var format = GetFormat(size, weight, align, paragraph, wrap);
        var layout = WriteFactory.CreateTextLayout(text, format, width, height);
        context.DrawTextLayout(new Vector2(left, top), layout, GetBrush(context, argb));
        layout.Dispose();
    }

    /// <summary>A small rounded chip. Returns its left edge so callers can lay out from the right.</summary>
    protected float Badge(
        ID2D1DeviceContext context,
        string text,
        float right,
        float centerY,
        uint foreground,
        uint background,
        float height = 20f,
        float size = 10.5f)
    {
        var width = Measure(text, size, FontWeight.SemiBold) + 16f;
        var left = right - width;
        var top = centerY - (height / 2f);

        FillRound(context, left, top, width, height, height / 2f, background);
        Label(
            context,
            text,
            left,
            top,
            width,
            height,
            size,
            FontWeight.SemiBold,
            foreground,
            TextAlignment.Center);

        return left;
    }

    protected float Measure(string text, float size, FontWeight weight)
    {
        var key = (text, size, weight);

        if (_widths.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var format = GetFormat(size, weight, TextAlignment.Leading, ParagraphAlignment.Near, WordWrapping.NoWrap);
        var layout = WriteFactory.CreateTextLayout(text, format, 4096f, 100f);
        var width = layout.Metrics.Width;
        layout.Dispose();

        _widths[key] = width;
        return width;
    }

    /// <summary>Measures the wrapped height of a block of text; used by the viewer's line layout.</summary>
    protected float MeasureHeight(string text, float width, float size, FontWeight weight)
    {
        var format = GetFormat(size, weight, TextAlignment.Leading, ParagraphAlignment.Near, WordWrapping.Wrap);
        var layout = WriteFactory.CreateTextLayout(text, format, Math.Max(16f, width), 10000f);
        var height = layout.Metrics.Height;
        layout.Dispose();
        return height;
    }

    protected void Scrollbar(
        ID2D1DeviceContext context,
        float left,
        float top,
        float height,
        float viewport,
        float content,
        float scroll,
        uint thumbColor)
    {
        if (content <= viewport + 0.5f || height <= 0f)
        {
            return;
        }

        var thumbHeight = MathF.Max(26f, height * (viewport / content));
        var travel = height - thumbHeight;
        var maxScroll = content - viewport;
        var t = maxScroll <= 0f ? 0f : Math.Clamp(scroll / maxScroll, 0f, 1f);
        var thumbTop = top + (travel * t);

        FillRound(context, left, top, 4f, height, 2f, 0x22FFFFFF);
        FillRound(context, left, thumbTop, 4f, thumbHeight, 2f, thumbColor);
    }

    protected IDWriteTextFormat GetFormat(
        float size,
        FontWeight weight,
        TextAlignment align,
        ParagraphAlignment paragraph,
        WordWrapping wrap)
    {
        var key = (size, weight, align, paragraph, wrap);

        if (_formats.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var format = WriteFactory.CreateTextFormat(
            "Segoe UI Variable Display",
            null!,
            weight,
            FontStyle.Normal,
            FontStretch.Normal,
            size);

        format.TextAlignment = align;
        format.ParagraphAlignment = paragraph;
        format.WordWrapping = wrap;

        _formats[key] = format;
        return format;
    }

    protected ID2D1SolidColorBrush GetBrush(ID2D1DeviceContext context, uint argb)
    {
        if (_brushes.TryGetValue(argb, out var cached))
        {
            return cached;
        }

        var brush = context.CreateSolidColorBrush(OverlayTheme.ToColor4(argb));
        _brushes[argb] = brush;
        return brush;
    }

    protected (float X, float Y) ToDip(nint lParam)
    {
        var raw = lParam.ToInt64();
        var scale = _dpi / 96f;

        return (
            (short)(raw & 0xFFFF) / scale,
            (short)((raw >> 16) & 0xFFFF) / scale);
    }

    private (float X, float Y) ToDipFromScreen()
    {
        if (!GetCursorPos(out var point) || !ScreenToClient(Handle, ref point))
        {
            return (-1f, -1f);
        }

        var scale = _dpi / 96f;
        return (point.X / scale, point.Y / scale);
    }

    public override void Dispose()
    {
        ReleaseDeviceResources();
        _host?.Dispose();
        _host = null;

        base.Dispose();
    }
}
