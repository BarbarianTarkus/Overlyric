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

        // ---- Curated palettes: each preset moves the hues, defaults stay where they were. ----
        var warm = new AppConfig { Theme = ThemePreset.Warm }.ToTheme();
        var cool = new AppConfig { Theme = ThemePreset.Cool }.ToTheme();
        var mono = new AppConfig { Theme = ThemePreset.Mono }.ToTheme();

        Check("the default palette keeps the original text colour",
            defaultTheme.ActiveTextColor == 0xFFF5F7FA && defaultTheme.ControlAccentColor == 0xFF7CC4FF,
            $"{defaultTheme.ActiveTextColor:X8}/{defaultTheme.ControlAccentColor:X8}");
        Check("the warm palette moves text and accent to warm hues",
            warm.ActiveTextColor == 0xFFFFF1E0 && warm.ControlAccentColor == 0xFFFFB870,
            $"{warm.ActiveTextColor:X8}/{warm.ControlAccentColor:X8}");
        Check("the cool palette is a different colour family",
            cool.ActiveTextColor == 0xFFEAF4FF && cool.ControlAccentColor == 0xFF8CCBFF,
            $"{cool.ActiveTextColor:X8}/{cool.ControlAccentColor:X8}");
        Check("the mono palette is neutral",
            mono.ActiveTextColor == 0xFFF2F2F2 && mono.ControlAccentColor == 0xFFD8DEE9,
            $"{mono.ActiveTextColor:X8}/{mono.ControlAccentColor:X8}");
        Check("the palettes are all different", warm.ActiveTextColor != cool.ActiveTextColor
            && cool.ActiveTextColor != mono.ActiveTextColor
            && mono.ActiveTextColor != defaultTheme.ActiveTextColor, "");
        Check("an unknown palette number degrades to the default",
            new AppConfig { Theme = (ThemePreset)99 }.ToTheme().ActiveTextColor == defaultTheme.ActiveTextColor, "");

        // ---- Active-line weight. ----
        Check("the default active weight is semibold",
            new AppConfig().ToTheme().ActiveFontWeight == Vortice.DirectWrite.FontWeight.SemiBold, "");
        Check("the weight choice reaches the theme",
            new AppConfig { ActiveLyricWeight = LyricWeight.Bold }.ToTheme().ActiveFontWeight == Vortice.DirectWrite.FontWeight.Bold
            && new AppConfig { ActiveLyricWeight = LyricWeight.Normal }.ToTheme().ActiveFontWeight == Vortice.DirectWrite.FontWeight.Normal,
            "");
        Check("an unknown weight number degrades to semibold",
            new AppConfig { ActiveLyricWeight = (LyricWeight)99 }.ToTheme().ActiveFontWeight == Vortice.DirectWrite.FontWeight.SemiBold, "");

        // ---- Stage mode: a full-monitor audience surface, not a theme. ----
        var stageBlack = new AppConfig { StageMode = true }.ToTheme();
        var stageGreen = new AppConfig { StageMode = true, StageChroma = true }.ToTheme();

        Check("the stage backdrop is opaque black by default",
            stageBlack.BackgroundColor == 0xFF000000 && stageBlack.StageMode, stageBlack.BackgroundColor.ToString("X8"));
        Check("the chroma stage backdrop is pure green",
            stageGreen.BackgroundColor == 0xFF00FF00, stageGreen.BackgroundColor.ToString("X8"));
        Check("the stage uses seven large lines",
            stageBlack.MultiLineCount == 7 && stageBlack.ActiveFontSize >= 56f && stageBlack.InactiveFontSize >= 36f,
            $"{stageBlack.MultiLineCount}/{stageBlack.ActiveFontSize}/{stageBlack.InactiveFontSize}");
        Check("the stage drops the highlight and forces the outline",
            !stageBlack.ShowActiveLineHighlight && stageBlack.TextOutline, "");
        Check("stage mode is off by default", !new AppConfig().StageMode, new AppConfig().StageMode);

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
