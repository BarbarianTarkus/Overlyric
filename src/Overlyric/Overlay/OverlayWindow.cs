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
    private const uint SwpFramechanged = 0x0020;
    private const uint SwpNoZOrder = 0x0004;

    private const float SingleLineWidthDip = 1200f;
    private const float SingleLineHeightDip = 200f;
    private const float MultiLineWidthDip = 1040f;
    private const float MultiLineHeightDip = 430f;

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
    private RECT _interactiveBounds;

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

    public bool IsVisible => _visible;

    public bool IsLocked => _locked;

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
            _renderer.Draw(context, _host.DipSize, _visual);

            var presented = _host.EndDraw();
            UpdateInteractiveBounds();

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

            case WM_MOUSEWHEEL:
                if (!_locked)
                {
                    OpacityScrolled?.Invoke((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                }

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
    }

    private IntPtr HitTest(nint lParam)
    {
        if (_locked)
        {
            return new IntPtr(HTTRANSPARENT);
        }

        var raw = lParam.ToInt64();
        var point = new POINT
        {
            X = (short)(raw & 0xFFFF),
            Y = (short)((raw >> 16) & 0xFFFF)
        };

        if (!ScreenToClient(Handle, ref point))
        {
            return new IntPtr(HTTRANSPARENT);
        }

        var inside = point.X >= _interactiveBounds.Left
                     && point.X < _interactiveBounds.Right
                     && point.Y >= _interactiveBounds.Top
                     && point.Y < _interactiveBounds.Bottom;

        return new IntPtr(inside ? HTCAPTION : HTTRANSPARENT);
    }

    private void UpdateInteractiveBounds()
    {
        if (_renderer is null)
        {
            return;
        }

        var bounds = _renderer.ContentBounds;
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            _interactiveBounds = default;
            return;
        }

        var scale = _dpi / 96f;
        _interactiveBounds = new RECT
        {
            Left = (int)(bounds.Left * scale),
            Top = (int)(bounds.Top * scale),
            Right = (int)(bounds.Right * scale),
            Bottom = (int)(bounds.Bottom * scale)
        };
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
        mode == OverlayMode.SingleLine
            ? (SingleLineWidthDip, SingleLineHeightDip)
            : (MultiLineWidthDip, MultiLineHeightDip);

    public override void Dispose()
    {
        _renderer?.Dispose();
        _renderer = null;
        _host?.Dispose();
        _host = null;

        base.Dispose();
    }
}
