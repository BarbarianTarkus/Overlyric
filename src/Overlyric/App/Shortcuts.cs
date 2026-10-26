namespace Overlyric.App;

/// <summary>
/// One rebindable action: the command it dispatches, how it reads in the settings window, and the
/// chord it gets when the user has never changed it.
/// </summary>
internal sealed record ShortcutAction(string Name, int Command, string Label, string Default);

/// <summary>The actions a user may rebind, in the order the settings window lists them.</summary>
internal static class ShortcutActions
{
    public static readonly ShortcutAction[] All =
    [
        new("ToggleOverlay", Commands.ToggleOverlay, "Show / hide overlay", "Ctrl+Alt+H"),
        new("ToggleMode", Commands.ToggleMode, "Switch layout", "Ctrl+Alt+M"),
        new("ToggleLock", Commands.ToggleLock, "Lock / unlock", "Ctrl+Alt+L"),
        new("OffsetBack", Commands.OffsetBack, "Lyrics earlier", "Ctrl+Alt+Comma"),
        new("OffsetForward", Commands.OffsetForward, "Lyrics later", "Ctrl+Alt+Period"),
        new("OffsetReset", Commands.OffsetReset, "Reset timing", "Ctrl+Alt+0"),
        new("OpenSettings", Commands.OpenSettings, "Open settings", ""),
        new("OpenLyricSheet", Commands.OpenLyricSheet, "Open full lyrics", ""),
        new("ChooseLyrics", Commands.OpenLyricPicker, "Choose lyrics", "")
    ];

    public static ShortcutAction? ForCommand(int command)
    {
        foreach (var action in All)
        {
            if (action.Command == command)
            {
                return action;
            }
        }

        return null;
    }

    /// <summary>The chord actually in force: the stored one, or the default when absent or unusable.</summary>
    public static HotKeyChord Chord(AppConfig config, ShortcutAction action)
    {
        if (config.Shortcuts.TryGetValue(action.Name, out var stored))
        {
            // An empty string is a deliberate "unbound", not a missing entry.
            return HotKeyChord.Parse(stored);
        }
        return HotKeyChord.Parse(action.Default);
    }

    /// <summary>What the settings window shows in the chip.</summary>
    public static string Text(AppConfig config, ShortcutAction action) => Chord(config, action).ToString();
}
