namespace Overlyric.App;

/// <summary>One command a running instance can execute, from its own command line or a second one.</summary>
internal enum HostCommand
{
    None = 0,
    Show,
    Hide,
    Toggle,
    LayoutSingle,
    LayoutPeek,
    LayoutMulti,
    LayoutNext,
    Lock,
    Unlock,
    ToggleLock,
    OffsetEarlier,
    OffsetLater,
    OffsetReset,
    Preview,
    Settings,
    Diagnostics,
    RefreshLyrics,
    StageToggle,
    StageOn,
    StageOff,
    StageGreen,
    StageBlack,
    Exit
}

/// <summary>
/// Parses the command-line actions a script or a second launch can send to the running instance.
/// </summary>
/// <remarks>
/// The first instance owns playback, so a second one does not start a second overlay: it forwards
/// its arguments over a named pipe. Parsing is kept away from both ends so the grammar can be tested
/// without starting the app. Unknown switches are an error rather than a silent no-op, because a
/// script that quietly did nothing is worse than one that says why.
/// </remarks>
internal static class CommandLine
{
    public static bool TryParse(IReadOnlyList<string> args, out HostCommand command, out string? error)
    {
        command = HostCommand.None;
        error = null;

        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var argument = raw.Trim();

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                // Startup flags that predate this parser are consumed by the host itself.
                if (argument is "--demo" or "--settings" or "--preview")
                {
                    continue;
                }

                error = $"Unknown argument '{argument}'.";
                command = HostCommand.None;
                return false;
            }

            var body = argument[2..];
            var equals = body.IndexOf('=');
            var name = (equals >= 0 ? body[..equals] : body).ToLowerInvariant();
            var value = equals >= 0 ? body[(equals + 1)..] : string.Empty;

            var parsed = name switch
            {
                "toggle" => HostCommand.Toggle,
                "show" => HostCommand.Show,
                "hide" => HostCommand.Hide,
                "lock" => HostCommand.Lock,
                "unlock" => HostCommand.Unlock,
                "preview" => HostCommand.Preview,
                "settings" => HostCommand.Settings,
                "diagnostics" => HostCommand.Diagnostics,
                "refresh" or "refresh-lyrics" => HostCommand.RefreshLyrics,
                "exit" or "quit" => HostCommand.Exit,
                "layout" => value.ToLowerInvariant() switch
                {
                    "single" => HostCommand.LayoutSingle,
                    "peek" or "single+1" => HostCommand.LayoutPeek,
                    "multi" => HostCommand.LayoutMulti,
                    "next" or "cycle" or "" => HostCommand.LayoutNext,
                    _ => HostCommand.None
                },
                "offset" => ParseOffset(value, out var offsetCommand) ? offsetCommand : HostCommand.None,
                "stage" => value.ToLowerInvariant() switch
                {
                    "" or "toggle" => HostCommand.StageToggle,
                    "on" => HostCommand.StageOn,
                    "off" => HostCommand.StageOff,
                    "green" or "chroma" => HostCommand.StageGreen,
                    "black" => HostCommand.StageBlack,
                    _ => HostCommand.None
                },
                "demo" => HostCommand.None,
                _ => HostCommand.None
            };

            if (parsed == HostCommand.None && name is not "demo")
            {
                error = $"Unknown switch '--{name}'.";
                command = HostCommand.None;
                return false;
            }

            if (parsed != HostCommand.None)
            {
                command = parsed;
            }
        }

        return true;
    }

    private static bool ParseOffset(string value, out HostCommand command)
    {
        command = HostCommand.None;

        if (value.Equals("reset", StringComparison.OrdinalIgnoreCase) || value == "0")
        {
            command = HostCommand.OffsetReset;
            return true;
        }

        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds))
        {
            return false;
        }

        command = milliseconds < 0 ? HostCommand.OffsetEarlier : HostCommand.OffsetLater;
        return true;
    }

    /// <summary>One stable line a second instance sends over the pipe; the parser owns the grammar.</summary>
    public static string Forward(IReadOnlyList<string> args) => string.Join(' ', args);
}
