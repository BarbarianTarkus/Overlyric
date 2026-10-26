using Overlyric.App;
using Overlyric.Overlay;

namespace Overlyric.ShortcutFeedbackTests;

/// <summary>
/// Checks the wording of the shortcut toast: a timing nudge names the total offset now in force
/// rather than repeating the step, and every state shortcut says which state it entered.
/// </summary>
internal static class Program
{
    private static int _checks;
    private static int _failures;

    private static int Main()
    {
        OffsetFormatChecks();
        OffsetNoticeChecks();
        StateNoticeChecks();

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ALL {_checks} CHECKS PASSED"
            : $"{_failures} OF {_checks} CHECK(S) FAILED");

        return _failures == 0 ? 0 : 1;
    }

    // ---- The exact form the user asked for: "Offset -0.25s" ----
    private static void OffsetFormatChecks()
    {
        Check("zero offset reads 0.00s", ShortcutFeedback.FormatOffset(0) == "0.00s", ShortcutFeedback.FormatOffset(0));
        Check("an earlier offset keeps its minus", ShortcutFeedback.FormatOffset(-250) == "-0.25s", ShortcutFeedback.FormatOffset(-250));
        Check("a later offset gets a plus", ShortcutFeedback.FormatOffset(250) == "+0.25s", ShortcutFeedback.FormatOffset(250));
        Check("the clamp reads in seconds", ShortcutFeedback.FormatOffset(-10_000) == "-10.00s", ShortcutFeedback.FormatOffset(-10_000));
        Check("a half-second is not rounded away", ShortcutFeedback.FormatOffset(500) == "+0.50s", ShortcutFeedback.FormatOffset(500));
    }

    private static void OffsetNoticeChecks()
    {
        var earlier = ShortcutFeedback.ForOffset(-250, -250, false, "this track");
        Check("earlier offset is titled with the total", earlier.Title == "Offset -0.25s \u00b7 this track", earlier.Title);
        Check("the title names the layer that supplied the offset",
            earlier.Title.Contains("this track", StringComparison.Ordinal), earlier.Title);
        Check("earlier offset says which way it moved", earlier.Detail.Contains("earlier", StringComparison.Ordinal), earlier.Detail);
        Check("earlier offset names the step", earlier.Detail.Contains("0.25s", StringComparison.Ordinal), earlier.Detail);
        Check("a timing notice is informational", earlier.Tone == NoticeTone.Info, earlier.Tone);

        var later = ShortcutFeedback.ForOffset(250, 250, false, "default");
        Check("later offset is titled with the total", later.Title == "Offset +0.25s \u00b7 default", later.Title);

        var bluetooth = ShortcutFeedback.ForOffset(150, 150, false, "Bluetooth headphones");
        Check("a device correction names the device",
            bluetooth.Title.Contains("Bluetooth headphones", StringComparison.Ordinal), bluetooth.Title);
        Check("later offset says which way it moved", later.Detail.Contains("later", StringComparison.Ordinal), later.Detail);

        // The rule that matters: the title is the offset in force after the press, not the step.
        var existing = ShortcutFeedback.ForOffset(1_000, -250, false, "this track");
        Check("the title reports the accumulated offset", existing.Title == "Offset +1.00s \u00b7 this track", existing.Title);
        Check("the detail still names the nudge just made", existing.Detail.Contains("earlier", StringComparison.Ordinal), existing.Detail);

        var reset = ShortcutFeedback.ForOffset(0, -250, true, "default");
        Check("reset is titled with zero", reset.Title == "Offset 0.00s \u00b7 default", reset.Title);
        Check("reset says the timing was reset", reset.Detail.Contains("reset", StringComparison.OrdinalIgnoreCase), reset.Detail);

        var clamped = ShortcutFeedback.ForOffset(-10_000, 0, false, "default");
        Check("a press at the limit admits nothing moved", clamped.Detail.Contains("unchanged", StringComparison.OrdinalIgnoreCase), clamped.Detail);
        Check("a clamped press still shows where the offset is", clamped.Title == "Offset -10.00s \u00b7 default", clamped.Title);
    }

    private static void StateNoticeChecks()
    {
        var shown = ShortcutFeedback.ForOverlay(enabled: true, visible: true);
        var hidden = ShortcutFeedback.ForOverlay(enabled: false, visible: false);
        var onButQuiet = ShortcutFeedback.ForOverlay(enabled: true, visible: false);
        Check("showing the overlay says so", shown.Title == "Overlay shown", shown.Title);
        Check("hiding the overlay says so", hidden.Title == "Overlay hidden", hidden.Title);
        Check("shown and hidden do not read the same", shown.Detail != hidden.Detail, shown.Detail);
        Check("enabled but quiet does not claim lyrics are on screen",
            onButQuiet.Title == "Overlay on" && !onButQuiet.Detail.Contains("on screen", StringComparison.Ordinal),
            $"{onButQuiet.Title} / {onButQuiet.Detail}");

        var multi = ShortcutFeedback.ForLayout(true);
        var single = ShortcutFeedback.ForLayout(false);
        var peek = ShortcutFeedback.ForPeek();
        Check("the multi-line layout is named", multi.Title.Contains("Multi-line", StringComparison.Ordinal), multi.Title);
        Check("the single-line layout is named", single.Title.Contains("Single-line", StringComparison.Ordinal), single.Title);
        Check("the peek layout is named", peek.Title.Contains("next", StringComparison.OrdinalIgnoreCase), peek.Title);
        Check("layout notices are informational",
            multi.Tone == NoticeTone.Info && single.Tone == NoticeTone.Info && peek.Tone == NoticeTone.Info,
            $"{multi.Tone}/{single.Tone}/{peek.Tone}");

        var locked = ShortcutFeedback.ForLock(true);
        var unlocked = ShortcutFeedback.ForLock(false);
        Check("locking names the state", locked.Title == "Overlay locked", locked.Title);
        Check("unlocking names the state", unlocked.Title == "Overlay unlocked", unlocked.Title);
        Check("unlocking explains how to move it", unlocked.Detail.Contains("Drag", StringComparison.OrdinalIgnoreCase), unlocked.Detail);

        var settings = ShortcutFeedback.ForSettings();
        Check("opening settings says so", settings.Title == "Settings", settings.Title);
        Check("opening settings has a detail line", settings.Detail.Length > 0, settings.Detail);

        Check("every shortcut notice is complete",
            !shown.IsEmpty && !hidden.IsEmpty && !onButQuiet.IsEmpty && !multi.IsEmpty && !single.IsEmpty
            && !locked.IsEmpty && !unlocked.IsEmpty && !settings.IsEmpty,
            "");
    }

    private static void Check(string what, bool ok, object? detail)
    {
        _checks++;
        if (ok)
        {
            Console.WriteLine($"PASS | {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"FAIL | {what}   (got: {detail})");
    }
}
