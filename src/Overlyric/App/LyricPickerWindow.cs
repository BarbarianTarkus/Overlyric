using Overlyric.Lyrics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Overlyric.App;

/// <summary>
/// The lyric selector: the track, what the overlay is currently showing, and every lrclib record that
/// could replace it.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the tray's old "Lyrics version" submenu. A menu cannot show a badge, a length delta
/// and an lrclib id at once, and it dismisses itself after every click; a window stays open while the
/// listener compares records and hears the effect of each choice on the overlay behind it.
/// </para>
/// <para>
/// Picking a row applies immediately rather than staging a choice behind an OK button. The overlay is
/// visible while this window is open, so the fastest way to judge a record is to hear it.
/// </para>
/// </remarks>
internal sealed class LyricPickerWindow : PanelWindow
{
    private const int RefreshControl = 1;
    private const int ViewLyricsControl = 2;
    private const int AutomaticControl = 3;
    private const int ScrollbarControl = 4;
    private const int RowBase = 100;

    private const float Pad = 28f;
    private const float InfoHeight = 74f;
    private const float ToolbarTop = 82f;
    private const float ToolbarHeight = 30f;
    private const float ListHeaderTop = 122f;
    private const float ListTop = 146f;
    private const float FooterHeight = 34f;
    private const float RowHeight = 46f;

    /// <summary>
    /// The three fixed columns after the title. They are left edges rather than right edges so the
    /// title, the length, the delta and the badges cannot drift into one another.
    /// </summary>
    private const float TitleWidth = 438f;
    private const float DurationLeft = 505f;
    private const float DeltaLeft = 570f;

    private readonly Func<LyricPickerModel> _model;
    private readonly Action<long?> _select;
    private readonly Action _refresh;
    private readonly Action _viewLyrics;

    private int _scrollRow;
    private int _maxScrollRow;
    private int _visibleRows;
    private string _listKey = "";
    private long _drawnRevision = -1;
    private readonly Dictionary<int, long> _rowRecords = [];

    public LyricPickerWindow(
        Func<LyricPickerModel> model,
        Action<long?> select,
        Action refresh,
        Action viewLyrics)
    {
        _model = model;
        _select = select;
        _refresh = refresh;
        _viewLyrics = viewLyrics;
    }

    protected override float WidthDip => 900f;

    protected override float HeightDip => 580f;

    protected override string Title => "Choose lyrics";

    private float ListBottom => HeightDip - FooterHeight;

    private float ListRight => WidthDip - 44f;

    protected override void DrawBody(ID2D1DeviceContext context)
    {
        var model = Snapshot();
        SyncList(model);
        DrawTrack(context, model);
        DrawToolbar(context, model);
        DrawListHeader(context);
        DrawVersions(context, model);
        DrawFooter(context, model);
    }

    private LyricPickerModel Snapshot()
    {
        try
        {
            return _model() ?? new LyricPickerModel();
        }
        catch (Exception)
        {
            return new LyricPickerModel();
        }
    }

    /// <summary>Reset the scroll window when the track or the list changes underneath it.</summary>
    private void SyncList(LyricPickerModel model)
    {
        var key = string.Concat(
            model.Revision,
            "|",
            model.Versions.Count,
            "|",
            model.PinnedId?.ToString() ?? "-",
            "|",
            model.HasTrack ? "1" : "0");

        if (string.Equals(key, _listKey, StringComparison.Ordinal))
        {
            return;
        }

        _listKey = key;
        _scrollRow = Math.Clamp(_scrollRow, 0, Math.Max(0, model.Versions.Count - _visibleRows + 1));
        ClearHover();
    }

    private void DrawTrack(ID2D1DeviceContext context, LyricPickerModel model)
    {
        if (!model.HasTrack)
        {
            Label(context, "Nothing is playing", Pad, 18f, WidthDip - (2 * Pad), 32f, 22f, FontWeight.SemiBold, ColorText);
            Label(
                context,
                "Start a track in any player that publishes to Windows media controls.",
                Pad,
                50f,
                WidthDip - (2 * Pad),
                20f,
                12.5f,
                FontWeight.Normal,
                ColorTextDim);
            Fill(context, R(Pad, InfoHeight - 1f, WidthDip - Pad, InfoHeight), ColorLine);
            return;
        }

        var title = LyricText.Fit(model.Title.Length > 0 ? model.Title : model.Artist, 68);
        Label(context, title, Pad, 12f, WidthDip - (2 * Pad), 32f, 22f, FontWeight.SemiBold, ColorText);

        var meta = BuildMeta(model);
        Label(context, meta, Pad, 44f, WidthDip - (2 * Pad), 20f, 12.5f, FontWeight.Normal, ColorTextDim);

        // What the overlay is showing right now, on the right of the header, so the list starts from
        // an answer rather than from a question.
        var badge = LyricBadges.ForSheet(
            model.LookupComplete,
            model.IsInstrumental,
            model.LineCount > 0,
            model.IsSynced,
            model.HasWordTimings);

        var (foreground, background) = BadgeColors(badge.Tone);
        var right = WidthDip - Pad;
        right = Badge(context, badge.Label, right, 26f, foreground, background) - 8f;

        var record = model.CurrentRecordId is { } id ? $"lrclib #{id}" : "No record";
        Label(
            context,
            record,
            WidthDip - Pad - 220f,
            44f,
            220f,
            18f,
            11f,
            FontWeight.Normal,
            ColorTextFaint,
            TextAlignment.Trailing);

        if (!string.IsNullOrWhiteSpace(model.Status) && model.LineCount == 0)
        {
            Label(
                context,
                LyricText.Fit(model.Status, 40),
                WidthDip - Pad - 320f,
                64f,
                320f,
                18f,
                11f,
                FontWeight.Normal,
                ColorWarning,
                TextAlignment.Trailing);
        }

        Fill(context, R(Pad, InfoHeight - 1f, WidthDip - Pad, InfoHeight), ColorLine);
    }

    private static string BuildMeta(LyricPickerModel model)
    {
        var parts = new List<string>(4);

        if (model.Artist.Length > 0)
        {
            parts.Add(model.Artist);
        }

        if (!string.IsNullOrWhiteSpace(model.Album))
        {
            parts.Add(model.Album!);
        }

        if (model.DurationSeconds is > 0)
        {
            parts.Add(LyricText.FormatDuration(model.DurationSeconds));
        }

        if (!string.IsNullOrWhiteSpace(model.SourceApp))
        {
            parts.Add(model.SourceApp!);
        }

        return parts.Count == 0 ? "Unknown track" : string.Join(" \u00b7 ", parts);
    }

    private void DrawToolbar(ID2D1DeviceContext context, LyricPickerModel model)
    {
        var count = model.Versions.Count;
        var text = model.IsDemo
            ? "Demo mode: the canned track is not sent to lrclib."
            : !model.HasTrack
                ? "No track to look up."
                : model.Loading
                    ? "Looking for other records\u2026"
                    : count == 0
                        ? "No alternative records found."
                        : $"{count} {(count == 1 ? "version" : "versions")} \u00b7 best match first";

        Label(
            context,
            text,
            Pad,
            ToolbarTop,
            WidthDip - (2 * Pad) - 250f,
            ToolbarHeight,
            12f,
            FontWeight.Normal,
            model.Loading ? ColorAccent : ColorTextFaint);

        const float lookWidth = 96f;
        const float viewWidth = 106f;

        var right = WidthDip - Pad;
        Button(
            context,
            RefreshControl,
            "Look again",
            right - lookWidth,
            ToolbarTop,
            lookWidth,
            ToolbarHeight,
            model.HasTrack && !model.Loading && !model.IsDemo);

        right -= lookWidth + 8f;

        Button(
            context,
            ViewLyricsControl,
            "Full lyrics",
            right - viewWidth,
            ToolbarTop,
            viewWidth,
            ToolbarHeight,
            model.HasTrack);
    }

    private void DrawListHeader(ID2D1DeviceContext context)
    {
        var y = ListHeaderTop;

        Label(context, "VERSION", Pad + 34f, y, 300f, 20f, 9.5f, FontWeight.SemiBold, ColorTextFaint);
        Label(
            context,
            "LENGTH",
            DurationLeft,
            y,
            60f,
            20f,
            9.5f,
            FontWeight.SemiBold,
            ColorTextFaint,
            TextAlignment.Trailing);
        Label(
            context,
            "MATCH",
            DeltaLeft,
            y,
            70f,
            20f,
            9.5f,
            FontWeight.SemiBold,
            ColorTextFaint,
            TextAlignment.Trailing);
        Label(
            context,
            "KIND",
            WidthDip - 250f,
            y,
            170f,
            20f,
            9.5f,
            FontWeight.SemiBold,
            ColorTextFaint,
            TextAlignment.Trailing);
    }

    private void DrawVersions(ID2D1DeviceContext context, LyricPickerModel model)
    {
        var listHeight = ListBottom - ListTop;
        _visibleRows = Math.Max(1, (int)MathF.Floor(listHeight / RowHeight));
        _maxScrollRow = 0;

        if (!model.HasTrack)
        {
            DrawEmpty(
                context,
                "The selector follows whatever is playing.",
                "Open a track and it will list every lrclib record for it.");
            return;
        }

        if (model.IsDemo && model.Versions.Count == 0)
        {
            DrawEmpty(
                context,
                "Demo mode",
                "The canned preview does not query lrclib. Play a real track to list its records.");
            return;
        }

        if (model.Loading && model.Versions.Count == 0)
        {
            DrawEmpty(
                context,
                "Looking for other versions\u2026",
                "lrclib is being asked for every record with this title and artist.");
            return;
        }

        if (model.Versions.Count == 0)
        {
            DrawEmpty(
                context,
                "No alternative versions",
                "lrclib has nothing else for this track right now. Look again in a moment.");
            return;
        }

        var totalRows = model.Versions.Count + 1;
        _maxScrollRow = Math.Max(0, totalRows - _visibleRows);
        _scrollRow = Math.Clamp(_scrollRow, 0, _maxScrollRow);

        var pinned = model.PinnedId;
        var y = ListTop;

        // Remember which record each drawn row belongs to. A click that arrives after the list has
        // changed underneath the pixels must not pin whatever record now occupies that index.
        _drawnRevision = model.Revision;
        _rowRecords.Clear();

        for (var slot = 0; slot < _visibleRows; slot++)
        {
            var index = _scrollRow + slot;

            if (index >= totalRows)
            {
                break;
            }

            if (index == 0)
            {
                DrawAutomaticRow(context, model, y, pinned is null);
            }
            else
            {
                var candidate = model.Versions[index - 1];
                DrawCandidateRow(context, model, candidate, index - 1, y, pinned);
            }

            y += RowHeight;
        }

        if (_maxScrollRow > 0)
        {
            Scrollbar(
                context,
                WidthDip - 18f,
                ListTop,
                listHeight,
                _visibleRows,
                totalRows,
                _scrollRow,
                ColorAccent);

            Hit(ScrollbarControl, R(WidthDip - 24f, ListTop, WidthDip - 10f, ListBottom));
        }
    }

    private void DrawAutomaticRow(ID2D1DeviceContext context, LyricPickerModel model, float top, bool selected)
    {
        var row = R(Pad, top + 2f, ListRight, top + RowHeight - 2f);
        var hot = HoverId == AutomaticControl;

        if (selected)
        {
            FillRound(context, row.Left, row.Top, row.Right - row.Left, row.Bottom - row.Top, 9f, ColorSelected);
        }
        else if (hot)
        {
            FillRound(context, row.Left, row.Top, row.Right - row.Left, row.Bottom - row.Top, 9f, ColorControlHot);
        }

        Label(context, "A", Pad + 8f, top + 4f, 20f, 24f, 10.5f, FontWeight.SemiBold, ColorAccent, TextAlignment.Center);
        Label(context, "Automatic (best match)", Pad + 34f, top + 4f, 380f, 20f, 13f, FontWeight.SemiBold, ColorText);
        Label(
            context,
            "Duration first, then title, album and timings.",
            Pad + 34f,
            top + 24f,
            420f,
            16f,
            10.5f,
            FontWeight.Normal,
            ColorTextDim);

        var right = ListRight - 8f;

        if (selected)
        {
            right = Badge(context, "In use", right, top + (RowHeight / 2f), ColorAccent, 0x2A7CC4FF) - 6f;
        }

        Badge(context, "Automatic", right, top + (RowHeight / 2f), ColorTextDim, 0x1AFFFFFF);

        Hit(AutomaticControl, row);
    }

    private void DrawCandidateRow(
        ID2D1DeviceContext context,
        LyricPickerModel model,
        LyricsCandidate candidate,
        int index,
        float top,
        long? pinned)
    {
        var id = RowBase + index;
        _rowRecords[id] = candidate.Id;
        var row = R(Pad, top + 2f, ListRight, top + RowHeight - 2f);
        var selected = pinned == candidate.Id;
        var inUse = model.PinnedId is null && model.CurrentRecordId == candidate.Id;
        var hot = HoverId == id;

        if (selected)
        {
            FillRound(context, row.Left, row.Top, row.Right - row.Left, row.Bottom - row.Top, 9f, ColorSelected);
            FillRound(context, row.Left, row.Top + 7f, 3f, RowHeight - 18f, 1.5f, ColorAccent);
        }
        else if (hot)
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
            Pad + 34f,
            top + 4f,
            TitleWidth,
            20f,
            13f,
            FontWeight.SemiBold,
            ColorText);

        var subtitle = BuildCandidateSubtitle(candidate);
        Label(
            context,
            LyricText.Fit(subtitle, 74),
            Pad + 34f,
            top + 24f,
            TitleWidth,
            16f,
            10.5f,
            FontWeight.Normal,
            ColorTextDim);

        Label(
            context,
            LyricText.FormatDuration(candidate.DurationSeconds),
            DurationLeft,
            top + 4f,
            60f,
            RowHeight - 8f,
            11.5f,
            FontWeight.Normal,
            ColorTextDim,
            TextAlignment.Trailing);

        var delta = LyricText.FormatDurationDelta(candidate.DurationSeconds, model.DurationSeconds);
        var deltaColor = delta == "matches" ? ColorGood : ColorTextFaint;

        Label(
            context,
            delta,
            DeltaLeft,
            top + 4f,
            70f,
            RowHeight - 8f,
            11.5f,
            FontWeight.Normal,
            deltaColor,
            TextAlignment.Trailing);

        var right = ListRight - 8f;

        var badge = LyricBadges.ForCandidate(candidate);
        var (foreground, background) = BadgeColors(badge.Tone);
        right = Badge(context, badge.Label, right, top + (RowHeight / 2f), foreground, background) - 6f;

        if (selected)
        {
            right = Badge(context, "Pinned", right, top + (RowHeight / 2f), ColorAccent, 0x2A7CC4FF) - 6f;
        }
        else if (inUse)
        {
            right = Badge(context, "In use", right, top + (RowHeight / 2f), ColorGood, 0x228FD6A0) - 6f;
        }

        Hit(id, row);
    }

    private static string BuildCandidateSubtitle(LyricsCandidate candidate)
    {
        var parts = new List<string>(4);

        if (!string.IsNullOrWhiteSpace(candidate.ArtistName))
        {
            parts.Add(candidate.ArtistName!);
        }

        if (!string.IsNullOrWhiteSpace(candidate.AlbumName))
        {
            parts.Add(candidate.AlbumName!);
        }

        var lines = candidate.HasWordSync || candidate.HasSynced
            ? candidate.SyncedLineCount
            : candidate.PlainLineCount;

        var lineText = LyricText.FormatLineCount(lines);
        if (lineText.Length > 0)
        {
            parts.Add(lineText);
        }

        parts.Add($"#{candidate.Id}");

        return string.Join(" \u00b7 ", parts);
    }

    private void DrawEmpty(ID2D1DeviceContext context, string title, string detail)
    {
        var center = (ListTop + ListBottom) / 2f;

        Label(
            context,
            title,
            Pad,
            center - 30f,
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

    private void DrawFooter(ID2D1DeviceContext context, LyricPickerModel model)
    {
        Fill(context, R(Pad, ListBottom, WidthDip - Pad, ListBottom + 1f), ColorLine);

        var left = model.HasTrack
            ? "Choosing a row applies it immediately; the overlay behind this window follows."
            : "Esc closes.";

        Label(
            context,
            left,
            Pad,
            ListBottom + 8f,
            WidthDip - (2 * Pad) - 260f,
            20f,
            11f,
            FontWeight.Normal,
            ColorTextFaint);

        var right = model.Versions.Count > 0
            ? $"{_scrollRow + 1}-{Math.Min(_scrollRow + _visibleRows, model.Versions.Count + 1)} of {model.Versions.Count + 1}"
            : "Esc closes";

        Label(
            context,
            right,
            WidthDip - Pad - 240f,
            ListBottom + 8f,
            240f,
            20f,
            11f,
            FontWeight.Normal,
            ColorTextFaint,
            TextAlignment.Trailing);
    }

    private void Button(
        ID2D1DeviceContext context,
        int id,
        string text,
        float left,
        float top,
        float width,
        float height,
        bool enabled)
    {
        var hot = enabled && HoverId == id;
        FillRound(context, left, top, width, height, 8f, hot ? ColorControlHot : ColorControl);
        Label(
            context,
            text,
            left,
            top,
            width,
            height,
            12f,
            FontWeight.Normal,
            enabled ? ColorText : ColorTextFaint,
            TextAlignment.Center);

        if (enabled)
        {
            Hit(id, R(left, top, left + width, top + height));
        }
    }

    private static (uint Foreground, uint Background) BadgeColors(LyricBadgeTone tone) => tone switch
    {
        LyricBadgeTone.Accent => (ColorAccent, 0x2A7CC4FF),
        LyricBadgeTone.Good => (ColorGood, 0x228FD6A0),
        LyricBadgeTone.Warm => (ColorWarm, 0x26E0A458),
        LyricBadgeTone.Warning => (ColorWarning, 0x26E0A458),
        _ => (ColorTextDim, 0x1AFFFFFF)
    };

    protected override bool IsDragTarget(int id) => id == ScrollbarControl;

    protected override void OnActivate(int id)
    {
        if (id >= RowBase)
        {
            var model = Snapshot();

            if (model.Revision != _drawnRevision || !_rowRecords.TryGetValue(id, out var recordId))
            {
                Invalidate();
                return;
            }

            _select(recordId);
            Invalidate();
            return;
        }

        switch (id)
        {
            case AutomaticControl:
                if (Snapshot().Revision != _drawnRevision)
                {
                    Invalidate();
                    return;
                }

                _select(null);
                Invalidate();
                return;

            case RefreshControl:
                _refresh();
                Invalidate();
                return;

            case ViewLyricsControl:
                _viewLyrics();
                return;
        }
    }

    protected override void OnBodyMouseDown(float x, float y)
    {
        if (IsDragging && DragControl == ScrollbarControl)
        {
            SetScrollFromY(y);
        }
    }

    protected override void OnBodyMouseMove(float x, float y)
    {
        if (IsDragging && DragControl == ScrollbarControl)
        {
            SetScrollFromY(y);
        }
    }

    private void SetScrollFromY(float y)
    {
        var height = ListBottom - ListTop;

        if (height <= 0f || _maxScrollRow <= 0)
        {
            return;
        }

        var t = Math.Clamp((y - ListTop) / height, 0f, 1f);
        var next = (int)MathF.Round(t * _maxScrollRow);

        if (next != _scrollRow)
        {
            _scrollRow = next;
            Invalidate();
        }
    }

    protected override void OnBodyWheel(int notches)
    {
        if (notches == 0 || _maxScrollRow <= 0)
        {
            return;
        }

        var steps = Math.Max(1, Math.Abs(notches) / 120);
        var next = Math.Clamp(_scrollRow + (notches > 0 ? -steps : steps), 0, _maxScrollRow);

        if (next != _scrollRow)
        {
            _scrollRow = next;
            ClearHover();
            Invalidate();
        }
    }

    protected override bool OnBodyKey(int virtualKey)
    {
        var page = Math.Max(1, _visibleRows - 1);
        var next = virtualKey switch
        {
            0x26 => _scrollRow - 1,          // Up
            0x28 => _scrollRow + 1,          // Down
            0x21 => _scrollRow - page,       // Page Up
            0x22 => _scrollRow + page,       // Page Down
            0x24 => 0,                       // Home
            0x23 => _maxScrollRow,           // End
            _ => int.MinValue
        };

        if (next == int.MinValue)
        {
            return false;
        }

        next = Math.Clamp(next, 0, _maxScrollRow);

        if (next != _scrollRow)
        {
            _scrollRow = next;
            ClearHover();
            Invalidate();
        }

        return true;
    }
}
