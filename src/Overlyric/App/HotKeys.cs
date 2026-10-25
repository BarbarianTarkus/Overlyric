using System.Runtime.InteropServices;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.App;

/// <summary>Registers system-wide hotkeys against the message window.</summary>
internal sealed class HotKeyManager : IDisposable
{
    private readonly nint _owner;
    private readonly List<int> _registered = [];
    private bool _disposed;

    public HotKeyManager(nint owner)
    {
        _owner = owner;
    }

    public event Action<int>? Pressed;

    /// <summary>
    /// Register a hotkey and report why it failed. Silently swallowing the result - as this used to -
    /// leaves the user pressing a shortcut that another application already owns, with no clue.
    /// </summary>
    public bool TryRegister(int id, uint modifiers, uint virtualKey, out int error)
    {
        if (RegisterHotKey(_owner, id, modifiers | MOD_NOREPEAT, virtualKey))
        {
            _registered.Add(id);
            error = 0;
            return true;
        }

        error = Marshal.GetLastWin32Error();
        return false;
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id))
        {
            UnregisterHotKey(_owner, id);
        }
    }

    public void HandleMessage(nint wParam)
    {
        Pressed?.Invoke((int)wParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var id in _registered)
        {
            UnregisterHotKey(_owner, id);
        }

        _registered.Clear();
    }
}

/// <summary>Identifiers shared by the hotkeys and the tray menu.</summary>
internal static class Commands
{
    public const int ToggleOverlay = 100;
    public const int ToggleMode = 101;
    public const int ToggleLock = 102;
    public const int ToggleHeader = 103;
    public const int ToggleProgressBar = 104;
    public const int ToggleHideWhenPaused = 105;
    public const int ToggleHideWhenNoLyrics = 106;
    public const int ToggleHideWhenInstrumental = 107;
    public const int TogglePlayerWarning = 108;
    public const int OffsetBack = 110;
    public const int OffsetForward = 111;
    public const int OffsetReset = 112;
    public const int AnchorBottom = 120;
    public const int AnchorTop = 121;
    public const int AnchorCenter = 122;
    public const int OpacityUp = 140;
    public const int OpacityDown = 141;
    public const int MoreLines = 150;
    public const int FewerLines = 151;
    public const int OpenCacheFolder = 160;
    public const int OpenConfigFolder = 161;
    public const int OpenSettings = 180;

    /// <summary>Open the lyric selector window, from the tray or the overlay's control bar.</summary>
    public const int OpenLyricPicker = 172;

    // The source menu is tray-only - no hotkey is bound to a player - but its ids live here with the
    // rest, because this class is the one id namespace: the tray and the hotkey table are dispatched
    // by the same switch, and a second place to look for "what is 200" is how collisions happen.
    public const int SourceAutomatic = 200;
    public const int SourcePriorityOrder = 201;

    /// <summary>Source-menu entries carry the index of the entry they were built for in the id.</summary>
    public const int PinSourceBase = 210;

    /// <summary>One past the source block, so its indices can be decoded by subtraction.</summary>
    public const int PinSourceEnd = 300;

    public const int Exit = 199;
}
