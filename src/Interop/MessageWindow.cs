using static Overlyric.Interop.NativeMethods;

namespace Overlyric.Interop;

/// <summary>
/// A hidden, never-shown window that exists only to receive messages: global hotkeys, tray icon
/// callbacks and the application timer. Keeping these off the overlay window means the overlay
/// can be recreated (for example on a display-mode change) without losing input registrations.
/// </summary>
/// <remarks>
/// It is a tool window and it is never shown, which is what keeps the application out of the
/// taskbar while none of its real windows are open: the shell lists a top-level window on the
/// taskbar when it is visible, unowned and not <c>WS_EX_TOOLWINDOW</c>. Everything this window
/// does - posted and sent messages, timers, hotkeys, tray callbacks - works while it is hidden,
/// so there is nothing to gain from showing it.
/// </remarks>
internal sealed class MessageWindow : WindowBase
{
    private readonly Lock _gate = new();
    private Action<uint, nint, nint>? _handler;
    private bool _created;

    protected override string ClassName => "OverlyricMessageWindow";

    protected override int WindowStyle => 0;

    /// <summary>Never a taskbar entry, never in Alt+Tab: it is plumbing, not a window.</summary>
    protected override int WindowExtraStyle => WS_EX_TOOLWINDOW;

    public void Create()
    {
        CreateWindow("Overlyric", 0, 0, 0, 0);
    }

    public void SetHandler(Action<uint, nint, nint> handler)
    {
        lock (_gate)
        {
            _handler = handler;
        }
    }

    protected override void OnCreated()
    {
        _created = true;
    }

    protected override IntPtr WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (_created)
        {
            Action<uint, nint, nint>? handler;
            lock (_gate)
            {
                handler = _handler;
            }

            handler?.Invoke(msg, wParam, lParam);
        }

        return msg switch
        {
            WM_DESTROY => IntPtr.Zero,
            _ => DefaultWindowProc(hWnd, msg, wParam, lParam)
        };
    }
}
