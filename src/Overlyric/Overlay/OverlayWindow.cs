using System.Runtime.InteropServices;
using Overlyric.Interop;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.Overlay;

/// <summary>
/// The lyric overlay window.
/// <para>
/// It is a topmost, non-activating tool window with <c>WS_EX_NOREDIRECTIONBITMAP</c>: Windows keeps
/// no redirection surface for it and DirectComposition supplies the pixels, which is both the
/// leanest and the most fluid way to host a per-pixel-transparent overlay. It never takes focus,
/// so it stays pinned above borderless-windowed games and full-screen video without stealing input.
/// </para>
/// </summary>
internal sealed class OverlayWindow : WindowBase
{
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int WmSetCursor = 0x0020;
    private const int WmCaptureChanged = 0x0215;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcMouseLeave = 0x02A2;
    private const uint TmeNonClient = 0x00000010;
    private const uint SwpFramechanged = 0x0020;
    private const uint SwpNoZOrder = 0x0004;

    /// <summary>Identifier of the short timer that hides the controls after the pointer leaves.</summary>
    private const ulong ControlsTimerId = 2;

    /// <summary>
    /// Long enough to cross the gap between the lyrics and the bar, short enough that the bar is
    /// gone as soon as the mouse has moved on.
    /// </summary>
    private const uint ControlsHideDelayMs = 450;

    private const int IdcHand = 32649;

    private const float SingleLineWidthDip = 1200f;
    private const float SingleLineHeightDip = 200f;
    private const float SingleLineWithNextWidthDip = 1200f;
    private const float SingleLineWithNextHeightDip = 250f;
    private const float MultiLineWidthDip = 1040f;
    private const float MultiLineHeightDip = 430f;

    private static nint _handCursor;

    private OverlayTheme _theme;

    private CompositionHost? _host;
    private LyricsRenderer? _renderer;
    private OverlayVisual _visual = new();
    private OverlayMode _mode = OverlayMode.SingleLine;
    private OverlayAnchor _anchor = OverlayAnchor.BottomCenter;
    private int _offsetX;
    private int _offsetY;
    private nint _monitor;
    private float _dpi = 96f;
    private bool _locked = true;
    private bool _visible;
    private bool _showControls;
    private OverlayControl? _hoverControl;
    private OverlayControl? _pressedControl;

    public OverlayWindow(OverlayTheme theme)
    {
        _theme = theme;
    }

    protected override string ClassName => "OverlyricOverlayWindow";

    protected override int WindowStyle => WS_POPUP | WS_CLIPSIBLINGS;

    protected override int WindowExtraStyle =>
        WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP | WS_EX_TRANSPARENT | WS_EX_LAYERED;

    /// <summary>Raised when the user scrolls the wheel while the overlay is unlocked.</summary>
    public event Action<int>? OpacityScrolled;

    /// <summary>Raised after the user finishes dragging the overlay.</summary>
    public event Action<int, int>? Moved;

    /// <summary>Raised when the user presses the Settings button on the control bar.</summary>
    public event Action? SettingsRequested;

    /// <summary>Raised with +/-250 ms when the user nudges the timing from the bar.</summary>
    public event Action<int>? OffsetStepRequested;

    /// <summary>Raised when the user resets the timing from the bar.</summary>
    public event Action? OffsetResetRequested;

    /// <summary>Raised when the user asks for the complete lyric sheet.</summary>
    public event Action? ViewLyricsRequested;

    /// <summary>Raised when the user asks to choose another lyric version.</summary>
    public event Action? ChooseLyricsRequested;

    /// <summary>Raised when the user cycles the layout from the bar. The host owns the mode.</summary>
    public event Action? LayoutRequested;

    /// <summary>Raised when the user locks the overlay from the bar.</summary>
    public event Action? LockRequested;

    public bool IsVisible => _visible;

    public bool IsLocked => _locked;

    /// <summary>True while the hover bar is on screen; exposed for probes and for the host's tooltip.</summary>
    public bool IsShowingControls => _showControls;

    public void Create(OverlayMode mode, OverlayAnchor anchor, int offsetX, int offsetY, nint monitor)
    {
        _mode = mode;
        _anchor = anchor;
        _offsetX = offsetX;
        _offsetY = offsetY;
        _monitor = monitor;

        var (dipWidth, dipHeight) = DipSizeFor(mode);
        CreateWindow("Overlyric", (int)dipWidth, (int)dipHeight);

        ApplyLayeredHitTesting();

        _dpi = ResolveDpi();
        ApplyBounds();
        EnsureHost();
    }

    /// <summary>
    /// Finish the click-through setup that Windows actually honours.
    /// <para>
    /// <c>WS_EX_TRANSPARENT</c> is not enough on its own. The documentation for layered windows is
    /// explicit: hit testing ignores the window's shape and passes the mouse to what is underneath
    /// only when the window <em>is</em> layered. Without that, a locked overlay still answers for its
    /// own rectangle and eats every click aimed at the application behind it - which is exactly how
    /// "it is locked and still not transparent" happens. Measured with <c>WindowFromPoint</c> over
    /// the overlay's centre: without this call the overlay is the window that answers; with it, the
    /// window below is.
    /// </para>
    /// <para>
    /// The alpha stays at 255, so the layered path never dims what DirectComposition draws; the
    /// overlay's own opacity is a property of the backdrop it paints, not of the window.
    /// </para>
    /// </summary>
    private void ApplyLayeredHitTesting()
    {
        if (SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA))
        {
            return;
        }

        // A layered window with no attributes draws nothing at all, so give the style back rather
        // than trade a visible overlay for an invisible click-through one.
        var style = GetWindowLongPtrW(Handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtrW(Handle, GWL_EXSTYLE, new nint(style & ~(long)WS_EX_LAYERED));
    }

    /// <summary>Swap between the slim single-line pill and the multi-line block.</summary>
    public void SetMode(OverlayMode mode)
    {
        if (_mode == mode)
        {
            return;
        }

        _mode = mode;
        ApplyBounds();
        RecreateHost();
    }

    /// <summary>
    /// Adopt a new theme in place. Rebuilding the window for every opacity step made the overlay
    /// flash away under the settings slider, so the theme is handed to the live renderer instead.
    /// </summary>
    public void SetTheme(OverlayTheme theme)
    {
        _theme = theme;
        _renderer?.SetTheme(theme);
        Render();
    }

    public void SetLocked(bool locked)
    {
        if (_locked == locked)
        {
            return;
        }

        _locked = locked;

        if (locked)
        {
            // Controls belong to the adjustment mode: a locked overlay must stay invisible to the
            // mouse, including the bar that would otherwise be its only interactive surface.
            HideControls();
        }

        var style = GetWindowLongPtrW(Handle, GWL_EXSTYLE).ToInt64();
        if (locked)
        {
            style |= WS_EX_TRANSPARENT;
        }
        else
        {
            style &= ~(long)WS_EX_TRANSPARENT;
        }

        SetWindowLongPtrW(Handle, GWL_EXSTYLE, new nint(style));

        // Extended style changes only take effect once the frame is recomputed.
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SwpFramechanged | SWP_NOMOVE | SWP_NOSIZE | SwpNoZOrder | SWP_NOACTIVATE);
    }

    public void SetVisible(bool visible)
    {
        if (_visible == visible)
        {
            return;
        }

        _visible = visible;

        if (!visible)
        {
            HideControls();
        }

        ShowWindow(Handle, visible ? SW_SHOWNA : SW_HIDE);
    }

    public void SetVisual(OverlayVisual visual) => _visual = visual;

    /// <summary>Re-assert topmost ordering; some full-screen applications displace other windows.</summary>
    public void ReassertTopmost() =>
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public void SetPlacement(OverlayAnchor anchor, int offsetX, int offsetY, nint monitor)
    {
        _anchor = anchor;
        _offsetX = offsetX;
        _offsetY = offsetY;
        _monitor = monitor;
        ApplyBounds();
    }

    public void Render()
    {
        if (_host is null || _renderer is null)
        {
            return;
        }

        try
        {
            var context = _host.BeginDraw();
            _renderer.Draw(context, _host.DipSize, _showControls
                ? _visual with { ShowControls = true, HoveredControl = _hoverControl }
                : _visual with { ShowControls = false, HoveredControl = null });

            var presented = _host.EndDraw();

            if (!presented)
            {
                RecreateHost();
            }
        }
        catch (Exception)
        {
            // Almost always device loss (driver reset, monitor hot-plug): rebuild and carry on.
            RecreateHost();
        }
    }

    protected override IntPtr WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                return HitTest(lParam);

            case WM_PAINT:
                BeginPaint(hWnd, out var paint);
                EndPaint(hWnd, ref paint);
                return IntPtr.Zero;

            case WM_MOUSEMOVE:
                OnPointerMove(lParam);
                return IntPtr.Zero;

            // The lyrics themselves are HTCAPTION so they drag the window, which means the pointer
            // over them is a non-client area: its movement and leave arrive as NC messages. Both
            // paths have to feed the same hover state or the bar would never appear over the words.
            case WmNcMouseMove:
                OnNonClientPointerMove(lParam);
                return IntPtr.Zero;

            case WM_MOUSELEAVE:
            case WmNcMouseLeave:
                OnPointerLeave();
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                if (OnPointerDown(lParam))
                {
                    return IntPtr.Zero;
                }

                break;

            case WM_LBUTTONUP:
                if (OnPointerUp(lParam))
                {
                    return IntPtr.Zero;
                }

                break;

            case WmCaptureChanged:
                _pressedControl = null;
                return IntPtr.Zero;

            case WmSetCursor:
                return OnSetCursor(hWnd, wParam, lParam);

            case WM_MOUSEWHEEL:
                if (!_locked)
                {
                    // The delta is the high word of wParam; lParam carries the screen point.
                    OpacityScrolled?.Invoke((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                }

                return IntPtr.Zero;

            case WM_TIMER when wParam == (nint)ControlsTimerId:
                HideControlsAfterDelay();
                return IntPtr.Zero;

            case WmExitSizeMove:
                NotifyMoved();
                return IntPtr.Zero;

            case WM_DISPLAYCHANGE:
                _dpi = ResolveDpi();
                ApplyBounds();
                RecreateHost();
                return IntPtr.Zero;

            case WM_DPICHANGED:
                _dpi = (wParam.ToInt64() & 0xFFFF);
                if (_dpi <= 0)
                {
                    _dpi = 96f;
                }

                ApplyBounds();
                RecreateHost();
                return IntPtr.Zero;

            case WM_DESTROY:
                Unregister(hWnd);
                return IntPtr.Zero;

            default:
                return DefaultWindowProc(hWnd, msg, wParam, lParam);
        }

        return DefaultWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Hit testing is the whole trick: the lyrics drag the window, the controls are a normal client
    /// area, and everything else lets the click through to the application behind. When the bar is
    /// visible its gap to the lyrics is included in the client area too, so moving the pointer from
    /// one to the other never falls through and makes the bar disappear on the way there.
    /// </summary>
    private IntPtr HitTest(nint lParam)
    {
        if (_locked)
        {
            return new IntPtr(HTTRANSPARENT);
        }

        if (!TryScreenPointToDip(lParam, out var x, out var y))
        {
            return new IntPtr(HTTRANSPARENT);
        }

        if (_showControls && (ControlAt(x, y) is not null || InControlBridge(x, y)))
        {
            return new IntPtr(HTCLIENT);
        }

        return new IntPtr(InsideContent(x, y) ? HTCAPTION : HTTRANSPARENT);
    }

    private void OnPointerMove(nint lParam)
    {
        if (_locked)
        {
            return;
        }

        // A move back inside means the pointer did not really leave after all.
        KillTimer(Handle, ControlsTimerId);

        if (!TryClientPointToDip(lParam, out var x, out var y))
        {
            return;
        }

        ShowControlsAt(x, y);
        TrackLeave(nonClient: false);
    }

    private void OnNonClientPointerMove(nint lParam)
    {
        if (_locked)
        {
            return;
        }

        KillTimer(Handle, ControlsTimerId);

        if (!TryScreenPointToDip(lParam, out var x, out var y))
        {
            return;
        }

        ShowControlsAt(x, y);
        TrackLeave(nonClient: true);
    }

    private void ShowControlsAt(float x, float y)
    {
        var control = ControlAt(x, y);
        var changed = false;

        if (!_showControls)
        {
            _showControls = true;
            changed = true;
        }

        if (control != _hoverControl)
        {
            _hoverControl = control;
            changed = true;
        }

        if (changed)
        {
            Render();
        }
    }

    /// <summary>
    /// Hiding is delayed rather than immediate: the pointer has to cross the gap between the lyrics
    /// and the bar, and a bar that vanishes mid-journey cannot be clicked.
    /// </summary>
    private void OnPointerLeave()
    {
        if (_locked)
        {
            return;
        }

        if (_showControls)
        {
            SetTimer(Handle, ControlsTimerId, ControlsHideDelayMs, 0);
        }
    }

    private void HideControlsAfterDelay()
    {
        KillTimer(Handle, ControlsTimerId);

        if (_locked || _pressedControl is not null)
        {
            return;
        }

        HideControls();
    }

    /// <summary>Drop the bar and any transient hover or press state. Safe to call when already hidden.</summary>
    private void HideControls()
    {
        if (Handle != nint.Zero)
        {
            KillTimer(Handle, ControlsTimerId);
        }

        var changed = _showControls || _hoverControl is not null || _pressedControl is not null;

        _showControls = false;
        _hoverControl = null;
        _pressedControl = null;

        if (changed && Handle != nint.Zero)
        {
            Render();
        }
    }

    private bool OnPointerDown(nint lParam)
    {
        if (_locked || !TryClientPointToDip(lParam, out var x, out var y))
        {
            return false;
        }

        var control = ControlAt(x, y);
        if (control is null)
        {
            return false;
        }

        _pressedControl = control;
        _hoverControl = control;
        SetCapture(Handle);
        Render();
        return true;
    }

    private bool OnPointerUp(nint lParam)
    {
        if (_pressedControl is not { } pressed)
        {
            return false;
        }

        TryClientPointToDip(lParam, out var x, out var y);
        var releasedOver = ControlAt(x, y);
        _pressedControl = null;
        ReleaseCapture();

        if (releasedOver == pressed)
        {
            Activate(pressed);
        }

        UpdateHoverFromCursor();
        Render();
        return true;
    }

    private void Activate(OverlayControl control)
    {
        switch (control)
        {
            case OverlayControl.Settings:
                HideControls();
                SettingsRequested?.Invoke();
                return;

            case OverlayControl.ViewLyrics:
                HideControls();
                ViewLyricsRequested?.Invoke();
                return;

            case OverlayControl.ChooseLyrics:
                HideControls();
                ChooseLyricsRequested?.Invoke();
                return;

            case OverlayControl.OffsetBack:
                OffsetStepRequested?.Invoke(-250);
                return;

            case OverlayControl.OffsetForward:
                OffsetStepRequested?.Invoke(250);
                return;

            case OverlayControl.OffsetReset:
                OffsetResetRequested?.Invoke();
                return;

            case OverlayControl.Layout:
                LayoutRequested?.Invoke();
                return;

            case OverlayControl.Lock:
                // The bar belongs to the unlocked state, so locking from it also dismisses it - the
                // window stops answering the mouse on the same tick.
                HideControls();
                LockRequested?.Invoke();
                return;
        }
    }

    private IntPtr OnSetCursor(nint hWnd, nint wParam, nint lParam)
    {
        if (_locked || !_showControls || (lParam.ToInt64() & 0xFFFF) != HTCLIENT)
        {
            return DefaultWindowProc(hWnd, WmSetCursor, wParam, lParam);
        }

        if (ControlAtCursor() is null)
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

    private OverlayControl? ControlAtCursor()
    {
        if (!GetCursorPos(out var point) || !ScreenToClient(Handle, ref point))
        {
            return null;
        }

        var scale = _dpi / 96f;
        return ControlAt(point.X / scale, point.Y / scale);
    }

    private void UpdateHoverFromCursor()
    {
        if (_locked || !_showControls)
        {
            return;
        }

        var control = ControlAtCursor();

        if (control != _hoverControl)
        {
            _hoverControl = control;
        }
    }

    private OverlayControl? ControlAt(float x, float y)
    {
        if (_renderer is null)
        {
            return null;
        }

        foreach (var hit in _renderer.ControlHits)
        {
            if (hit.Contains(x, y))
            {
                return hit.Control;
            }
        }

        return null;
    }

    /// <summary>
    /// The strip between the bar and the lyrics, measured in device-independent coordinates. Only a
    /// bridge, not a click target: it exists so hit testing does not fall through while the pointer
    /// travels towards a button.
    /// </summary>
    private bool InControlBridge(float x, float y)
    {
        if (_renderer is null)
        {
            return false;
        }

        var bar = _renderer.ControlsBounds;
        var content = _renderer.ContentBounds;

        if (bar.Right <= bar.Left || content.Right <= content.Left)
        {
            return false;
        }

        if (x < bar.Left || x >= bar.Right)
        {
            return false;
        }

        if (bar.Bottom <= content.Top)
        {
            return y >= bar.Bottom && y < content.Top;
        }

        if (bar.Top >= content.Bottom)
        {
            return y >= content.Bottom && y < bar.Top;
        }

        return false;
    }

    private bool InsideContent(float x, float y)
    {
        if (_renderer is null)
        {
            return false;
        }

        var bounds = _renderer.ContentBounds;
        return bounds.Right > bounds.Left
               && bounds.Bottom > bounds.Top
               && x >= bounds.Left
               && x < bounds.Right
               && y >= bounds.Top
               && y < bounds.Bottom;
    }

    private bool TryScreenPointToDip(nint lParam, out float x, out float y)
    {
        var raw = lParam.ToInt64();
        var point = new POINT
        {
            X = (short)(raw & 0xFFFF),
            Y = (short)((raw >> 16) & 0xFFFF)
        };

        if (!ScreenToClient(Handle, ref point))
        {
            x = 0f;
            y = 0f;
            return false;
        }

        var scale = _dpi / 96f;
        x = point.X / scale;
        y = point.Y / scale;
        return true;
    }

    private bool TryClientPointToDip(nint lParam, out float x, out float y)
    {
        var raw = lParam.ToInt64();
        var scale = _dpi / 96f;

        x = (short)(raw & 0xFFFF) / scale;
        y = (short)((raw >> 16) & 0xFFFF) / scale;
        return true;
    }

    private void TrackLeave(bool nonClient)
    {
        var track = new TRACKMOUSEEVENT
        {
            cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = TME_LEAVE | (nonClient ? TmeNonClient : 0u),
            hwndTrack = Handle
        };

        TrackMouseEvent(ref track);
    }

    private void NotifyMoved()
    {
        if (GetWindowRect(Handle, out var rect))
        {
            Moved?.Invoke(rect.Left, rect.Top);
        }
    }

    private void EnsureHost()
    {
        var (dipWidth, dipHeight) = DipSizeFor(_mode);
        _host = CompositionHost.Create(Handle, ToPixels(dipWidth), ToPixels(dipHeight), _dpi);
        _renderer = new LyricsRenderer(_host.WriteFactory!, _theme);
    }

    private void RecreateHost()
    {
        var wasVisible = _visible;

        _renderer?.Dispose();
        _renderer = null;
        _host?.Dispose();
        _host = null;

        try
        {
            EnsureHost();
            Render();
        }
        catch (Exception)
        {
            // Leave the overlay blank rather than crash the process.
        }

        if (wasVisible)
        {
            SetVisible(true);
        }
    }

    private void ApplyBounds()
    {
        var (dipWidth, dipHeight) = DipSizeFor(_mode);
        var pixelWidth = ToPixels(dipWidth);
        var pixelHeight = ToPixels(dipHeight);

        var monitor = _monitor != IntPtr.Zero ? _monitor : MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return;
        }

        var work = info.rcWork;
        var x = work.Left + ((work.Width - pixelWidth) / 2) + _offsetX;
        var y = _anchor switch
        {
            OverlayAnchor.TopCenter => work.Top + _offsetY,
            OverlayAnchor.BottomCenter => work.Bottom - pixelHeight - _offsetY,
            _ => work.Top + ((work.Height - pixelHeight) / 2) + _offsetY
        };

        SetWindowPos(
            Handle,
            HWND_TOPMOST,
            x,
            y,
            pixelWidth,
            pixelHeight,
            SWP_NOACTIVATE | (_visible ? SWP_SHOWWINDOW : 0));

        _host?.Resize(pixelWidth, pixelHeight, _dpi);
    }

    private float ResolveDpi()
    {
        var dpi = GetDpiForWindow(Handle);
        return dpi > 0 ? dpi : 96f;
    }

    private int ToPixels(float dip) => (int)MathF.Round(dip * _dpi / 96f);

    private static (float Width, float Height) DipSizeFor(OverlayMode mode) =>
        mode switch
        {
            OverlayMode.SingleLine => (SingleLineWidthDip, SingleLineHeightDip),
            OverlayMode.SingleLineWithNext => (SingleLineWithNextWidthDip, SingleLineWithNextHeightDip),
            _ => (MultiLineWidthDip, MultiLineHeightDip)
        };

    public override void Dispose()
    {
        if (Handle != nint.Zero)
        {
            KillTimer(Handle, ControlsTimerId);
        }

        _renderer?.Dispose();
        _renderer = null;
        _host?.Dispose();
        _host = null;

        base.Dispose();
    }
}
