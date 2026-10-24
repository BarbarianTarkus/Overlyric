using Overlyric.Media;

namespace Overlyric.SourcePreferenceTests;

/// <summary>
/// Checks the source selector: the rule that decides which of several publishing players the overlay
/// follows. This is the part a user configures by hand - the priority list and the pin live in
/// <c>config.json</c> - so every malformed order has to degrade into a usable one rather than into a
/// wrong player or an exception.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        // ---- With nothing configured, the old behaviour is preserved exactly ----

        var none = SourceSelector.Choose([], [], null);
        Check("no sessions means no choice", !none.HasChoice, none.Reason);

        var single = new[] { Candidate("aimp", current: false) };
        var singleChoice = SourceSelector.Choose(single, [], null);
        Check("one session and no config still follows it", singleChoice.Aumid == "aimp", singleChoice.Aumid);
        Check("and says why", singleChoice.Reason == SourceChoiceReason.Fallback, singleChoice.Reason);

        var two = new[]
        {
            Candidate("aimp", current: false),
            Candidate("brave", current: true)
        };
        var defaultChoice = SourceSelector.Choose(two, [], null);
        Check("an empty list follows what Windows calls current", defaultChoice.Aumid == "brave", defaultChoice.Aumid);
        Check("for the system-current reason", defaultChoice.Reason == SourceChoiceReason.SystemCurrent, defaultChoice.Reason);

        var defaultSettings = SourceSelector.Choose(two, SourceSettings.Default);
        Check("default settings behave like no configuration at all", defaultSettings.Aumid == "brave", defaultSettings.Aumid);

        var settingsChoice = SourceSelector.Choose(two, new SourceSettings([Preference("aimp")], ""));
        Check("the settings overload applies the priority list", settingsChoice.Aumid == "aimp", settingsChoice.Aumid);

        var noCurrent = new[]
        {
            Candidate("brave", current: false, playing: true, advancing: false),
            Candidate("aimp", current: false, playing: true, advancing: true)
        };
        var advancingWins = SourceSelector.Choose(noCurrent, [], null);
        Check("with no current session, the moving timeline wins", advancingWins.Aumid == "aimp", advancingWins.Aumid);

        var allPaused = new[]
        {
            Candidate("brave", current: false, playing: false, advancing: false),
            Candidate("aimp", current: false, playing: false, advancing: false)
        };
        var pausedPick = SourceSelector.Choose(allPaused, [], null);
        Check("with nothing playing, the first session is taken rather than nothing", pausedPick.Aumid == "brave", pausedPick.Aumid);

        // ---- The pin outranks everything, including a playing source ----

        var pinWins = SourceSelector.Choose(
            [Candidate("aimp", current: false, playing: false), Candidate("brave", current: true)],
            [],
            "aimp");
        Check("a pin beats a playing source", pinWins.Aumid == "aimp", pinWins.Aumid);
        Check("for the pinned reason", pinWins.Reason == SourceChoiceReason.Pinned, pinWins.Reason);

        var pinBeatsPriority = SourceSelector.Choose(
            [Candidate("aimp"), Candidate("brave")],
            [Preference("brave")],
            "aimp");
        Check("a pin beats the priority list", pinBeatsPriority.Aumid == "aimp", pinBeatsPriority.Aumid);

        var pinAbsent = SourceSelector.Choose(
            [Candidate("brave", current: true)],
            [],
            "aimp");
        Check("a pin naming an app that is not running falls through", pinAbsent.Aumid == "brave", pinAbsent.Aumid);
        Check("to the system current", pinAbsent.Reason == SourceChoiceReason.SystemCurrent, pinAbsent.Reason);

        var pinCase = SourceSelector.Choose([Candidate("AIMP.exe")], [], "aimp.exe");
        Check("a pin matches case-insensitively", pinCase.Aumid == "AIMP.exe", pinCase.Aumid);

        var pinSpaces = SourceSelector.Choose([Candidate("aimp")], [], "  aimp  ");
        Check("a pin is matched after trimming", pinSpaces.Aumid == "aimp", pinSpaces.Aumid);

        // ---- The priority list is taken in order ----

        var orderAb = SourceSelector.Choose(
            [Candidate("aimp"), Candidate("brave")],
            [Preference("aimp"), Preference("brave")],
            null);
        Check("the first listed player wins", orderAb.Aumid == "aimp", orderAb.Aumid);
        Check("for the priority reason", orderAb.Reason == SourceChoiceReason.Priority, orderAb.Reason);

        var orderBa = SourceSelector.Choose(
            [Candidate("aimp"), Candidate("brave")],
            [Preference("brave"), Preference("aimp")],
            null);
        Check("reordering the list changes the winner", orderBa.Aumid == "brave", orderBa.Aumid);

        var secondListed = SourceSelector.Choose(
            [Candidate("brave", current: true)],
            [Preference("aimp"), Preference("brave")],
            null);
        Check("a listed player that is not running is skipped", secondListed.Aumid == "brave", secondListed.Aumid);

        var beatsCurrent = SourceSelector.Choose(
            [Candidate("brave", current: true), Candidate("aimp", current: false)],
            [Preference("aimp")],
            null);
        Check("the list beats what Windows calls current", beatsCurrent.Aumid == "aimp", beatsCurrent.Aumid);

        var pausedButListed = SourceSelector.Choose(
            [Candidate("aimp", playing: false), Candidate("brave", playing: true)],
            [Preference("aimp")],
            null);
        Check("a listed player wins even when paused", pausedButListed.Aumid == "aimp", pausedButListed.Aumid);

        // ---- Two sessions under one app user model id ----

        var duplicates = new[]
        {
            new SourceCandidate("brave", "brave", "Brave", IsSystemCurrent: true, IsPlaying: false, PositionAdvances: false),
            new SourceCandidate("brave#1", "brave", "Brave", IsSystemCurrent: false, IsPlaying: true, PositionAdvances: true)
        };
        var duplicatePick = SourceSelector.Choose(duplicates, [Preference("brave")], null);
        Check("the playing duplicate is the one meant", duplicatePick.Key == "brave#1", duplicatePick.Key);

        var duplicatePin = SourceSelector.Choose(duplicates, [], "brave");
        Check("the pin picks the playing duplicate too", duplicatePin.Key == "brave#1", duplicatePin.Key);

        // ---- Nothing configured matches: Windows, then the best of what is left ----

        var listMisses = SourceSelector.Choose(
            [Candidate("brave", current: true)],
            [Preference("aimp")],
            null);
        Check("a list that matches nothing falls back to Windows", listMisses.Reason == SourceChoiceReason.SystemCurrent, listMisses.Reason);

        var listMissesNoCurrent = SourceSelector.Choose(
            [Candidate("brave", current: false)],
            [Preference("aimp")],
            null);
        Check("and to the best remaining candidate when Windows names none", listMissesNoCurrent.Reason == SourceChoiceReason.Fallback, listMissesNoCurrent.Reason);

        // ---- Cleaning a hand-edited list ----

        Check("a null list cleans to empty", SourceSelector.NormalisePriority(null).Count == 0, "non-empty");

        var cleaned = SourceSelector.NormalisePriority(
        [
            Preference("  aimp  ", "  AIMP  "),
            Preference("", "blank"),
            Preference("AIMP", "duplicate"),
            null!,
            Preference("brave", "")
        ]);
        Check("blanks and duplicates are dropped", cleaned.Count == 2, cleaned.Count);
        Check("order survives cleaning", cleaned[0].Aumid == "aimp" && cleaned[1].Aumid == "brave", Describe(cleaned));
        Check("values are trimmed", cleaned[0].Label == "AIMP", cleaned[0].Label);
        Check("a missing label becomes the app id", cleaned[1].Label == "brave", cleaned[1].Label);

        var overlong = SourceSelector.NormalisePriority(
            Enumerable.Range(0, SourceSelector.MaxPriority + 4).Select(i => Preference($"app{i}")));
        Check("the list is capped at the size the settings grid can draw", overlong.Count == SourceSelector.MaxPriority, overlong.Count);
        Check("and keeps the highest-priority entries", overlong[0].Aumid == "app0", overlong[0].Aumid);

        // ---- Editing the list from a menu ----

        var list = new List<SourcePreference> { Preference("a"), Preference("b"), Preference("c") };

        Check("moving the top entry up changes nothing", SourceSelector.MoveUp(list, 0) == 0 && Describe(list) == "a,b,c", Describe(list));
        Check("moving up swaps and reports the new index", SourceSelector.MoveUp(list, 2) == 1 && Describe(list) == "a,c,b", Describe(list));
        Check("moving the last entry down changes nothing", SourceSelector.MoveDown(list, 2) == 2 && Describe(list) == "a,c,b", Describe(list));
        Check("moving down swaps and reports the new index", SourceSelector.MoveDown(list, 0) == 1 && Describe(list) == "c,a,b", Describe(list));
        Check("out-of-range moves are refused", SourceSelector.MoveUp(list, 9) == 9, "moved");

        Check("removing an entry works", SourceSelector.Remove(list, "a") && Describe(list) == "c,b", Describe(list));
        Check("removing a missing entry reports failure", !SourceSelector.Remove(list, "zzz"), "reported success");

        SourceSelector.Promote(list, "brave", "Brave");
        Check("promoting a new app adds it at the front", Describe(list) == "brave,c,b", Describe(list));
        Check("promoting stores its label", list[0].Label == "Brave", list[0].Label);

        SourceSelector.Promote(list, "B", "ignored");
        Check("promoting an existing app moves it to the front", Describe(list) == "b,brave,c", Describe(list));
        Check("and keeps its stored label", list[0].Label == "b", list[0].Label);

        SourceSelector.Promote(list, "   ", "nothing");
        Check("promoting a blank app id is a no-op", Describe(list) == "b,brave,c", Describe(list));

        Check("lookup is case-insensitive", SourceSelector.IndexOf(list, "BRAVE") == 1, SourceSelector.IndexOf(list, "BRAVE"));

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "ALL CHECKS PASSED"
            : $"{_failures} CHECK(S) FAILED");

        return _failures == 0 ? 0 : 1;
    }

    private static SourceCandidate Candidate(
        string aumid,
        bool current = false,
        bool playing = true,
        bool advancing = true) =>
        new(aumid, aumid, aumid, current, playing, advancing);

    private static SourcePreference Preference(string aumid, string? label = null) =>
        new() { Aumid = aumid, Label = label ?? aumid };

    private static string Describe(IEnumerable<SourcePreference> priority) =>
        string.Join(",", priority.Select(e => e.Aumid));

    private static void Check(string what, bool ok, object detail)
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
