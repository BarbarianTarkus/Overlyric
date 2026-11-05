namespace Overlyric.Media;

/// <summary>
/// The Presence decisions that can be made without Windows: whether a foreground program is on the
/// hide list, and whether the fullscreen readings look like a real exclusive-fullscreen game.
/// </summary>
/// <remarks>
/// Both are deliberately conservative. A protected process reads as "unknown", and unknown never
/// hides the overlay; the fullscreen check is a conjunction of four facts plus a sustained state, so
/// a maximised window or a notification while a video plays cannot raise it.
/// </remarks>
internal static class PresenceRules
{
    public const int QunsBusy = 2;

    public const int QunsRunningD3DFullScreen = 3;

    public const int MaxHideApps = 32;

    /// <summary>
    /// True when the foreground program's file name (or its name without .exe) is on the hide list.
    /// The comparison is case-insensitive and path-insensitive: the list stores names, not paths.
    /// </summary>
    public static bool IsHiddenForApp(IReadOnlyList<string>? hideApps, string? foregroundPath)
    {
        if (hideApps is null || hideApps.Count == 0 || string.IsNullOrWhiteSpace(foregroundPath))
        {
            return false;
        }

        var file = Path.GetFileName(foregroundPath);

        if (file.Length == 0)
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(file);

        foreach (var entry in hideApps)
        {
            var name = (entry ?? string.Empty).Trim();

            if (name.Length == 0)
            {
                continue;
            }

            if (string.Equals(name, file, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, stem, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The exclusive-fullscreen heuristic: the shell says fullscreen or busy, the foreground window
    /// is not ours or the shell, it is on the overlay's display, and its extended bounds cover that
    /// display entirely. Each leg is measured; none is guessed.
    /// </summary>
    public static bool LooksExclusiveFullscreen(
        int notificationState,
        bool foregroundIsOwnOrShell,
        bool foregroundOnOverlayMonitor,
        bool coversOverlayMonitor) =>
        notificationState is QunsBusy or QunsRunningD3DFullScreen
        && !foregroundIsOwnOrShell
        && foregroundOnOverlayMonitor
        && coversOverlayMonitor;

    /// <summary>
    /// Repairs the hand-editable hide list: trims entries, drops blanks and duplicates (Windows file
    /// names are case-insensitive), keeps a bare file name rather than a path, and caps the list.
    /// </summary>
    public static List<string> NormaliseHideApps(List<string>? source)
    {
        var clean = new List<string>();

        if (source is null)
        {
            return clean;
        }

        foreach (var entry in source)
        {
            var name = Path.GetFileName((entry ?? string.Empty).Trim());

            if (name.Length == 0 || clean.Count >= MaxHideApps)
            {
                continue;
            }

            if (!clean.Exists(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            {
                clean.Add(name);
            }
        }

        return clean;
    }
}

/// <summary>
/// Decides when the fullscreen reading has held long enough to be worth one quiet sentence, and
/// makes sure the same situation is announced once rather than on every tick.
/// </summary>
internal sealed class FullscreenWatch
{
    /// <summary>Long enough that alt-tabbing, a flash video or a fullscreen settings dialog is not it.</summary>
    public const int SustainMs = 10_000;

    private long _since = -1;
    private bool _announced;

    public bool Candidate { get; private set; }

    /// <summary>True on the single update where the sustained situation becomes worth announcing.</summary>
    public bool IsNewSituation { get; private set; }

    public void Update(bool candidate, long nowMs)
    {
        IsNewSituation = false;

        if (!candidate)
        {
            Reset();
            return;
        }

        Candidate = true;
        _since = _since < 0 ? nowMs : _since;

        if (!_announced && nowMs - _since >= SustainMs)
        {
            _announced = true;
            IsNewSituation = true;
        }
    }

    public void Reset()
    {
        Candidate = false;
        IsNewSituation = false;
        _since = -1;
        _announced = false;
    }
}
