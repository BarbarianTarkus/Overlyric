using System.Runtime.InteropServices;
using Overlyric.Interop;
using Overlyric.Lyrics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.App;

/// <summary>
/// The complete lyric sheet for the track that is playing: every line, its timestamp, and which line
/// the music is on.
/// </summary>
/// <remarks>
/// <para>
/// Read-only on purpose. The next stage for this window is editing the sheet and uploading it to
/// LRCLIB, and that is why it is built around <see cref="LyricSheet"/> - a parsed document with the
/// record identity - rather than around a formatted string. The scrolling, measuring and active-line
/// work is what an editor will reuse; the drawing code deliberately has no mutation path yet.
/// </para>
/// <para>
/// It does have one way to ask a different question. When the automatic lookup cannot find the song -
/// a player that publishes <c>Artist - Title</c> as the title is the common case - the window offers
/// its own lrclib search. The query is editable, the records it returns are listed here, and choosing
/// one pins it for the track just as the selector does. The text field is a single line with caret,
/// arrows, Home/End, Backspace/Delete and paste; a full editor (selection, undo, IME composition) is
/// deliberately out of scope for this window.
/// </para>
/// <para>
/// The viewer follows the track while it is open: each new lyric result replaces the sheet, and a new
/// record resets the scroll to the line being sung.
/// </para>
/// </remarks>
internal sealed class LyricViewerWindow : PanelWindow
{
    private const int ScrollbarControl = 1;
    private const int OpenSearchControl = 2;
    private const int SearchGoControl = 3;
    private const int SearchCloseControl = 4;
    private const int SearchFieldControl = 5;
    private const int SearchScrollbarControl = 6;
    private const int ExportControl = 7;
    private const int UndoControl = 8;
    private const int RedoControl = 9;
    private const int SetStartControl = 10;
    private const int SetEndControl = 11;
    private const int SaveControl = 12;
    private const int EditFileControl = 13;
    private const int SearchResultBase = 100;

    /// <summary>Nudge buttons are this base plus an index into <see cref="NudgeStepsMs"/>.</summary>
    private const int NudgeBase = 20;

    private static readonly int[] NudgeStepsMs = [-250, -50, -10, 10, 50, 250];

    private const float Pad = 28f;
    private const float HeaderHeight = 98f;
    private const float ListTop = 106f;
    private const float FooterHeight = 38f;

    /// <summary>The timing-editing toolbar sits between the list and the footer.</summary>
    private const float EditToolbarHeight = 40f;
    private const float TimestampLeft = 28f;
    private const float TimestampWidth = 62f;
    private const float TextLeft = 100f;
    private const float TextRightPadding = 52f;
    private const float ContentPaddingY = 8f;

    private const float ActiveFontSize = 16.5f;
    private const float InactiveFontSize = 14.5f;

    private const float SearchFieldHeight = 34f;
    private const float SearchFieldPadding = 12f;
    private const float SearchStatusTop = 148f;
    private const float SearchResultsTop = 174f;
    private const float SearchRowHeight = 44f;
    private const float SearchTextSize = 13f;
    private const int MaxQueryLength = 240;

    /// <summary>
    /// The search result columns are anchored to the right edge, like the selector's, so the badge
    /// stays next to the scrollbar and the title takes the width the window has. The constants are
    /// gaps and column widths rather than absolute lefts, because those are what must survive a
    /// resize.
    /// </summary>
    private const float SearchBadgeColumnWidth = 110f;
    private const float SearchColumnGap = 16f;
    private const float SearchMatchWidth = 70f;
    private const float SearchDurationWidth = 60f;

    private static readonly uint ColorReader = 0xFFC9D0DC;
    private static readonly uint ColorReaderActive = 0xFFF5F7FA;

    private sealed record DisplayLine(string Timestamp, string Text, bool Active, bool Selected);

    private readonly List<DisplayLine> _lines = [];
    private readonly List<float> _prefix = [];
    private readonly List<float> _heights = [];
    private readonly Dictionary<int, long> _resultRecords = [];

    private readonly Action<string> _search;
    private readonly Action<long> _chooseRecord;
    private readonly Action<IReadOnlyList<LyricLine>> _export;
    private readonly Action<IReadOnlyList<LyricLine>> _openExternal;
    private readonly Func<TimeSpan> _position;
    private readonly Func<IReadOnlyList<LyricLine>, string?> _saveEdited;

    private LyricSheet? _sheet;
    private LyricEditSession? _edit;
    private string? _editMessage;
    private bool _editSessionFresh;
    private bool _measured;
    private float _measuredWidth = -1f;
    private bool _scrollToActive;
    private float _contentHeight;
    private float _scroll;
    private int _activeIndex = -1;

    private bool _searchOpen;
    private string _query = "";
    private int _caret;
    private float _queryOffset;
    private Rect _fieldRect;
    private int _searchScrollRow;
    private int _visibleSearchRows;
    private int _maxSearchRow;

    public LyricViewerWindow(
        Action<string> search,
        Action<long> chooseRecord,
        Action<IReadOnlyList<LyricLine>> export,
        Action<IReadOnlyList<LyricLine>> openExternal,
        Func<TimeSpan> position,
        Func<IReadOnlyList<LyricLine>, string?> saveEdited)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(chooseRecord);
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(openExternal);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(saveEdited);

        _search = search;
        _chooseRecord = chooseRecord;
        _export = export;
        _openExternal = openExternal;
        _position = position;
        _saveEdited = saveEdited;
    }

    protected override float DefaultWidthDip => 900f;

    protected override float DefaultHeightDip => 620f;

    protected override float MinWidthDip => 640f;

    protected override float MinHeightDip => 400f;

    protected override string Title => "Lyrics";

    private float ListBottom => HeightDip - FooterHeight - EditToolbarHeight;

    private float EditToolbarTop => HeightDip - FooterHeight - EditToolbarHeight + 4f;

    /// <summary>Where the footer starts: below the timing toolbar, not below the list.</summary>
    private float FooterTop => ListBottom + EditToolbarHeight;

    private float TextWidth => WidthDip - TextLeft - TextRightPadding;

    private float SearchDurationLeft =>
        WidthDip - 52f - SearchBadgeColumnWidth - SearchColumnGap - SearchDurationWidth;

    private float SearchMatchLeft => SearchDurationLeft - SearchColumnGap - SearchMatchWidth;

    private float SearchTitleWidth => MathF.Max(80f, SearchMatchLeft - (Pad + 36f) - 16f);

    /// <summary>Open the window on a sheet, scrolled to the line being sung.</summary>
    public void ShowSheet(LyricSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        _sheet = sheet;
        RebuildEdit(sheet);
        _measured = false;
        _scrollToActive = true;
        _scroll = 0f;
        _searchOpen = false;
        ResetQuery(sheet);

        ShowPanel();
    }

    /// <summary>
    /// Replace the document while the window is open. The scroll is preserved when the same record is
    /// still being shown, and jumps back to the active line when a different one arrives.
    /// </summary>
    public void UpdateSheet(LyricSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        var changed = !string.Equals(_sheet?.Key, sheet.Key, StringComparison.Ordinal);
        var newTrack = _sheet is null || _sheet.Revision != sheet.Revision;

        _sheet = sheet;

        if (newTrack)
        {
            // The query belongs to a song: keep it while the same one is playing, and start over when
            // the track changes.
            _searchOpen = false;
            ResetQuery(sheet);
        }

        // Keep unsaved edits on screen while the same track is playing; a new track, or an answer
        // that arrives when the sheet is not being edited, replaces the document as before.
        if (newTrack || (_edit is null && sheet.Lines.Count > 0) || (changed && _edit is { IsDirty: false }))
        {
            RebuildEdit(sheet);
        }

        if (changed)
        {
            _measured = false;
            _scrollToActive = true;
        }

        if (IsOpen)
        {
            Invalidate();
        }
    }

    /// <summary>
    /// Start editing from a sheet. The session keeps its own copy of the lines, so the cached answer
    /// is never mutated; saving writes a local selection instead.
    /// </summary>
    private void RebuildEdit(LyricSheet sheet)
    {
        _edit = sheet.Lines.Count > 0 ? new LyricEditSession(sheet.Lines) : null;
        _editSessionFresh = _edit is not null;
        _editMessage = null;
    }

    private void DrawEditToolbar(ID2D1DeviceContext context)
    {
        Fill(context, R(Pad, EditToolbarTop - 5f, WidthDip - Pad, EditToolbarTop - 4f), ColorLine);

        const float height = 26f;
        var top = EditToolbarTop + 4f;
        var enabled = _edit is not null;

        // The full set of buttons is 608 DIP wide; below the default width they shrink so the
        // toolbar fits inside the window padding, and the selection label moves to the footer.
        var compact = WidthDip < 780f;
        var nudgeWidth = compact ? 36f : 42f;
        var buttonWidth = compact ? 44f : 48f;
        var saveWidth = compact ? 48f : 52f;
        var setStartWidth = compact ? 66f : 76f;
        var setEndWidth = compact ? 56f : 66f;

        var right = WidthDip - Pad;

        right = EditButton(context, SaveControl, "Save", right, top, saveWidth, height, enabled && _edit!.IsDirty);
        right = EditButton(context, RedoControl, "Redo", right, top, buttonWidth, height, enabled && _edit!.CanRedo);
        right = EditButton(context, UndoControl, "Undo", right, top, buttonWidth, height, enabled && _edit!.CanUndo);
        right = EditButton(context, SetEndControl, "Set end", right, top, setEndWidth, height, enabled);
        right = EditButton(context, SetStartControl, "Set start", right, top, setStartWidth, height, enabled);

        for (var i = NudgeStepsMs.Length - 1; i >= 0; i--)
        {
            var step = NudgeStepsMs[i];
            var text = (step > 0 ? "+" : string.Empty) + step.ToString(System.Globalization.CultureInfo.InvariantCulture);
            right = EditButton(context, NudgeBase + i, text, right, top, nudgeWidth, height, enabled);
        }

        // The selection label is only drawn when it has room; the footer always carries it.
        var infoWidth = right - Pad - 8f;

        if (infoWidth >= 130f)
        {
            var info = _edit is null
                ? "Timing editor"
                : _edit.SelectedLine is { } selected
                    ? $"Line {_edit.SelectedIndex + 1}/{_edit.Lines.Count} \u00b7 {LyricText.FormatTimestamp(selected.Start)}"
                    : "No line selected";

            Label(context, info, Pad, top, infoWidth, height, 11.5f, FontWeight.SemiBold, ColorTextDim);
        }
    }

    /// <summary>Places one toolbar button against its right edge and returns the next right edge.</summary>
    private float EditButton(
        ID2D1DeviceContext context,
        int id,
        string text,
        float right,
        float top,
        float width,
        float height,
        bool enabled)
    {
        var left = right - width;
        Button(context, id, text, R(left, top, left + width, top + height), enabled);
        return left - 6f;
    }

    private void NudgeEdit(int deltaMs)
    {
        if (_edit is null)
        {
            return;
        }

        var everyLine = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        var changed = everyLine ? _edit.ShiftAll(deltaMs) : _edit.NudgeSelected(deltaMs);

        if (changed)
        {
            _measured = false;
            EnsureSelectedVisible();
        }

        _editMessage = _edit.Message;
        Invalidate();
    }

    private void SetStartToNow()
    {
        if (_edit is null)
        {
            return;
        }

        if (_edit.SetStart(_position()))
        {
            _measured = false;
            EnsureSelectedVisible();
        }

        _editMessage = _edit.Message;
        Invalidate();
    }

    private void SetEndToNow()
    {
        if (_edit is null)
        {
            return;
        }

        if (_edit.SetEnd(_position()))
        {
            _measured = false;
            EnsureSelectedVisible();
        }

        _editMessage = _edit.Message;
        Invalidate();
    }

    private void SaveEdits()
    {
        if (_edit is null || !_edit.IsDirty)
        {
            return;
        }

        var error = _saveEdited(_edit.Lines);

        if (error is null)
        {
            _edit.MarkSaved();
        }

        _editMessage = error ?? _edit.Message;
        Invalidate();
    }

    private void AfterHistoryMove()
    {
        if (_edit is null)
        {
            return;
        }

        _measured = false;
        EnsureSelectedVisible();
        _editMessage = _edit.Message;
        Invalidate();
    }

    /// <summary>
    /// Ctrl+C copies the selected (or active) line; Ctrl+Shift+C copies the whole sheet as LRC. The
    /// clipboard is the one place a lyric is useful outside this window, and both forms are what
    /// listeners actually paste into a message or a bug report.
    /// </summary>
    private void CopyLyrics()
    {
        var sheet = _sheet;

        if (sheet is null || sheet.Lines.Count == 0)
        {
            return;
        }

        var lines = _edit?.Lines ?? sheet.Lines;
        var wholeSheet = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        string text;

        if (wholeSheet)
        {
            text = LyricTextCodec.EncodeLrc(
                sheet.Artist,
                sheet.Title,
                sheet.Album,
                sheet.DurationSeconds,
                lines);
        }
        else
        {
            var index = _edit?.SelectedIndex ?? _activeIndex;

            if (index < 0 || index >= lines.Count)
            {
                return;
            }

            var line = lines[index].Text.Trim();
            text = line.Length == 0 ? " " : line;
        }

        _editMessage = Clipboard.WriteText(Handle, text)
            ? wholeSheet ? "Copied the whole sheet as LRC." : "Copied the selected line."
            : "The clipboard is busy; try again.";

        Invalidate();
    }

    /// <summary>Scrolls just enough that the selected line is fully inside the list viewport.</summary>
    private void EnsureSelectedVisible()
    {
        var index = _edit?.SelectedIndex ?? -1;

        if (index < 0 || index >= _prefix.Count)
        {
            return;
        }

        var viewport = ListBottom - ListTop;
        var top = ListTop + ContentPaddingY + _prefix[index] - _scroll;
        var bottom = top + _heights[index];

        if (top < ListTop)
        {
            _scroll = MathF.Max(0f, _prefix[index] + ContentPaddingY - 4f);
        }
        else if (bottom > ListBottom)
        {
            _scroll += bottom - ListBottom;
        }

        _scroll = Math.Clamp(_scroll, 0f, MathF.Max(0f, _contentHeight - viewport));
    }

    /// <summary>Maps a click in the list band onto a line; used to select the line being edited.</summary>
    private void SelectLineAt(float y)
    {
        if (_edit is null || _prefix.Count == 0)
        {
            return;
        }

        var contentY = y - ListTop + _scroll - ContentPaddingY;

        for (var i = 0; i < _prefix.Count; i++)
        {
            if (contentY >= _prefix[i] && contentY < _prefix[i] + _heights[i])
            {
                _edit.Select(i);
                Invalidate();
                return;
            }
        }
    }

    protected override void DrawBody(ID2D1DeviceContext context)
    {
        var sheet = _sheet;

        DrawHeader(context, sheet);

        if (_searchOpen)
        {
            DrawSearch(context, sheet);
            DrawFooter(context, sheet);
            return;
        }

        if (sheet is null || sheet.Lines.Count == 0)
        {
            DrawEmpty(context, sheet);
            DrawFooter(context, sheet);
            return;
        }

        MeasureIfNeeded(sheet);

        var viewport = ListBottom - ListTop;
        var maxScroll = Math.Max(0f, _contentHeight - viewport);
        _scroll = Math.Clamp(_scroll, 0f, maxScroll);

        context.PushAxisAlignedClip(
            R(0f, ListTop, WidthDip, ListBottom),
            AntialiasMode.Aliased);

        var y = ListTop + ContentPaddingY - _scroll;

        for (var i = 0; i < _lines.Count; i++)
        {
            var height = _heights[i];
            var top = y + _prefix[i];

            if (top + height < ListTop)
            {
                continue;
            }

            if (top > ListBottom)
            {
                break;
            }

            DrawLine(context, _lines[i], top, height);
        }

        context.PopAxisAlignedClip();

        if (maxScroll > 0f)
        {
            Scrollbar(
                context,
                WidthDip - 18f,
                ListTop,
                viewport,
                viewport,
                _contentHeight,
                _scroll,
                ColorAccent);

            Hit(ScrollbarControl, R(WidthDip - 24f, ListTop, WidthDip - 10f, ListBottom));
        }

        DrawEditToolbar(context);
        DrawFooter(context, sheet);
    }

    private void DrawHeader(ID2D1DeviceContext context, LyricSheet? sheet)
    {
        if (sheet is null || !sheet.HasTrack)
        {
            Label(context, "Nothing is playing", Pad, 16f, WidthDip - (2 * Pad), 32f, 22f, FontWeight.SemiBold, ColorText);
            Label(
                context,
                "Overlyric shows the words for whatever Windows says is playing.",
                Pad,
                48f,
                WidthDip - (2 * Pad),
                20f,
                12.5f,
                FontWeight.Normal,
                ColorTextDim);
            Fill(context, R(Pad, HeaderHeight - 1f, WidthDip - Pad, HeaderHeight), ColorLine);
            return;
        }

        var title = LyricText.Fit(sheet.Title.Length > 0 ? sheet.Title : sheet.Artist, 72);
        Label(context, title, Pad, 10f, WidthDip - (2 * Pad), 32f, 22f, FontWeight.SemiBold, ColorText);

        var meta = BuildMeta(sheet);
        Label(context, meta, Pad, 42f, WidthDip - (2 * Pad), 20f, 12.5f, FontWeight.Normal, ColorTextDim);

        var badge = LyricBadges.ForSheet(
            sheet.IsComplete,
            sheet.IsInstrumental,
            sheet.LineCount > 0,
            sheet.IsSynced,
            sheet.HasWordTimings);

        var (foreground, background) = BadgeColors(badge.Tone);
        var right = WidthDip - Pad;
        right = Badge(context, badge.Label, right, 26f, foreground, background) - 8f;

        if (sheet.Source == LyricSource.Local)
        {
            right = Badge(context, LyricBadges.Local.Label, right, 26f, ColorAccent, 0x2A7CC4FF) - 8f;
        }

        if (sheet.LineCount > 0)
        {
            Badge(context, LyricText.FormatLineCount(sheet.LineCount), right, 26f, ColorTextDim, 0x1AFFFFFF);
        }

        var record = sheet.Source switch
        {
            LyricSource.Local => "local file",
            LyricSource.Pinned => sheet.RecordId is { } pinnedId ? $"pinned lrclib #{pinnedId}" : "pinned record",
            _ => sheet.RecordId is { } id ? $"lrclib #{id}" : "No record"
        };

        Label(
            context,
            record,
            WidthDip - Pad - 240f,
            46f,
            240f,
            18f,
            11f,
            FontWeight.Normal,
            ColorTextFaint,
            TextAlignment.Trailing);

        // The status used to float on the right, twenty pixels under the record id, in the same
        // corner. As a strip under the header it cannot collide with anything, and it only occupies
        // space when there is something to say.
        if (!string.IsNullOrWhiteSpace(sheet.Status))
        {
            FillRound(context, Pad, 70f, WidthDip - (2 * Pad), 22f, 6f, ColorControl);
            FillRound(context, Pad, 70f, 3f, 22f, 1.5f, ColorWarning);
            Label(
                context,
                LyricText.Fit(sheet.Status, 110),
                Pad + 12f,
                70f,
                WidthDip - (2 * Pad) - 24f,
                22f,
                11.5f,
                FontWeight.Normal,
                ColorWarning);
        }

        Fill(context, R(Pad, HeaderHeight - 1f, WidthDip - Pad, HeaderHeight), ColorLine);
    }

    private static string BuildMeta(LyricSheet sheet)
    {
        var parts = new List<string>(4);

        if (sheet.Artist.Length > 0)
        {
            parts.Add(sheet.Artist);
        }

        if (!string.IsNullOrWhiteSpace(sheet.Album))
        {
            parts.Add(sheet.Album!);
        }

        if (sheet.DurationSeconds is > 0)
        {
            parts.Add(LyricText.FormatDuration(sheet.DurationSeconds));
        }

        if (!string.IsNullOrWhiteSpace(sheet.SourceApp))
        {
            parts.Add(sheet.SourceApp!);
        }

        return parts.Count == 0 ? "Unknown track" : string.Join(" \u00b7 ", parts);
    }

    /// <summary>
    /// Re-wrap the sheet whenever the width changes. Wrapped line heights are the one part of this
    /// window's layout that depends on the window size, so a resize has to throw the measurements
    /// away; the height only moves the viewport, which the scroll clamp already handles.
    /// </summary>
    private void MeasureIfNeeded(LyricSheet sheet)
    {
        var width = TextWidth;

        if (_measured && MathF.Abs(_measuredWidth - width) < 0.5f)
        {
            return;
        }

        _measured = true;
        _measuredWidth = width;
        _lines.Clear();
        _prefix.Clear();
        _heights.Clear();

        var source = _edit?.Lines ?? sheet.Lines;
        _activeIndex = sheet.IsSynced ? LyricText.ActiveLine(source, sheet.Position) : -1;

        if (_editSessionFresh && _edit is not null)
        {
            _edit.Select(_activeIndex >= 0 && _activeIndex < source.Count ? _activeIndex : 0);
            _editSessionFresh = false;
        }

        var selected = _edit?.SelectedIndex ?? -1;
        var total = 0f;

        for (var i = 0; i < source.Count; i++)
        {
            var line = source[i];
            var active = i == _activeIndex;
            var size = active ? ActiveFontSize : InactiveFontSize;
            var weight = active ? FontWeight.SemiBold : FontWeight.Normal;
            var text = line.Text.Length == 0 ? " " : line.Text;
            var height = MathF.Max(26f, MeasureHeight(text, TextWidth, size, weight) + 12f);

            _lines.Add(new DisplayLine(
                sheet.IsSynced ? LyricText.FormatTimestamp(line.Start) : "",
                text,
                active,
                i == selected));

            _prefix.Add(total);
            _heights.Add(height);
            total += height;
        }

        _contentHeight = total + (ContentPaddingY * 2f);

        if (_scrollToActive && _activeIndex >= 0 && _activeIndex < _prefix.Count)
        {
            var viewport = ListBottom - ListTop;
            _scroll = Math.Clamp(_prefix[_activeIndex] - (viewport * 0.35f), 0f, Math.Max(0f, _contentHeight - viewport));
        }

        _scrollToActive = false;
    }

    private void DrawLine(ID2D1DeviceContext context, DisplayLine line, float top, float height)
    {
        // The selected row is the editing cursor: it stays marked while playback highlights another
        // line, so "what I am changing" and "what is being sung" cannot be confused.
        if (line.Selected)
        {
            FillRound(context, TextLeft - 12f, top + 2f, TextWidth + 18f, height - 4f, 8f, 0x2E2F4A66);
            FillRound(context, TextLeft - 24f, top + 6f, 3f, height - 12f, 1.5f, ColorTextDim);
        }

        if (line.Active)
        {
            FillRound(
                context,
                TextLeft - 12f,
                top + 2f,
                TextWidth + 18f,
                height - 4f,
                8f,
                0x1F7CC4FF);

            FillRound(context, TextLeft - 24f, top + 6f, 3f, height - 12f, 1.5f, ColorAccent);
        }

        if (line.Timestamp.Length > 0)
        {
            Label(
                context,
                line.Timestamp,
                TimestampLeft,
                top,
                TimestampWidth,
                height,
                11.5f,
                FontWeight.Normal,
                line.Active ? ColorAccent : ColorTextFaint,
                TextAlignment.Trailing);
        }

        Label(
            context,
            line.Text,
            TextLeft,
            top + 2f,
            TextWidth,
            height - 4f,
            line.Active ? ActiveFontSize : InactiveFontSize,
            line.Active ? FontWeight.SemiBold : FontWeight.Normal,
            line.Active ? ColorReaderActive : ColorReader,
            TextAlignment.Leading,
            ParagraphAlignment.Near,
            WordWrapping.Wrap);
    }

    private void DrawEmpty(ID2D1DeviceContext context, LyricSheet? sheet)
    {
        var center = (ListTop + ListBottom) / 2f;

        var title = sheet switch
        {
            null => "No lyric sheet",
            { IsComplete: false } => "Looking up lyrics\u2026",
            { IsInstrumental: true } => "Instrumental",
            { Lines.Count: 0 } => sheet.Status ?? "No lyrics found",
            _ => "No lyrics found"
        };

        var detail = sheet switch
        {
            null => "Open the overlay and choose Lyrics.",
            { IsComplete: false } => "lrclib has not answered yet. This window will update by itself.",
            { IsInstrumental: true } => "lrclib says there is nothing to sing on this track.",
            { Lines.Count: 0 } => "Try Choose lyrics in the overlay, or search lrclib with a different query.",
            _ => ""
        };

        Label(
            context,
            title,
            Pad,
            center - 34f,
            WidthDip - (2 * Pad),
            30f,
            18f,
            FontWeight.SemiBold,
            ColorText,
            TextAlignment.Center);

        Label(
            context,
            detail,
            Pad,
            center,
            WidthDip - (2 * Pad),
            48f,
            12.5f,
            FontWeight.Normal,
            ColorTextDim,
            TextAlignment.Center,
            ParagraphAlignment.Near,
            WordWrapping.Wrap);

        if (sheet is { HasTrack: true })
        {
            const float searchWidth = 150f;

            var left = (WidthDip - searchWidth) / 2f;

            Button(
                context,
                OpenSearchControl,
                "Search LRCLIB",
                R(left, center + 48f, left + searchWidth, center + 78f),
                !sheet.IsDemo);
        }
    }

    private void DrawFooter(ID2D1DeviceContext context, LyricSheet? sheet)
    {
        Fill(context, R(Pad, FooterTop, WidthDip - Pad, FooterTop + 1f), ColorLine);

        if (_searchOpen)
        {
            Label(
                context,
                "Enter searches \u00b7 Esc returns to the sheet \u00b7 Click a record to use it.",
                Pad,
                FooterTop + 9f,
                WidthDip - (2 * Pad) - 200f,
                20f,
                11f,
                FontWeight.Normal,
                ColorTextFaint);

            var count = sheet?.SearchResults.Count ?? 0;
            var right = count > 0 ? $"{count} {(count == 1 ? "record" : "records")}" : "Esc closes";

            Label(
                context,
                right,
                WidthDip - Pad - 180f,
                FooterTop + 9f,
                180f,
                20f,
                11f,
                FontWeight.Normal,
                ColorTextFaint,
                TextAlignment.Trailing);

            return;
        }

        if (sheet is { HasTrack: true, LineCount: > 0 })
        {
            const float buttonWidth = 116f;
            const float exportWidth = 90f;
            const float editWidth = 88f;
            var editLeft = Pad + buttonWidth + 8f + exportWidth + 8f;

            Button(
                context,
                OpenSearchControl,
                "Search LRCLIB",
                R(Pad, FooterTop + 7f, Pad + buttonWidth, FooterTop + 31f),
                !sheet.IsDemo);

            Button(
                context,
                ExportControl,
                "Export\u2026",
                R(Pad + buttonWidth + 8f, FooterTop + 7f, Pad + buttonWidth + 8f + exportWidth, FooterTop + 31f),
                sheet.IsSynced && !sheet.IsDemo);

            Button(
                context,
                EditFileControl,
                "Edit file\u2026",
                R(editLeft, FooterTop + 7f, editLeft + editWidth, FooterTop + 31f),
                !sheet.IsDemo);

            // Below the default width the selection prefix and the help sentence are dropped: there
            // is room for one short sentence (usually the last operation's result) and no more, and
            // the Label draws without wrapping, so an over-long hint would run under the line count.
            var compact = WidthDip < 800f;
            var selection = !compact && _edit?.SelectedLine is { } selected
                ? $"Line {_edit.SelectedIndex + 1}/{_edit.Lines.Count} \u00b7 {LyricText.FormatTimestamp(selected.Start)} - "
                : string.Empty;

            var help = _edit is not null
                ? selection + (_editMessage ?? "Up/Down selects a line; Shift+click a nudge shifts every line; Ctrl+Z/Y undo/redo.")
                : sheet.IsDemo
                    ? "Demo mode: the canned track is not sent to lrclib."
                    : sheet.IsSynced
                        ? "Ctrl+F searches lrclib with your own words."
                        : "This sheet has no timings, so there is nothing to export yet.";

            var hintLeft = editLeft + editWidth + 10f;
            var hintWidth = MathF.Max(0f, WidthDip - Pad - 190f - hintLeft);
            var maxChars = Math.Max(6, (int)(hintWidth / 5.6f));
            var hint = help.Length <= maxChars ? help : help[..(maxChars - 1)] + "\u2026";

            if (hintWidth > 20f)
            {
                Label(
                    context,
                    hint,
                    hintLeft,
                    FooterTop + 9f,
                    hintWidth,
                    20f,
                    11f,
                    FontWeight.Normal,
                    ColorTextFaint);
            }
        }

        // The empty state carries its own Search LRCLIB button, so the footer only has to say how
        // to leave the window.

        var rightText = sheet is { LineCount: > 0 } ? $"{sheet.LineCount} lines" : "Esc closes";

        Label(
            context,
            rightText,
            WidthDip - Pad - 180f,
            FooterTop + 9f,
            180f,
            20f,
            11f,
            FontWeight.Normal,
            ColorTextFaint,
            TextAlignment.Trailing);
    }

    // ---- manual search --------------------------------------------------------------------

    private void DrawSearch(ID2D1DeviceContext context, LyricSheet? sheet)
    {
        const float goWidth = 88f;
        const float backWidth = 76f;

        var left = Pad;
        var right = WidthDip - Pad - goWidth - 8f - backWidth - 8f;
        var top = ListTop;

        _fieldRect = R(left, top, right, top + SearchFieldHeight);

        FillRound(context, left, top, right - left, SearchFieldHeight, 8f, ColorControl);
        Fill(context, R(left, top + SearchFieldHeight - 1f, right, top + SearchFieldHeight), ColorAccent);

        var textLeft = left + SearchFieldPadding;
        var available = right - textLeft - SearchFieldPadding;
        var caretWidth = Measure(_query[.._caret], SearchTextSize, FontWeight.Normal);
        _queryOffset = MathF.Max(0f, caretWidth - available);

        context.PushAxisAlignedClip(_fieldRect, AntialiasMode.Aliased);

        if (_query.Length == 0)
        {
            Label(
                context,
                "Artist - title",
                textLeft,
                top,
                available,
                SearchFieldHeight,
                SearchTextSize,
                FontWeight.Normal,
                ColorTextFaint);
        }
        else
        {
            var textWidth = Measure(_query, SearchTextSize, FontWeight.Normal);

            Label(
                context,
                _query,
                textLeft - _queryOffset,
                top,
                MathF.Max(available, textWidth + 2f),
                SearchFieldHeight,
                SearchTextSize,
                FontWeight.Normal,
                ColorText);
        }

        var caretX = textLeft - _queryOffset + caretWidth;
        Fill(context, R(caretX, top + 8f, caretX + 1f, top + SearchFieldHeight - 8f), ColorAccent);
        context.PopAxisAlignedClip();

        Hit(SearchFieldControl, _fieldRect);

        var searching = sheet is { SearchLoading: true };

        var searchLeft = right + 8f;
        var backLeft = searchLeft + goWidth + 8f;

        Button(
            context,
            SearchGoControl,
            "Search",
            R(searchLeft, top, searchLeft + goWidth, top + SearchFieldHeight),
            _query.Trim().Length > 0 && !searching);

        Button(
            context,
            SearchCloseControl,
            "Back",
            R(backLeft, top, backLeft + backWidth, top + SearchFieldHeight),
            true);

        var resultCount = sheet?.SearchResults.Count ?? 0;

        var status = sheet switch
        {
            null => "Nothing is playing.",
            { SearchLoading: true } => "Asking lrclib\u2026",
            { SearchMessage: { Length: > 0 } message } => message,
            _ when resultCount > 0 => $"{resultCount} records \u00b7 best fit first",
            _ => "Type a query and press Enter. The artist and title are a starting point, not a rule."
        };

        var statusColor = ColorTextFaint;

        if (sheet is { SearchLoading: true })
        {
            statusColor = ColorAccent;
        }
        else if (sheet is { SearchMessage: { Length: > 0 } })
        {
            statusColor = ColorWarning;
        }

        Label(
            context,
            status,
            Pad,
            SearchStatusTop,
            WidthDip - (2 * Pad),
            20f,
            11.5f,
            FontWeight.Normal,
            statusColor);

        DrawSearchResults(context, sheet);
    }

    private void DrawSearchResults(ID2D1DeviceContext context, LyricSheet? sheet)
    {
        var top = SearchResultsTop;
        var bottom = ListBottom;
        var height = bottom - top;

        _visibleSearchRows = Math.Max(1, (int)MathF.Floor(height / SearchRowHeight));
        _maxSearchRow = 0;
        _resultRecords.Clear();

        if (sheet is null || !sheet.HasTrack)
        {
            DrawCenterMessage(context, "Nothing is playing", "Open a track and search again.", top, bottom);
            return;
        }

        if (sheet.SearchResults.Count == 0)
        {
            if (sheet.SearchLoading)
            {
                DrawCenterMessage(context, "Searching lrclib\u2026", "The answer appears here as soon as it arrives.", top, bottom);
            }
            else
            {
                DrawCenterMessage(
                    context,
                    sheet.SearchMessage ?? "No search yet",
                    "Edit the query above, then press Enter or click Search.",
                    top,
                    bottom);
            }

            return;
        }

        var results = sheet.SearchResults;
        _maxSearchRow = Math.Max(0, results.Count - _visibleSearchRows);
        _searchScrollRow = Math.Clamp(_searchScrollRow, 0, _maxSearchRow);

        var y = top;

        for (var slot = 0; slot < _visibleSearchRows; slot++)
        {
            var index = _searchScrollRow + slot;

            if (index >= results.Count)
            {
                break;
            }

            DrawSearchRow(context, sheet, results[index], index, y);
            y += SearchRowHeight;
        }

        if (_maxSearchRow > 0)
        {
            Scrollbar(
                context,
                WidthDip - 18f,
                top,
                height,
                _visibleSearchRows,
                results.Count,
                _searchScrollRow,
                ColorAccent);

            Hit(SearchScrollbarControl, R(WidthDip - 24f, top, WidthDip - 10f, bottom));
        }
    }

    private void DrawSearchRow(
        ID2D1DeviceContext context,
        LyricSheet sheet,
        LyricsCandidate candidate,
        int index,
        float top)
    {
        var id = SearchResultBase + index;
        _resultRecords[id] = candidate.Id;

        var row = R(Pad, top + 2f, WidthDip - 44f, top + SearchRowHeight - 2f);
        var inUse = candidate.Id == sheet.RecordId;

        if (inUse)
        {
            FillRound(context, row.Left, row.Top, row.Right - row.Left, row.Bottom - row.Top, 9f, ColorSelected);
            FillRound(context, row.Left + 4f, row.Top + 7f, 3f, SearchRowHeight - 18f, 1.5f, ColorAccent);
        }
        else if (HoverId == id)
        {
            FillRound(context, row.Left, row.Top, row.Right - row.Left, row.Bottom - row.Top, 9f, ColorControlHot);
        }

        Label(
            context,
            (index + 1).ToString("00"),
            Pad + 8f,
            top + 4f,
            20f,
            24f,
            10.5f,
            FontWeight.Normal,
            ColorTextFaint,
            TextAlignment.Center);

        var title = string.IsNullOrWhiteSpace(candidate.TrackName) ? "Untitled record" : candidate.TrackName!;
        Label(
            context,
            LyricText.Fit(title, 52),
            Pad + 36f,
            top + 4f,
            SearchTitleWidth,
            20f,
            13f,
            FontWeight.SemiBold,
            ColorText);

        Label(
            context,
            LyricText.Fit(BuildResultSubtitle(candidate, inUse), 78),
            Pad + 36f,
            top + 24f,
            SearchTitleWidth,
            16f,
            10.5f,
            FontWeight.Normal,
            ColorTextDim);

        var delta = LyricText.FormatDurationDelta(candidate.DurationSeconds, sheet.DurationSeconds);
        Label(
            context,
            delta,
            SearchMatchLeft,
            top + 4f,
            70f,
            SearchRowHeight - 8f,
            11.5f,
            FontWeight.SemiBold,
            delta == "matches" ? ColorGood : ColorTextDim,
            TextAlignment.Trailing);

        Label(
            context,
            LyricText.FormatDuration(candidate.DurationSeconds),
            SearchDurationLeft,
            top + 4f,
            60f,
            SearchRowHeight - 8f,
            11.5f,
            FontWeight.Normal,
            ColorTextDim,
            TextAlignment.Trailing);

        var badge = LyricBadges.ForCandidate(candidate);
        var (foreground, background) = BadgeColors(badge.Tone);
        Badge(context, badge.Label, WidthDip - 52f, top + (SearchRowHeight / 2f), foreground, background);

        Hit(id, row);
    }

    private static string BuildResultSubtitle(LyricsCandidate candidate, bool inUse)
    {
        var parts = new List<string>(5);

        if (!string.IsNullOrWhiteSpace(candidate.ArtistName))
        {
            parts.Add(candidate.ArtistName!);
        }

        if (!string.IsNullOrWhiteSpace(candidate.AlbumName))
        {
            parts.Add(candidate.AlbumName!);
        }

        parts.Add($"#{candidate.Id}");

        if (inUse)
        {
            parts.Add("in use");
        }

        return string.Join(" \u00b7 ", parts);
    }

    private void DrawCenterMessage(
        ID2D1DeviceContext context,
        string title,
        string detail,
        float top,
        float bottom)
    {
        var center = (top + bottom) / 2f;

        Label(
            context,
            title,
            Pad,
            center - 28f,
            WidthDip - (2 * Pad),
            26f,
            15f,
            FontWeight.SemiBold,
            ColorText,
            TextAlignment.Center);

        Label(
            context,
            detail,
            Pad,
            center + 2f,
            WidthDip - (2 * Pad),
            40f,
            12f,
            FontWeight.Normal,
            ColorTextFaint,
            TextAlignment.Center,
            ParagraphAlignment.Near,
            WordWrapping.Wrap);
    }

    private void OpenSearch()
    {
        if (_sheet is { IsDemo: true })
        {
            return;
        }

        _searchOpen = true;
        _searchScrollRow = 0;
        _caret = _query.Length;
        ClearHover();
        Invalidate();
    }

    private void CloseSearch()
    {
        _searchOpen = false;
        ClearHover();
        Invalidate();
    }

    private void RunSearch()
    {
        var query = _query.Trim();

        if (query.Length == 0)
        {
            return;
        }

        _searchScrollRow = 0;
        _search(query);
        Invalidate();
    }

    private void InsertText(string text)
    {
        var clean = text
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace('\t', ' ');

        if (clean.Length == 0)
        {
            return;
        }

        var room = MaxQueryLength - _query.Length;
        if (room <= 0)
        {
            return;
        }

        if (clean.Length > room)
        {
            clean = clean[..room];
        }

        _query = _query.Insert(_caret, clean);
        _caret += clean.Length;
        Invalidate();
    }

    private void PasteText()
    {
        var text = ClipboardText();

        if (text is not null)
        {
            InsertText(text);
        }
    }

    /// <summary>
    /// Reads the clipboard's Unicode text. Best effort: a clipboard another process is holding open,
    /// or one with no text in it, simply inserts nothing.
    /// </summary>
    private string? ClipboardText()
    {
        if (!OpenClipboard(Handle))
        {
            return null;
        }

        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == nint.Zero)
            {
                return null;
            }

            var pointer = GlobalLock(handle);
            if (pointer == nint.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    private bool SearchKey(int virtualKey)
    {
        switch (virtualKey)
        {
            case VK_RETURN:
                RunSearch();
                return true;

            case VK_ESCAPE:
                CloseSearch();
                return true;

            case 0x25: // Left
                if (_caret > 0)
                {
                    _caret--;
                    Invalidate();
                }

                return true;

            case 0x27: // Right
                if (_caret < _query.Length)
                {
                    _caret++;
                    Invalidate();
                }

                return true;

            case VK_HOME:
                _caret = 0;
                Invalidate();
                return true;

            case VK_END:
                _caret = _query.Length;
                Invalidate();
                return true;

            case 0x08: // Backspace
                if (_caret > 0)
                {
                    _query = _query.Remove(_caret - 1, 1);
                    _caret--;
                    Invalidate();
                }

                return true;

            case 0x2E: // Delete
                if (_caret < _query.Length)
                {
                    _query = _query.Remove(_caret, 1);
                    Invalidate();
                }

                return true;

            case 0x56 when (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0: // Ctrl+V
                PasteText();
                return true;

            case 0x26: // Up
                ScrollSearch(-1);
                return true;

            case 0x28: // Down
                ScrollSearch(1);
                return true;

            case VK_PRIOR:
                ScrollSearch(-(_visibleSearchRows - 1));
                return true;

            case VK_NEXT:
                ScrollSearch(_visibleSearchRows - 1);
                return true;

            default:
                // While the field is open it owns the keyboard, so no key falls through to the
                // sheet's scroll shortcuts or closes the window behind it.
                return true;
        }
    }

    private int CaretFromX(float x)
    {
        var textLeft = _fieldRect.Left + SearchFieldPadding;
        var target = x - textLeft + _queryOffset;
        var best = 0;
        var bestDistance = float.MaxValue;

        for (var i = 0; i <= _query.Length; i++)
        {
            var width = Measure(_query[..i], SearchTextSize, FontWeight.Normal);
            var distance = MathF.Abs(width - target);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private void ResetQuery(LyricSheet sheet)
    {
        _query = sheet.SuggestSearchQuery();
        _caret = _query.Length;
        _queryOffset = 0f;
        _searchScrollRow = 0;
    }

    private static (uint Foreground, uint Background) BadgeColors(LyricBadgeTone tone) => tone switch
    {
        LyricBadgeTone.Accent => (ColorAccent, 0x2A7CC4FF),
        LyricBadgeTone.Good => (ColorGood, 0x228FD6A0),
        LyricBadgeTone.Warm => (ColorWarm, 0x26E0A458),
        LyricBadgeTone.Warning => (ColorWarning, 0x26E0A458),
        _ => (ColorTextDim, 0x1AFFFFFF)
    };

    protected override bool IsDragTarget(int id) => id is ScrollbarControl or SearchScrollbarControl;

    protected override void OnActivate(int id)
    {
        if (id >= NudgeBase && id < NudgeBase + NudgeStepsMs.Length)
        {
            NudgeEdit(NudgeStepsMs[id - NudgeBase]);
            return;
        }

        if (id >= SearchResultBase)
        {
            if (!_resultRecords.TryGetValue(id, out var recordId))
            {
                Invalidate();
                return;
            }

            _searchOpen = false;
            ClearHover();
            _chooseRecord(recordId);
            Invalidate();
            return;
        }

        switch (id)
        {
            case OpenSearchControl:
                OpenSearch();
                return;

            case SearchGoControl:
                RunSearch();
                return;

            case SearchCloseControl:
                CloseSearch();
                return;

            case ExportControl:
                _export(_edit?.Lines ?? (IReadOnlyList<LyricLine>)(_sheet?.Lines ?? []));
                return;

            case EditFileControl:
                _openExternal(_edit?.Lines ?? (IReadOnlyList<LyricLine>)(_sheet?.Lines ?? []));
                return;

            case UndoControl:
                _edit?.Undo();
                AfterHistoryMove();
                return;

            case RedoControl:
                _edit?.Redo();
                AfterHistoryMove();
                return;

            case SetStartControl:
                SetStartToNow();
                return;

            case SetEndControl:
                SetEndToNow();
                return;

            case SaveControl:
                SaveEdits();
                return;
        }
    }

    protected override void OnBodyMouseDown(float x, float y)
    {
        if (_searchOpen)
        {
            if (x >= _fieldRect.Left && x < _fieldRect.Right && y >= _fieldRect.Top && y < _fieldRect.Bottom)
            {
                _caret = CaretFromX(x);
                Invalidate();
                return;
            }

            if (IsDragging && DragControl == SearchScrollbarControl)
            {
                SetSearchScrollFromY(y);
            }

            return;
        }

        if (y >= ListTop && y <= ListBottom && x < WidthDip - 30f)
        {
            SelectLineAt(y);
        }

        if (IsDragging && DragControl == ScrollbarControl)
        {
            SetScrollFromY(y);
        }
    }

    protected override void OnBodyMouseMove(float x, float y)
    {
        if (!IsDragging)
        {
            return;
        }

        if (DragControl == ScrollbarControl)
        {
            SetScrollFromY(y);
        }
        else if (DragControl == SearchScrollbarControl)
        {
            SetSearchScrollFromY(y);
        }
    }

    private void SetScrollFromY(float y)
    {
        var viewport = ListBottom - ListTop;
        var maxScroll = Math.Max(0f, _contentHeight - viewport);

        if (maxScroll <= 0f)
        {
            return;
        }

        var t = Math.Clamp((y - ListTop) / Math.Max(1f, viewport), 0f, 1f);
        var next = t * maxScroll;

        if (Math.Abs(next - _scroll) > 0.5f)
        {
            _scroll = next;
            ClearHover();
            Invalidate();
        }
    }

    private void SetSearchScrollFromY(float y)
    {
        var height = ListBottom - SearchResultsTop;

        if (height <= 0f || _maxSearchRow <= 0)
        {
            return;
        }

        var t = Math.Clamp((y - SearchResultsTop) / height, 0f, 1f);
        var next = (int)MathF.Round(t * _maxSearchRow);

        if (next != _searchScrollRow)
        {
            _searchScrollRow = next;
            ClearHover();
            Invalidate();
        }
    }

    protected override void OnBodyWheel(int notches)
    {
        if (notches == 0)
        {
            return;
        }

        var steps = Math.Max(1, Math.Abs(notches) / 120);

        if (_searchOpen)
        {
            ScrollSearch(notches > 0 ? -steps : steps);
            return;
        }

        var viewport = ListBottom - ListTop;
        var maxScroll = Math.Max(0f, _contentHeight - viewport);

        if (maxScroll <= 0f)
        {
            return;
        }

        NextScroll(_scroll + (notches > 0 ? -72f : 72f) * steps, maxScroll);
    }

    private void ScrollSearch(int rows)
    {
        if (_maxSearchRow <= 0 || rows == 0)
        {
            return;
        }

        var next = Math.Clamp(_searchScrollRow + rows, 0, _maxSearchRow);

        if (next != _searchScrollRow)
        {
            _searchScrollRow = next;
            ClearHover();
            Invalidate();
        }
    }

    protected override void OnBodyChar(char character)
    {
        if (!_searchOpen || char.IsControl(character))
        {
            return;
        }

        InsertText(character.ToString());
    }

    protected override bool OnBodyKey(int virtualKey)
    {
        if (_searchOpen)
        {
            return SearchKey(virtualKey);
        }

        if (virtualKey == 0x46
            && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
            && _sheet is { IsDemo: false })
        {
            OpenSearch();
            return true;
        }

        if (_edit is not null && virtualKey is 0x26 or 0x28)
        {
            _edit.SelectRelative(virtualKey == 0x26 ? -1 : 1);
            EnsureSelectedVisible();
            Invalidate();
            return true;
        }

        if (virtualKey == 0x5A && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0)
        {
            _edit?.Undo();
            AfterHistoryMove();
            return true;
        }

        if (virtualKey == 0x59 && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0)
        {
            _edit?.Redo();
            AfterHistoryMove();
            return true;
        }

        if (virtualKey == 0x43 && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0)
        {
            CopyLyrics();
            return true;
        }

        var viewport = ListBottom - ListTop;
        var maxScroll = Math.Max(0f, _contentHeight - viewport);

        if (maxScroll <= 0f)
        {
            return false;
        }

        var next = virtualKey switch
        {
            0x26 => _scroll - 32f,          // Up
            0x28 => _scroll + 32f,          // Down
            0x21 => _scroll - (viewport * 0.85f), // Page Up
            0x22 => _scroll + (viewport * 0.85f), // Page Down
            0x24 => 0f,                      // Home
            0x23 => maxScroll,               // End
            _ => float.NaN
        };

        if (float.IsNaN(next))
        {
            return false;
        }

        NextScroll(next, maxScroll);
        return true;
    }

    private void NextScroll(float value, float maxScroll)
    {
        var next = Math.Clamp(value, 0f, maxScroll);

        if (Math.Abs(next - _scroll) <= 0.5f)
        {
            return;
        }

        _scroll = next;
        ClearHover();
        Invalidate();
    }

    private void Button(
        ID2D1DeviceContext context,
        int id,
        string text,
        Rect rect,
        bool enabled)
    {
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var hot = enabled && HoverId == id;

        FillRound(context, rect.Left, rect.Top, width, height, 8f, hot ? ColorControlHot : ColorControl);
        Label(
            context,
            text,
            rect.Left,
            rect.Top,
            width,
            height,
            12f,
            FontWeight.Normal,
            enabled ? ColorText : ColorTextFaint,
            TextAlignment.Center);

        if (enabled)
        {
            Hit(id, rect);
        }
    }
}
