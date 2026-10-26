using Overlyric.Media;

namespace Overlyric.PresenceTests;

/// <summary>
/// Checks the two Presence decisions: the per-app hide match and the exclusive-fullscreen heuristic
/// with its ten-second sustain and announce-once contract.
/// </summary>
internal static class Program
{
    private static int _checks;
    private static int _failures;

    private static int Main()
    {
        HideChecks();
        FullscreenChecks();
        WatchChecks();

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ALL {_checks} CHECKS PASSED"
            : $"{_failures} OF {_checks} CHECK(S) FAILED");

        return _failures == 0 ? 0 : 1;
    }

    private static void HideChecks()
    {
        var apps = new List<string> { "Teams.exe", "obs64", "zoom" };

        Check("an exact executable name hides", PresenceRules.IsHiddenForApp(apps, @"C:\Program Files\Teams\Teams.exe"), "");
        Check("a name without .exe hides", PresenceRules.IsHiddenForApp(apps, @"D:\Apps\OBS\obs64.exe"), "");
        Check("the match is case-insensitive", PresenceRules.IsHiddenForApp(apps, @"C:\x\ZOOM.EXE"), "");
        Check("an unlisted program does not hide", !PresenceRules.IsHiddenForApp(apps, @"C:\x\chrome.exe"), "");
        Check("an empty list hides nothing", !PresenceRules.IsHiddenForApp([], @"C:\x\Teams.exe"), "");
        Check("no foreground process is not a match", !PresenceRules.IsHiddenForApp(apps, null), "");
        Check("a protected process reads as no match", !PresenceRules.IsHiddenForApp(apps, ""), "");

        var normalised = PresenceRules.NormaliseHideApps(["  ", "Teams.exe", @"C:\Program Files\Teams\Teams.exe", "teams.exe", "OBS"]);
        Check("the hide list is trimmed, deduplicated and reduced to names",
            normalised.Count == 2 && normalised[0] == "Teams.exe" && normalised[1] == "OBS",
            string.Join("|", normalised));

        var capped = new List<string>();

        for (var i = 0; i < 40; i++)
        {
            capped.Add("app" + i + ".exe");
        }

        Check("the hide list is capped", PresenceRules.NormaliseHideApps(capped).Count == PresenceRules.MaxHideApps, PresenceRules.NormaliseHideApps(capped).Count);
    }

    private static void FullscreenChecks()
    {
        Check("fullscreen state 3 with a covering foreign window on our display looks exclusive",
            PresenceRules.LooksExclusiveFullscreen(3, foregroundIsOwnOrShell: false, foregroundOnOverlayMonitor: true, coversOverlayMonitor: true), "");
        Check("busy state 2 is included", PresenceRules.LooksExclusiveFullscreen(2, false, true, true), "");
        Check("accepting notifications is not fullscreen", !PresenceRules.LooksExclusiveFullscreen(5, false, true, true), "");
        Check("our own window or the shell is never the game", !PresenceRules.LooksExclusiveFullscreen(3, true, true, true), "");
        Check("a game on another display is not our fullscreen", !PresenceRules.LooksExclusiveFullscreen(3, false, false, true), "");
        Check("a window that does not cover the display is not fullscreen", !PresenceRules.LooksExclusiveFullscreen(3, false, true, false), "");
        Check("quiet time is not fullscreen", !PresenceRules.LooksExclusiveFullscreen(6, false, true, true), "");
    }

    private static void WatchChecks()
    {
        var watch = new FullscreenWatch();
        watch.Update(false, 0);
        Check("a quiet watch announces nothing", !watch.IsNewSituation && !watch.Candidate, "");

        watch.Update(true, 1_000);
        Check("a candidate is not announced immediately", !watch.IsNewSituation, "");
        watch.Update(true, 1_000 + FullscreenWatch.SustainMs - 1);
        Check("a candidate one tick short is not announced", !watch.IsNewSituation, "");
        watch.Update(true, 1_000 + FullscreenWatch.SustainMs);
        Check("the sustained situation is announced once", watch.IsNewSituation, "");
        watch.Update(true, 40_000);
        Check("the same situation is not announced again", !watch.IsNewSituation, "");

        watch.Update(false, 41_000);
        Check("losing the candidate clears it", !watch.Candidate, "");
        watch.Update(true, 50_000);
        watch.Update(true, 50_000 + FullscreenWatch.SustainMs);
        Check("a later situation is announced again", watch.IsNewSituation, "");

        watch.Reset();
        Check("reset forgets the situation", !watch.Candidate && !watch.IsNewSituation, "");
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
