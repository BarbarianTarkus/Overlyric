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

    public void Register(int id, uint modifiers, uint virtualKey)
    {
        if (RegisterHotKey(_owner, id, modifiers | MOD_NOREPEAT, virtualKey))
        {
            _registered.Add(id);
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
    public const int AutoVersion = 170;
    public const int RefreshVersions = 171;

    /// <summary>Command ids for version-menu entries are this base plus the candidate index.</summary>
    public const int SelectVersionBase = 1000;

    public const int Exit = 199;
}
