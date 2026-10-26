using Overlyric.Lyrics;

namespace Overlyric.App;

/// <summary>
/// The timing-editing state for one sheet: a bounded undo/redo history, one selected line, and the
/// operations the editor offers. Pure and window-free, so the rules can be checked without a desktop.
/// </summary>
/// <remarks>
/// <para>
/// Edits never touch the cached LRCLIB answer; saving writes a local selection (see
/// <c>LyricsService.TrySaveEdited</c>), and choosing Automatic returns the track to the network
/// answer. That is the same rule the local library uses for an imported file.
/// </para>
/// <para>
/// Lines are kept sorted by start time after every operation, because the renderer and the overlay
/// both assume that order. The selection follows the line that was edited rather than its index, so
/// moving a line past its neighbour does not silently move the selection to a different line.
/// </para>
/// </remarks>
internal sealed class LyricEditSession
{
    /// <summary>Enough undo steps for a whole sheet's worth of fixes, without unbounded memory.</summary>
    private const int MaxHistory = 100;

    private readonly List<LyricLine> _lines;
    private readonly Stack<EditState> _undo = new();
    private readonly Stack<EditState> _redo = new();
    private int _selected = -1;

    public LyricEditSession(IReadOnlyList<LyricLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        _lines = [.. lines];
        _selected = _lines.Count > 0 ? 0 : -1;
    }

    public IReadOnlyList<LyricLine> Lines => _lines;

    public int SelectedIndex => _selected;

    public LyricLine? SelectedLine => _selected >= 0 && _selected < _lines.Count ? _lines[_selected] : null;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>True once an edit has been made and not yet saved.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>The last refusal, or null after a successful operation. Shown in the toolbar.</summary>
    public string? Message { get; private set; }

    public bool HasLines => _lines.Count > 0;

    public void Select(int index)
    {
        if (_lines.Count == 0)
        {
            _selected = -1;
            return;
        }

        _selected = Math.Clamp(index, 0, _lines.Count - 1);
        Message = null;
    }

    public void SelectRelative(int delta) => Select(_selected < 0 ? 0 : _selected + delta);

    /// <summary>Stamps the selected line's start. A line whose end would no longer follow is refused.</summary>
    public bool SetStart(TimeSpan start)
    {
        if (SelectedLine is not { } line)
        {
            return false;
        }

        start = ClampStart(start);

        if (line.End is { } end && start >= end)
        {
            Message = "The start must be before this line's end.";
            return false;
        }

        PushUndo();
        ReplaceSelected(line with { Start = start });
        Message = $"Line {_selected + 1} starts at {Format(start)}.";
        return true;
    }

    /// <summary>Stamps the selected line's end. A null clears it, letting the next line's start decide.</summary>
    public bool SetEnd(TimeSpan? end)
    {
        if (SelectedLine is not { } line)
        {
            return false;
        }

        if (end is { } value)
        {
            value = ClampStart(value);

            if (value <= line.Start)
            {
                Message = "The end must be after the line's start.";
                return false;
            }

            PushUndo();
            ReplaceSelected(line with { End = value });
            Message = $"Line {_selected + 1} ends at {Format(value)}.";
            return true;
        }

        if (line.End is null)
        {
            Message = null;
            return false;
        }

        PushUndo();
        ReplaceSelected(line with { End = null });
        Message = $"Line {_selected + 1} has no explicit end.";
        return true;
    }

    /// <summary>Moves the selected line's start (and its end, when it has one) by a delta.</summary>
    public bool NudgeSelected(int deltaMs)
    {
        if (deltaMs == 0 || SelectedLine is not { } line)
        {
            return false;
        }

        var moved = Move(line, deltaMs);

        if (moved == line)
        {
            Message = "That line cannot move any further.";
            return false;
        }

        PushUndo();
        ReplaceSelected(moved);
        Message = $"Line {_selected + 1} moved by {FormatDelta(deltaMs)}.";
        return true;
    }

    /// <summary>Shifts every line by a delta, for a sheet that is uniformly early or late.</summary>
    public bool ShiftAll(int deltaMs)
    {
        if (deltaMs == 0 || _lines.Count == 0)
        {
            return false;
        }

        var anyMoved = false;
        var shifted = new List<LyricLine>(_lines.Count);

        foreach (var line in _lines)
        {
            var moved = Move(line, deltaMs);
            anyMoved |= moved != line;
            shifted.Add(moved);
        }

        if (!anyMoved)
        {
            Message = "No line can move any further in that direction.";
            return false;
        }

        var selected = _selected;

        PushUndo();
        _lines.Clear();
        _lines.AddRange(shifted);
        SortKeepingSelection(selected);
        Message = $"Every line shifted by {FormatDelta(deltaMs)}.";
        return true;
    }

    public bool Undo()
    {
        if (!UndoCore())
        {
            return false;
        }

        Message = "Undone.";
        return true;
    }

    public bool Redo()
    {
        if (!RedoCore())
        {
            return false;
        }

        Message = "Redone.";
        return true;
    }

    /// <summary>Called by the host once the edited sheet has been written; keeps the history.</summary>
    public void MarkSaved()
    {
        IsDirty = false;
        Message = "Saved as this track's local sheet.";
    }

    private bool UndoCore()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        _redo.Push(Capture());
        Restore(_undo.Pop());
        return true;
    }

    private bool RedoCore()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        _undo.Push(Capture());
        Restore(_redo.Pop());
        return true;
    }

    private void PushUndo()
    {
        _undo.Push(Capture());
        _redo.Clear();

        if (_undo.Count > MaxHistory)
        {
            // Stack<T> has no bounded push; rebuild without the oldest entry.
            var kept = _undo.ToArray();
            _undo.Clear();

            for (var i = kept.Length - 1; i >= 0 && _undo.Count < MaxHistory; i--)
            {
                _undo.Push(kept[i]);
            }
        }

        IsDirty = true;
    }

    private EditState Capture() => new([.. _lines], _selected);

    private void Restore(EditState state)
    {
        _lines.Clear();
        _lines.AddRange(state.Lines);
        _selected = Math.Clamp(state.Selected, -1, Math.Max(-1, _lines.Count - 1));
        IsDirty = true;
    }

    private void ReplaceSelected(LyricLine line)
    {
        var selected = _selected;
        _lines[selected] = line;
        SortKeepingSelection(selected);
    }

    /// <summary>
    /// Sorts by start time, keeping document order for equal starts, then re-finds the edited line.
    /// The edited line's identity is its pre-sort position, which is all that is needed here.
    /// </summary>
    private void SortKeepingSelection(int selectedBefore)
    {
        var indexed = new List<(LyricLine Line, int Order)>(_lines.Count);

        for (var i = 0; i < _lines.Count; i++)
        {
            indexed.Add((_lines[i], i));
        }

        indexed.Sort(static (a, b) =>
        {
            var byStart = a.Line.Start.CompareTo(b.Line.Start);
            return byStart != 0 ? byStart : a.Order.CompareTo(b.Order);
        });

        _lines.Clear();

        foreach (var entry in indexed)
        {
            _lines.Add(entry.Line);
        }

        for (var i = 0; i < indexed.Count; i++)
        {
            if (indexed[i].Order == selectedBefore)
            {
                _selected = i;
                return;
            }
        }

        _selected = Math.Clamp(selectedBefore, -1, _lines.Count - 1);
    }

    private static LyricLine Move(LyricLine line, int deltaMs)
    {
        var delta = TimeSpan.FromMilliseconds(deltaMs);
        var start = ClampStart(line.Start + delta);

        if (line.End is not { } end)
        {
            return line with { Start = start };
        }

        var movedEnd = ClampStart(end + delta);

        // A line with an end keeps its span: a nudge below zero clamps both, a negative span is fixed.
        if (movedEnd <= start)
        {
            movedEnd = start + TimeSpan.FromMilliseconds(1);
        }

        return line with { Start = start, End = movedEnd };
    }

    private static TimeSpan ClampStart(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static string Format(TimeSpan value) => value.ToString(@"m\:ss\.ff", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatDelta(int deltaMs) =>
        (deltaMs / 1000.0).ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture) + " s";

    private readonly record struct EditState(List<LyricLine> Lines, int Selected);
}
