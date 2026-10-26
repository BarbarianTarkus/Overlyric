using System.Runtime.InteropServices;
using Overlyric.Interop;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.App;

/// <summary>Notification-area icon and its context menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = WM_APP + 10;

    private readonly nint _owner;
    private nint _icon;
    private bool _added;
    private bool _disposed;

    public TrayIcon(nint owner)
    {
        _owner = owner;
    }

    public static uint Message => CallbackMessage;

    public event Action? LeftClicked;

    public event Action? MenuRequested;

    public void Show(string tooltip)
    {
        // The executable's own icon, at notification-area size. The stock icons below are only a
        // fallback for a build that somehow lost its resources: a tray entry with no icon at all
        // leaves the user unable to find the app.
        _icon = AppIcon.Small;
        if (_icon == nint.Zero)
        {
            _icon = LoadIconW(nint.Zero, new nint(32512));
        }

        if (_icon == nint.Zero)
        {
            _icon = LoadIconW(nint.Zero, new nint(32513));
        }

        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _owner,
            uID = IconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = Truncate(tooltip, 127),
            szInfo = "",
            szInfoTitle = ""
        };

        _added = ShellNotifyIcon(NIM_ADD, data);

        if (_added)
        {
            data.uFlags = NIF_TIP | NIF_SHOWTIP;
            ShellNotifyIcon(NIM_SETVERSION, data);
        }
    }

    public void SetTooltip(string tooltip)
    {
        if (!_added)
        {
            return;
        }

        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _owner,
            uID = IconId,
            uFlags = NIF_TIP,
            hIcon = _icon,
            szTip = Truncate(tooltip, 127),
            szInfo = "",
            szInfoTitle = ""
        };

        ShellNotifyIcon(NIM_MODIFY, data);
    }

    /// <summary>
    /// Show a balloon from the tray icon. This exists for the one class of problem the overlay
    /// cannot report: when it is not there at all, or is showing nothing useful, there is no surface
    /// left to explain why.
    /// </summary>
    public void Alert(string title, string text)
    {
        if (!_added)
        {
            return;
        }

        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _owner,
            uID = IconId,
            uFlags = NIF_INFO,
            hIcon = _icon,
            szTip = "",
            szInfo = Truncate(text, 255),
            szInfoTitle = Truncate(title, 63),
            dwInfoFlags = NIIF_INFO
        };

        ShellNotifyIcon(NIM_MODIFY, data);
    }

    /// <summary>Handle the raw messages Windows sends to the owner window for the tray icon.</summary>
    public void HandleMessage(nint lParam)
    {
        var message = (int)lParam & 0xFFFF;

        switch (message)
        {
            case WM_LBUTTONUP:
                LeftClicked?.Invoke();
                break;
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                MenuRequested?.Invoke();
                break;
        }
    }

    /// <summary>
    /// Show the context menu at the cursor. Uses <c>TPM_RETURNCMD</c> so the selection is returned
    /// directly instead of arriving later as a WM_COMMAND.
    /// </summary>
    public static int ShowMenu(nint owner, IReadOnlyList<TrayMenuItem> items)
    {
        var menu = CreatePopupMenu();
        if (menu == nint.Zero)
        {
            return 0;
        }

        try
        {
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    AppendMenuW(menu, MF_SEPARATOR, nint.Zero, null);
                    continue;
                }

                if (item.Children is { Count: > 0 } children)
                {
                    // Submenus are owned by the parent, so they are deliberately not destroyed here.
                    var submenu = CreatePopupMenu();
                    if (submenu == nint.Zero)
                    {
                        continue;
                    }

                    foreach (var child in children)
                    {
                        if (child.IsSeparator)
                        {
                            AppendMenuW(submenu, MF_SEPARATOR, nint.Zero, null);
                            continue;
                        }

                        var childFlags = MF_STRING
                            | (child.IsChecked ? MF_CHECKED : MF_UNCHECKED)
                            | (child.IsDisabled ? MF_GRAYED : 0u);
                        AppendMenuW(submenu, childFlags, new nint(child.Id), child.Text);
                    }

                    AppendMenuW(menu, MF_POPUP, submenu, item.Text);
                    continue;
                }

                var flags = MF_STRING
                    | (item.IsChecked ? MF_CHECKED : MF_UNCHECKED)
                    | (item.IsDisabled ? MF_GRAYED : 0u);
                AppendMenuW(menu, flags, new nint(item.Id), item.Text);
            }

            if (!GetCursorPos(out var cursor))
            {
                return 0;
            }

            SetForegroundWindow(owner);

            var selected = TrackPopupMenu(
                menu,
                TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                cursor.X,
                cursor.Y,
                0,
                owner,
                nint.Zero);

            // Documented workaround so the menu dismisses correctly.
            PostMessageW(owner, 0, nint.Zero, nint.Zero);

            return selected;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added)
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _owner,
                uID = IconId,
                szTip = "",
                szInfo = "",
                szInfoTitle = ""
            };

            ShellNotifyIcon(NIM_DELETE, data);
            _added = false;
        }
    }
}

internal readonly record struct TrayMenuItem(
    int Id,
    string Text,
    bool IsChecked = false,
    bool IsSeparator = false,
    IReadOnlyList<TrayMenuItem>? Children = null,
    bool IsDisabled = false)
{
    public static TrayMenuItem Separator => new(0, "", IsSeparator: true);

    /// <summary>A nested menu. Selection inside it still returns a command id to the caller.</summary>
    public static TrayMenuItem Submenu(string text, IReadOnlyList<TrayMenuItem> children) =>
        new(0, text, Children: children);

    /// <summary>A greyed-out informational row that cannot be chosen.</summary>
    public static TrayMenuItem Disabled(string text) => new(0, text, IsDisabled: true);
}
