using Overlyric.App;
using Overlyric.Overlay;

namespace Overlyric.ThemeTests;

/// <summary>
/// Checks the OLED bundle and the colours it turns into. The preset is one definition on the config,
/// and the blackout is one colour swap, so both can be pinned without opening the overlay.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        var defaults = new AppConfig();
        var defaultTheme = defaults.ToTheme();

        Check("a fresh config does not black out the sweep", !defaults.KaraokeBlackout);
        Check("without the blackout, un-sung words keep the dim colour",
            defaultTheme.KaraokeUnlitColor == defaultTheme.InactiveTextColor,
            $"{defaultTheme.KaraokeUnlitColor:X8} / {defaultTheme.InactiveTextColor:X8}");

        var blackout = new AppConfig { KaraokeBlackout = true, InactiveLineOpacity = 0.4 };
        var blackoutTheme = blackout.ToTheme();

        Check("the blackout makes un-sung words pure black",
            blackoutTheme.KaraokeUnlitColor == 0xFF000000,
            blackoutTheme.KaraokeUnlitColor.ToString("X8"));
        Check("the blackout leaves the surrounding lines dim",
            blackoutTheme.InactiveTextColor == 0x66F5F7FA,
            blackoutTheme.InactiveTextColor.ToString("X8"));
        Check("the blackout does not change the sung colour",
            blackoutTheme.ActiveTextColor == 0xFFF5F7FA,
            blackoutTheme.ActiveTextColor.ToString("X8"));

        // Start from the opposite of every preset value, so each assertion below proves the preset
        // moved it rather than found it already set.
        var config = new AppConfig
        {
            BackgroundOpacity = 0.7,
            ShowProgressBar = true,
            HeaderAutoHideSeconds = 0,
            ShowActiveLineHighlight = true,
            InactiveLineOpacity = 1.0,
            Karaoke = false,
            KaraokeBlackout = false,
            PixelShift = false,
            TextOutline = false,
            HideWhenPaused = false
        };

        config.ApplyOledPreset();

        Check("the preset removes the backdrop", config.BackgroundOpacity == 0.0, config.BackgroundOpacity);
        Check("the preset hides the progress bar", !config.ShowProgressBar);
        Check("the preset makes the track info fade", config.HeaderAutoHideSeconds == 8, config.HeaderAutoHideSeconds);
        Check("the preset removes the highlight bar", !config.ShowActiveLineHighlight);
        Check("the preset dims surrounding lines to 40 %", config.InactiveLineOpacity == 0.4, config.InactiveLineOpacity);
        Check("the preset turns the sweep on", config.Karaoke);
        Check("the preset blacks out the un-sung half", config.KaraokeBlackout);
        Check("the preset turns pixel shift on", config.PixelShift);
        Check("the preset uses the outline", config.TextOutline);
        Check("the preset hides while paused", config.HideWhenPaused);

        var theme = config.ToTheme();
        Check("the preset theme has no backdrop at all", (theme.BackgroundColor >> 24) == 0,
            theme.BackgroundColor.ToString("X8"));
        Check("the preset theme is pure black on un-sung words",
            theme.KaraokeUnlitColor == 0xFF000000,
            theme.KaraokeUnlitColor.ToString("X8"));
        Check("the preset theme keeps the surrounding lines readable",
            theme.InactiveTextColor == 0x66F5F7FA,
            theme.InactiveTextColor.ToString("X8"));

        // ---- Monitor preference: a hand-edited map degrades instead of breaking startup. ----
        var normalised = MonitorSettings.Normalise(new Dictionary<string, MonitorPlacement>
        {
            ["  "] = new() { X = 1 },
            [@"\\.\DISPLAY1"] = new() { X = 20_000, Y = -20_000 },
            [@"\\.\DISPLAY2"] = null!
        });

        Check("a hand-edited monitor map drops blank names", normalised.Count == 1, normalised.Count);
        Check("a monitor offset is clamped to the global range",
            normalised[@"\\.\DISPLAY1"].X == MonitorSettings.MaxOffset
            && normalised[@"\\.\DISPLAY1"].Y == -MonitorSettings.MaxOffset,
            $"{normalised[@"\\.\DISPLAY1"].X}/{normalised[@"\\.\DISPLAY1"].Y}");

        var monitorDefaults = new AppConfig();
        Check("a fresh config follows the cursor and remembers no display",
            monitorDefaults.Monitor == MonitorChoice.Cursor
            && monitorDefaults.MonitorDevice.Length == 0
            && monitorDefaults.MonitorOffsets.Count == 0,
            monitorDefaults.Monitor);

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");

        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string what, bool ok, object? detail = null)
    {
        if (ok)
        {
            Console.WriteLine($"PASS | {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"FAIL | {what}   (got: {detail})");
    }
}
