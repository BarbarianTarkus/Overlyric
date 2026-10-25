using System.Runtime.InteropServices;
using Overlyric.Interop;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.Overlay;

/// <summary>
/// The little always-on-top, click-through window that confirms a global shortcut.
/// </summary>
/// <remarks>
/// It is deliberately separate from <see cref="OverlayWindow"/>. The shortcut that needs explaining
/// most - "hide overlay" - is exactly the one after which the lyric window is gone, and the
/// no-lyrics and paused rules can hide the overlay while a timing nudge still deserves an answer. A
/// window of its own means the toast is visible in those cases without making the overlay pretend to
/// be on. It reuses <see cref="CompositionHost"/> and <see cref="LyricsRenderer"/> and draws only the
/// notice pill, so the toast and the track-info pill cannot drift apart visually.
/// </remarks>
internal sealed class ShortcutToastWindow : WindowBase
{
    private const ulong TimerId = 1;

    /// <summary>Long enough to read a value, short enough not to sit in the way after a nudge.</summary>
    private const int DurationMs = 1800;

    /// <summary>Wide enough for a song-length detail on one line; the pill itself hugs its text.</summary>
    private const float WidthDip = 780f;

    /// <summary>One notice pill plus breathing room, in device-independent pixels.</summary>
    private const float HeightDip = 80f;

    /// <summary>Clearance from the screen edge chosen for the toast.</summary>
    private const float EdgeMarginDip = 64f;

    private OverlayTheme _theme;
    private CompositionHost? _host;
    private LyricsRenderer? _renderer;
    private OverlayVisual _visual = new();
    private nint _monitor;
    private float _dpi = 96f;
    private bool _atTop = true;
    private bool _visible;

    public ShortcutToastWindow(OverlayTheme theme)
    {
        _theme = theme;
    }

    protected override string ClassName => "OverlyricShortcutToast";

    protected override int WindowStyle => WS_POPUP;

    protected override int WindowExtraStyle =>
        WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP | WS_EX_TRANSPARENT | WS_EX_LAYERED;

    public void Create()
    {
        CreateWindow("Overlyric", (int)WidthDip, (int)HeightDip);
        ApplyLayeredHitTesting();

        _dpi = ResolveDpi();
        ApplyBounds();
        EnsureHost();
    }

    /// <summary>Adopt a new theme in place, the same way the overlay does.</summary>
    public void SetTheme(OverlayTheme theme)
    {
        _theme = theme;
        _renderer?.SetTheme(theme);

        if (_visible)
        {
            Render();
        }
    }

    /// <summary>
    /// Show a notice and restart the dismiss timer. The caller chooses the edge opposite the overlay's
    /// anchor, so a toast never lands on top of the lyrics it is describing.
    /// </summary>
    public void Show(OverlayNotice notice, bool atTop, nint monitor)
    {
        _visual = new OverlayVisual { Notice = notice };
        _atTop = atTop;
        _monitor = monitor;
        _dpi = ResolveDpi();

        ApplyBounds();
        EnsureHost();

        _visible = true;
        ShowWindow(Handle, SW_SHOWNA);
        Render();

        // A second press replaces the first showing with its own full deadline.
        SetTimer(Handle, TimerId, DurationMs, 0);
    }

    private void Hide()
    {
        if (!_visible)
        {
            return;
        }

        _visible = false;
        KillTimer(Handle, TimerId);
        ShowWindow(Handle, SW_HIDE);
    }

    private void Render()
    {
        if (_host is null || _renderer is null)
        {
            return;
        }

        try
        {
            var context = _host.BeginDraw();
            _renderer.Draw(context, _host.DipSize, _visual);

            if (!_host.EndDraw())
            {
                RecreateHost();
            }
        }
        catch (Exception)
        {
            RecreateHost();
        }
    }

    protected override IntPtr WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_TIMER when wParam == (nint)TimerId:
                Hide();
                return IntPtr.Zero;

            // Belt and braces with WS_EX_TRANSPARENT: a toast must never eat a click aimed at the
            // application or video behind it.
            case WM_NCHITTEST:
                return new IntPtr(HTTRANSPARENT);

            case WM_PAINT:
                BeginPaint(hWnd, out var paint);
                EndPaint(hWnd, ref paint);
                return IntPtr.Zero;

            case WM_DISPLAYCHANGE:
                _dpi = ResolveDpi();
                ApplyBounds();
                RecreateHost();
                return IntPtr.Zero;

            case WM_DPICHANGED:
                _dpi = wParam.ToInt64() & 0xFFFF;
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

    /// <summary>
    /// Layered windows are what make a click-through overlay click-through; the same rule applies to
    /// this window. The alpha stays at 255 because DirectComposition supplies the pixels.
    /// </summary>
    private void ApplyLayeredHitTesting()
    {
        if (SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA))
        {
            return;
        }

        var style = GetWindowLongPtrW(Handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtrW(Handle, GWL_EXSTYLE, new nint(style & ~(long)WS_EX_LAYERED));
    }

    private void ApplyBounds()
    {
        var pixelWidth = ToPixels(WidthDip);
        var pixelHeight = ToPixels(HeightDip);

        var monitor = _monitor != nint.Zero ? _monitor : MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return;
        }

        var work = info.rcWork;
        var x = work.Left + ((work.Width - pixelWidth) / 2);
        var margin = ToPixels(EdgeMarginDip);
        var y = _atTop ? work.Top + margin : work.Bottom - pixelHeight - margin;

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

    private void EnsureHost()
    {
        if (_host is not null)
        {
            return;
        }

        _host = CompositionHost.Create(Handle, ToPixels(WidthDip), ToPixels(HeightDip), _dpi);
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
            // A lost device must not take the process down; the next shortcut simply has no toast.
        }

        if (wasVisible)
        {
            ShowWindow(Handle, SW_SHOWNA);
        }
    }

    private float ResolveDpi()
    {
        var dpi = GetDpiForWindow(Handle);
        return dpi > 0 ? dpi : 96f;
    }

    private int ToPixels(float dip) => (int)MathF.Round(dip * _dpi / 96f);

    public override void Dispose()
    {
        KillTimer(Handle, TimerId);

        _renderer?.Dispose();
        _renderer = null;
        _host?.Dispose();
        _host = null;

        base.Dispose();
    }
}
