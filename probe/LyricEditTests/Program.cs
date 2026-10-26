using Overlyric.App;
using Overlyric.Lyrics;

namespace Overlyric.LyricEditTests;

/// <summary>
/// Checks the timing editor: selection, set start/end, per-line and whole-sheet nudges, the refusal
/// of impossible edits, undo/redo and its bound, and that edits never reach past zero.
/// </summary>
internal static class Program
{
    private static int _checks;
    private static int _failures;

    private static int Main()
    {
        SelectionChecks();
        StartEndChecks();
        NudgeChecks();
        ShiftChecks();
        HistoryChecks();

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ALL {_checks} CHECKS PASSED"
            : $"{_failures} OF {_checks} CHECK(S) FAILED");

        return _failures == 0 ? 0 : 1;
    }

    private static void SelectionChecks()
    {
        var empty = new LyricEditSession([]);
        Check("an empty sheet has no selection", empty.SelectedIndex == -1 && empty.SelectedLine is null, empty.SelectedIndex);
        Check("an empty sheet cannot be edited", !empty.SetStart(TimeSpan.Zero) && !empty.NudgeSelected(10) && !empty.ShiftAll(10), "");

        var session = NewSession();
        Check("a fresh session selects the first line", session.SelectedIndex == 0, session.SelectedIndex);
        session.Select(2);
        Check("a selected line can be changed", session.SelectedIndex == 2, session.SelectedIndex);
        session.Select(99);
        Check("selection is clamped to the sheet", session.SelectedIndex == 2, session.SelectedIndex);
        session.SelectRelative(-1);
        Check("relative selection moves by one", session.SelectedIndex == 1, session.SelectedIndex);
        session.SelectRelative(-5);
        Check("relative selection clamps at the start", session.SelectedIndex == 0, session.SelectedIndex);
    }

    private static void StartEndChecks()
    {
        var session = NewSession();
        session.Select(1);

        Check("setting a start marks the session dirty", session.SetStart(TimeSpan.FromSeconds(4)) && session.IsDirty, session.Message);
        Check("the edited line carries the new start", session.Lines[1].Start == TimeSpan.FromSeconds(4), session.Lines[1].Start);
        Check("the edit is reported in the toolbar message", session.Message?.Contains("starts at", StringComparison.Ordinal) == true, session.Message);

        session.Select(0);
        Check("a start cannot overtake the line's own end",
            !session.SetStart(TimeSpan.FromSeconds(30)), session.Message);
        Check("a refused edit leaves the line alone", session.Lines[0].Start == TimeSpan.FromSeconds(1), session.Lines[0].Start);

        session.Select(0);
        Check("a start before zero is clamped", session.SetStart(TimeSpan.FromSeconds(-5)) && session.Lines[0].Start == TimeSpan.Zero, session.Lines[0].Start);

        session.Select(2);
        Check("setting an end works", session.SetEnd(TimeSpan.FromSeconds(12)) && session.Lines[2].End == TimeSpan.FromSeconds(12), session.Lines[2].End);
        Check("an end before the start is refused", !session.SetEnd(TimeSpan.FromSeconds(1)), session.Message);
        Check("clearing an end works", session.SetEnd(null) && session.Lines[2].End is null, session.Lines[2].End);
        Check("clearing an absent end is a no-op", !session.SetEnd(null), "");
    }

    private static void NudgeChecks()
    {
        var session = NewSession();
        session.Select(0);
        var before = session.Lines[0].Start;

        Check("a nudge moves the selected line", session.NudgeSelected(250) && session.Lines[0].Start == before + TimeSpan.FromMilliseconds(250), session.Lines[0].Start);
        Check("a nudge moves the line's end too",
            session.Lines[0].End == TimeSpan.FromMilliseconds(4250), session.Lines[0].End);

        Check("a zero nudge does nothing", !session.NudgeSelected(0), "");

        session.Select(0);
        var lineAtZero = new LyricEditSession([new LyricLine(TimeSpan.Zero, "At zero")]);
        Check("a line at zero cannot move earlier", !lineAtZero.NudgeSelected(-250), lineAtZero.Message);
        Check("an impossible nudge is still a no-op", lineAtZero.Lines[0].Start == TimeSpan.Zero, lineAtZero.Lines[0].Start);

        var withEnd = new LyricEditSession([new LyricLine(TimeSpan.FromMilliseconds(100), "Short", TimeSpan.FromMilliseconds(200))]);
        Check("a nudge below zero keeps a positive span",
            withEnd.NudgeSelected(-250) && withEnd.Lines[0].Start == TimeSpan.Zero && withEnd.Lines[0].End > withEnd.Lines[0].Start,
            $"{withEnd.Lines[0].Start}/{withEnd.Lines[0].End}");
    }

    private static void ShiftChecks()
    {
        var session = NewSession();
        session.Select(1);

        Check("shift all moves every line", session.ShiftAll(1000)
            && session.Lines.All(line => line.Start >= TimeSpan.FromMilliseconds(1500)), session.Message);
        Check("shift all keeps the selection on the same line", session.SelectedIndex == 1, session.SelectedIndex);
        Check("shift all is reported as such", session.Message?.Contains("Every line", StringComparison.Ordinal) == true, session.Message);

        var clamped = new LyricEditSession([
            new LyricLine(TimeSpan.Zero, "Zero", TimeSpan.FromSeconds(1)),
            new LyricLine(TimeSpan.FromSeconds(5), "Five")
        ]);
        Check("shift all clamps at zero rather than going negative",
            clamped.ShiftAll(-2000) && clamped.Lines[0].Start == TimeSpan.Zero && clamped.Lines[0].End > TimeSpan.Zero,
            $"{clamped.Lines[0].Start}/{clamped.Lines[0].End}");

        var sorted = new LyricEditSession([
            new LyricLine(TimeSpan.FromSeconds(1), "One"),
            new LyricLine(TimeSpan.FromSeconds(5), "Two"),
            new LyricLine(TimeSpan.FromSeconds(9), "Three")
        ]);
        sorted.Select(1);
        sorted.ShiftAll(-3000);
        Check("shift all keeps the sheet sorted", sorted.Lines.Select(line => line.Start).SequenceEqual(sorted.Lines.Select(line => line.Start).Order()), string.Join(",", sorted.Lines.Select(line => line.Start)));
        Check("shift all keeps a line below zero clamped in place", sorted.Lines[0].Start == TimeSpan.Zero, sorted.Lines[0].Start);
    }

    private static void HistoryChecks()
    {
        var session = NewSession();
        session.Select(0);
        session.SetStart(TimeSpan.FromSeconds(2));

        Check("an edit can be undone", session.Undo() && session.Lines[0].Start == TimeSpan.FromSeconds(1), session.Lines[0].Start);
        Check("an undone edit can be redone", session.Redo() && session.Lines[0].Start == TimeSpan.FromSeconds(2), session.Lines[0].Start);

        session.Undo();
        session.SetEnd(TimeSpan.FromSeconds(3));
        Check("a new edit clears the redo history", !session.CanRedo, session.CanRedo);

        session.MarkSaved();
        Check("saving clears the dirty flag", !session.IsDirty, session.IsDirty);
        session.Undo();
        Check("undoing a saved edit makes it dirty again", session.IsDirty, session.IsDirty);

        var toOriginal = NewSession();
        toOriginal.Select(0);
        toOriginal.SetStart(TimeSpan.FromSeconds(2));
        toOriginal.Undo();
        Check("undoing back to the original sheet clears the dirty flag", !toOriginal.IsDirty, toOriginal.IsDirty);

        var bounded = NewSession();
        bounded.Select(0);

        for (var i = 1; i <= 150; i++)
        {
            bounded.SetStart(TimeSpan.FromMilliseconds(i * 10));
        }

        var undos = 0;
        while (bounded.CanUndo)
        {
            bounded.Undo();
            undos++;
        }

        Check("the undo history is bounded", undos <= 100, undos);
        Check("a long edit history still reaches the first state", bounded.Lines[0].Start == TimeSpan.FromSeconds(1), bounded.Lines[0].Start);

        var empty = new LyricEditSession([]);
        Check("undo and redo on an empty history are no-ops", !empty.Undo() && !empty.Redo(), "");
    }

    private static LyricEditSession NewSession() => new([
        new LyricLine(TimeSpan.FromSeconds(1), "One", TimeSpan.FromSeconds(4)),
        new LyricLine(TimeSpan.FromSeconds(5), "Two"),
        new LyricLine(TimeSpan.FromSeconds(9), "Three")
    ]);

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
