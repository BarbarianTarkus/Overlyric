using System.Runtime.InteropServices;
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
    private const int ImportControl = 5;
    private const int OpenWebControl = 6;
    private const int ClipboardControl = 7;
    private const int ImportAcceptControl = 8;
    private const int ImportCancelControl = 9;
    private const int TitleFieldControl = 10;
    private const int ArtistFieldControl = 11;
    private const int AlbumFieldControl = 12;
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
    /// The columns after the title are measured from the right edge, so the badges and the numbers
    /// stay where the eye expects them and the title gets whatever room is left. On the default
    /// 900 DIP window that puts the delta at 470 and the length at 558; shrinking the window shortens
    /// the title first, and growing it lengthens the title rather than scattering the columns.
    /// </summary>
    private const float BadgeColumnWidth = 220f;
    private const float MatchColumnWidth = 70f;
    private const float DurationColumnWidth = 60f;
    private const float ColumnGap = 18f;

    private readonly Func<LyricPickerModel> _model;
    private readonly Action<long?> _select;
    private readonly Action _refresh;
    private readonly Action _viewLyrics;
    private readonly Action _import;
    private readonly Action _openWeb;
    private readonly Action _pinClipboard;
    private readonly Action _acceptImport;
    private readonly Action _cancelImport;
    private readonly Action<string> _search;

    private ID2D1Bitmap? _artworkBitmap;
    private byte[]? _artworkSource;

    private string _titleField = "";
    private string _artistField = "";
    private string _albumField = "";
    private string _albumSeed = "";
    private long _queryRevision = long.MinValue;

    /// <summary>Which header field holds the keyboard: 0 none, 1 the title, 2 the artist, 3 the album.</summary>
    private int _fieldFocus;
    private bool _searchMode;

    /// <summary>
    /// What the search box currently asks for, in lrclib's word order. The album is a refinement, not
    /// part of the default question: it only joins when the listener has changed it. Left as the track
    /// states it, "artist title" is what finds a song; adding the album narrows the answer to nothing
    /// far more often than it disambiguates.
    /// </summary>
    private string ComposedQuery
    {
        get
        {
            var parts = new List<string>(3) { _artistField.Trim(), _titleField.Trim() };
            var album = _albumField.Trim();

            if (album.Length > 0 && !string.Equals(album, _albumSeed.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(album);
            }

            return string.Join(' ', parts.Where(static part => part.Length > 0));
        }
    }

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
        Action viewLyrics,
        Action import,
        Action openWeb,
        Action pinClipboard,
        Action acceptImport,
        Action cancelImport,
        Action<string> search)
    {
        _model = model;
        _select = select;
        _refresh = refresh;
        _viewLyrics = viewLyrics;
        _import = import;
        _openWeb = openWeb;
        _pinClipboard = pinClipboard;
        _acceptImport = acceptImport;
        _cancelImport = cancelImport;
        _search = search;
    }

    protected override float DefaultWidthDip => 900f;

    protected override float DefaultHeightDip => 580f;

    protected override float MinWidthDip => 720f;

    protected override float MinHeightDip => 360f;

    protected override string Title => "Choose lyrics";

    protected override WindowDestination Destination => WindowDestination.Versions;

    private float ListBottom => HeightDip - FooterHeight;

    private float ListRight => WidthDip - 44f;

    private float TitleWidth => MathF.Max(80f, MatchLeft - (Pad + 34f) - 16f);

    private float MatchLeft => ListRight - BadgeColumnWidth - ColumnGap - DurationColumnWidth - ColumnGap - MatchColumnWidth;

    private float DurationLeft => ListRight - BadgeColumnWidth - ColumnGap - DurationColumnWidth;

    protected override void DrawBody(ID2D1DeviceContext context)
    {
        var model = Snapshot();
        SyncList(model);
        ResetQueryForTrack(model);
        DrawTrack(context, model);
        DrawToolbar(context, model);

        if (model.ImportPreview is not null)
        {
            DrawImportPreview(context, model);
        }
        else
        {
            DrawListHeader(context);
            DrawVersions(context, model);
        }

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
            _searchMode ? model.SearchResults.Count : model.Versions.Count,
            "|",
            _searchMode ? "search" : "auto",
            "|",
            model.PinnedId?.ToString() ?? "-",
            "|",
            model.HasTrack ? "1" : "0");

        if (string.Equals(key, _listKey, StringComparison.Ordinal))
        {
            return;
        }

        _listKey = key;
        var total = _searchMode ? model.SearchResults.Count : model.Versions.Count;
        _scrollRow = Math.Clamp(_scrollRow, 0, Math.Max(0, total - _visibleRows + 1));
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

        // The title and the meta line the automatic lookup would have typed are the search box now:
        // refining a lookup that found nothing happens here rather than in another window.

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

        if (model.Source == LyricSource.Local)
        {
            right = Badge(context, LyricBadges.Local.Label, right, 26f, ColorAccent, 0x2A7CC4FF) - 8f;
        }

        var record = model.Source switch
        {
            LyricSource.Local => "local file",
            LyricSource.Pinned => model.CurrentRecordId is { } pinnedId ? $"pinned lrclib #{pinnedId}" : "pinned record",
            _ => model.CurrentRecordId is { } id ? $"lrclib #{id}" : "No record"
        };
        Label(
            context,
            record,
            WidthDip - Pad - 220f,
            40f,
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
                58f,
                320f,
                18f,
                11f,
                FontWeight.Normal,
                ColorWarning,
                TextAlignment.Trailing);
        }

        DrawHeaderFields(context, model);

        Fill(context, R(Pad, InfoHeight - 1f, WidthDip - Pad, InfoHeight), ColorLine);
    }

    /// <summary>
    /// The header is the query, in the shape the album art gives it: the cover and the album name on
    /// the left, the song title, the artist and the length centred beside it. The title and the artist
    /// are editable where they are printed - no box, no second search window - and Enter, or the
    /// Search button, asks lrclib what the two lines say.
    /// </summary>
    private void DrawHeaderFields(ID2D1DeviceContext context, LyricPickerModel model)
    {
        const float artworkSize = 48f;
        const float artworkX = 50f;
        const float centerLeft = 100f;
        var centerWidth = MathF.Max(200f, WidthDip - 200f);

        var artwork = ArtworkBitmap(context, model);

        if (artwork is not null)
        {
            context.DrawBitmap(
                artwork,
                new Vortice.RawRectF(artworkX, 4f, artworkX + artworkSize, 4f + artworkSize),
                1f,
                InterpolationMode.Linear,
                null,
                null);
        }
        else
        {
            FillRound(context, artworkX, 4f, artworkSize, artworkSize, 3f, ColorControl);
        }

        // The album name sits under the cover, centred on it, and is editable like the other two.
        DrawFieldText(
            context,
            _albumField,
            _fieldFocus == 3,
            "Album",
            11f,
            FontWeight.Normal,
            ColorTextFaint,
            0f,
            52f,
            (2f * artworkX) + artworkSize,
            16f,
            16);

        DrawFieldText(context, _titleField, _fieldFocus == 1, "Song title", 22f, FontWeight.SemiBold, ColorText, centerLeft, 3f, centerWidth, 30f, 48);
        DrawFieldText(context, _artistField, _fieldFocus == 2, "Artist", 12.5f, FontWeight.Normal, ColorTextDim, centerLeft, 32f, centerWidth, 18f, 64);

        Label(
            context,
            LyricText.Fit(BuildMetaLine(model), 40),
            centerLeft,
            52f,
            centerWidth,
            18f,
            12.5f,
            FontWeight.Normal,
            ColorTextDim,
            TextAlignment.Center);

        Hit(TitleFieldControl, R(centerLeft, 3f, centerLeft + centerWidth, 32f));
        Hit(ArtistFieldControl, R(centerLeft, 32f, centerLeft + centerWidth, 52f));
        Hit(AlbumFieldControl, R(0f, 52f, (2f * artworkX) + artworkSize, 70f));
    }

    private static string BuildMetaLine(LyricPickerModel model)
    {
        var parts = new List<string>(2);

        if (model.DurationSeconds is > 0)
        {
            parts.Add(LyricText.FormatDuration(model.DurationSeconds));
        }

        if (!string.IsNullOrWhiteSpace(model.SourceApp))
        {
            parts.Add(model.SourceApp!);
        }

        return string.Join(" \u00b7 ", parts);
    }

    private void DrawFieldText(
        ID2D1DeviceContext context,
        string text,
        bool focused,
        string placeholder,
        float size,
        FontWeight weight,
        uint color,
        float left,
        float top,
        float width,
        float height,
        int fit)
    {
        var empty = text.Length == 0;
        var shown = empty && !focused ? placeholder : LyricText.Fit(text, fit);
        var shownColor = empty && !focused ? ColorTextFaint : color;

        Label(context, shown, left, top, width, height, size, weight, shownColor, TextAlignment.Center);

        if (!focused)
        {
            return;
        }

        // The caret and the rule are measured, not drawn as a "|" glyph: the character carries its own
        // side bearings, which is what left a gap between the last letter and the cursor.
        var textWidth = Measure(shown, size, weight);
        var textLeft = left + MathF.Max(0f, (width - textWidth) / 2f);

        var caretHeight = MathF.Max(size, 12f);
        var caretTop = top + MathF.Max(1f, (height - caretHeight) / 2f);
        var caretX = textLeft + textWidth + 1f;

        Fill(context, R(caretX, caretTop, caretX + 1f, caretTop + caretHeight), ColorAccent);
        Fill(context, R(textLeft - 2f, top + height - 1f, textLeft + textWidth + 2f, top + height), ColorAccent);
    }

    /// <summary>The album art, decoded once per image and reused until the track changes.</summary>
    private ID2D1Bitmap? ArtworkBitmap(ID2D1DeviceContext context, LyricPickerModel model)
    {
        if (model.Artwork is null || model.ArtworkWidth <= 0 || model.ArtworkHeight <= 0)
        {
            return null;
        }

        if (ReferenceEquals(model.Artwork, _artworkSource))
        {
            return _artworkBitmap;
        }

        _artworkBitmap?.Dispose();
        _artworkBitmap = null;
        _artworkSource = model.Artwork;

        var handle = GCHandle.Alloc(model.Artwork, GCHandleType.Pinned);

        try
        {
            var properties = new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96f,
                96f);

            _artworkBitmap = context.CreateBitmap(
                new SizeI(model.ArtworkWidth, model.ArtworkHeight),
                handle.AddrOfPinnedObject(),
                (uint)(model.ArtworkWidth * 4),
                properties);
        }
        catch (Exception)
        {
            _artworkBitmap = null;
        }
        finally
        {
            handle.Free();
        }

        return _artworkBitmap;
    }

    /// <summary>
    /// Ask lrclib the question the two lines spell out. When they still hold the track's own words the
    /// answer is the automatic list again, which is what "look again" meant before the fields existed.
    /// </summary>
    private void RunQuerySearch()
    {
        var model = Snapshot();
        var query = ComposedQuery;

        if (query.Length == 0 || model.IsDemo)
        {
            Invalidate();
            return;
        }

        _scrollRow = 0;
        ClearHover();

        if (string.Equals(query, model.Query.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            _searchMode = false;
            _refresh();
        }
        else
        {
            _searchMode = true;
            _search(query);
        }

        Invalidate();
    }

    /// <summary>A new track resets the field to what the automatic finder would have asked.</summary>
    private void ResetQueryForTrack(LyricPickerModel model)
    {
        if (model.Revision == _queryRevision)
        {
            return;
        }

        _queryRevision = model.Revision;
        _titleField = model.Title;
        _artistField = model.Artist;
        _albumSeed = model.Album ?? "";
        _albumField = _albumSeed;
        _fieldFocus = 0;
        _searchMode = false;
    }

    private void DrawToolbar(ID2D1DeviceContext context, LyricPickerModel model)
    {
        var count = _searchMode ? model.SearchResults.Count : model.Versions.Count;
        var busy = _searchMode ? model.SearchLoading : model.Loading;
        var text = model.IsDemo
            ? "Demo mode: the canned track is not sent to lrclib."
            : !model.HasTrack
                ? "No track to look up."
                : busy
                    ? _searchMode ? "Searching lrclib\u2026" : "Looking for other records\u2026"
                    : count == 0
                        ? _searchMode ? model.SearchMessage ?? "No records for that search." : "No alternative records found."
                        : _searchMode
                            ? $"{count} {(count == 1 ? "result" : "results")} for \u201c{LyricText.Fit(ComposedQuery, 40)}\u201d"
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
            busy ? ColorAccent : ColorTextFaint);

        const float lookWidth = 96f;
        const float viewWidth = 106f;

        var right = WidthDip - Pad;
        Button(
            context,
            RefreshControl,
            "Search",
            right - lookWidth,
            ToolbarTop,
            lookWidth,
            ToolbarHeight,
            model.HasTrack && !busy && !model.IsDemo);

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
            "MATCH",
            MatchLeft,
            y,
            70f,
            20f,
            9.5f,
            FontWeight.SemiBold,
            ColorTextFaint,
            TextAlignment.Trailing);
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

        // "KIND" labelled a column that only ever repeated the badges at the end of the row.
        Label(
            context,
            "BADGES",
            ListRight - 220f,
            y,
            220f,
            20f,
            9.5f,
            FontWeight.SemiBold,
            ColorTextFaint,
            TextAlignment.Trailing);
    }

    private void DrawVersions(ID2D1DeviceContext context, LyricPickerModel model)
    {
        var records = _searchMode ? model.SearchResults : model.Versions;
        var loading = _searchMode ? model.SearchLoading : model.Loading;

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

        if (model.IsDemo && records.Count == 0)
        {
            DrawEmpty(
                context,
                "Demo mode",
                "The canned preview does not query lrclib. Play a real track to list its records.");
            return;
        }

        if (loading && records.Count == 0)
        {
            DrawEmpty(
                context,
                _searchMode ? "Searching lrclib\u2026" : "Looking for other versions\u2026",
                _searchMode
                    ? "Asking lrclib for every record that matches what you typed."
                    : "lrclib is being asked for every record with this title and artist.");
            return;
        }

        if (records.Count == 0)
        {
            DrawEmpty(
                context,
                _searchMode ? "No records for that search" : "No alternative versions",
                _searchMode
                    ? model.SearchMessage ?? "Try fewer words, or the song title on its own."
                    : "lrclib has nothing else for this track right now.");

            // The empty state carries its own fix. This is the only moment the refresh matters, and a
            // button four rows up in the toolbar is a button nobody looks for. Same control id, so
            // the two of them dispatch the same command.
            const float retryWidth = 132f;

            Button(
                context,
                RefreshControl,
                "Search again",
                (WidthDip - retryWidth) / 2f,
                ((ListTop + ListBottom) / 2f) + 52f,
                retryWidth,
                30f,
                !model.Loading);

            return;
        }

        var totalRows = records.Count + (_searchMode ? 0 : 1);
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

            if (index == 0 && !_searchMode)
            {
                DrawAutomaticRow(context, model, y, pinned is null);
            }
            else
            {
                var slotIndex = _searchMode ? index : index - 1;
                var candidate = records[slotIndex];
                DrawCandidateRow(context, model, candidate, slotIndex, y, pinned);
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

        var delta = LyricText.FormatDurationDelta(candidate.DurationSeconds, model.DurationSeconds);
        var deltaColor = delta == "matches" ? ColorGood : ColorTextDim;

        Label(
            context,
            delta,
            MatchLeft,
            top + 4f,
            70f,
            RowHeight - 8f,
            11.5f,
            FontWeight.SemiBold,
            deltaColor,
            TextAlignment.Trailing);

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

        const float buttonHeight = 24f;
        var top = ListBottom + 5f;
        var left = Pad;
        var usable = model.HasTrack && !model.IsDemo;

        Button(context, ImportControl, "Import file\u2026", left, top, 100f, buttonHeight, usable);
        left += 108f;

        Button(context, OpenWebControl, "Open on LRCLIB", left, top, 116f, buttonHeight, usable);
        left += 124f;

        Button(context, ClipboardControl, "Pin from clipboard", left, top, 128f, buttonHeight, usable);
        left += 136f;

        var message = model.ImportMessage
            ?? (model.ImportPreview is not null
                ? "Review the import, then Import or Cancel."
                : model.HasTrack
                    ? "Choosing a row applies it immediately; the overlay behind this window follows."
                    : "Esc closes.");

        Label(
            context,
            message,
            left,
            ListBottom + 8f,
            MathF.Max(80f, WidthDip - Pad - left - 180f),
            20f,
            11f,
            FontWeight.Normal,
            model.ImportMessage is null ? ColorTextFaint : ColorTextDim);

        var right = model.ImportPreview is not null
            ? "Import preview"
            : model.Versions.Count > 0
                ? $"{_scrollRow + 1}-{Math.Min(_scrollRow + _visibleRows, model.Versions.Count + 1)} of {model.Versions.Count + 1}"
                : "Esc closes";

        Label(
            context,
            right,
            WidthDip - Pad - 180f,
            ListBottom + 8f,
            180f,
            20f,
            11f,
            FontWeight.Normal,
            ColorTextFaint,
            TextAlignment.Trailing);
    }

    private void DrawImportPreview(ID2D1DeviceContext context, LyricPickerModel model)
    {
        var preview = model.ImportPreview!;
        var top = ListTop;
        var bottom = ListBottom - 8f;
        var left = Pad + 18f;
        var width = WidthDip - (2 * Pad) - 36f;

        FillRound(context, Pad, top, WidthDip - (2 * Pad), bottom - top, 10f, ColorControl);

        Label(context, "Import " + preview.FileName, left, top + 14f, width, 28f, 17f, FontWeight.SemiBold, ColorText);

        var timing = preview.IsSynced
            ? $"{preview.LineCount} timed {(preview.LineCount == 1 ? "line" : "lines")}"
            : $"{preview.LineCount} plain {(preview.LineCount == 1 ? "line" : "lines")}";

        if (preview.IsSynced && preview.FirstStart is { } first && preview.LastStart is { } last)
        {
            timing += string.Concat(" \u00b7 ", LyricText.FormatTimestamp(first), " \u2192 ", LyricText.FormatTimestamp(last));
        }

        Label(context, timing, left, top + 50f, width, 22f, 12.5f, FontWeight.Normal, ColorTextDim);

        if (preview.DurationDeltaSeconds is { } delta)
        {
            var sentence = delta switch
            {
                < -0.5 => $"The sheet ends {Math.Abs(delta):0.0} s before the track does.",
                > 0.5 => $"The sheet runs {delta:0.0} s past the end of the track.",
                _ => "Its length matches the track."
            };

            Label(context, sentence, left, top + 76f, width, 22f, 12f, FontWeight.Normal, ColorTextFaint);
        }

        if (!preview.IsSynced)
        {
            Label(
                context,
                "No timestamps: the words are shown as plain text and cannot follow the music.",
                left,
                top + 104f,
                width,
                22f,
                12f,
                FontWeight.Normal,
                ColorWarning);
        }
        else
        {
            Label(
                context,
                "Stored for this track and used instead of the lrclib record until you choose Automatic.",
                left,
                top + 104f,
                width,
                22f,
                12f,
                FontWeight.Normal,
                ColorTextFaint);
        }

        var buttonTop = bottom - 46f;
        Button(context, ImportAcceptControl, "Import", WidthDip - Pad - 224f, buttonTop, 100f, 30f, true);
        Button(context, ImportCancelControl, "Cancel", WidthDip - Pad - 116f, buttonTop, 100f, 30f, true);
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

            case TitleFieldControl:
                _fieldFocus = 1;
                Invalidate();
                return;

            case ArtistFieldControl:
                _fieldFocus = 2;
                Invalidate();
                return;

            case AlbumFieldControl:
                _fieldFocus = 3;
                Invalidate();
                return;

            case RefreshControl:
                RunQuerySearch();
                return;

            case ViewLyricsControl:
                _viewLyrics();
                return;

            case ImportControl:
                _import();
                Invalidate();
                return;

            case OpenWebControl:
                _openWeb();
                Invalidate();
                return;

            case ClipboardControl:
                _pinClipboard();
                Invalidate();
                return;

            case ImportAcceptControl:
                _acceptImport();
                Invalidate();
                return;

            case ImportCancelControl:
                _cancelImport();
                Invalidate();
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

    protected override void OnBodyChar(char character)
    {
        if (_fieldFocus == 0 || char.IsControl(character))
        {
            return;
        }

        if (_fieldFocus == 1)
        {
            if (_titleField.Length < 96)
            {
                _titleField += character;
            }
        }
        else if (_fieldFocus == 2)
        {
            if (_artistField.Length < 96)
            {
                _artistField += character;
            }
        }
        else if (_albumField.Length < 96)
        {
            _albumField += character;
        }

        Invalidate();
    }

    protected override bool OnBodyKey(int virtualKey)
    {
        if (_fieldFocus != 0)
        {
            switch (virtualKey)
            {
                case 0x0D: // Enter asks lrclib the question the two lines spell out
                    RunQuerySearch();
                    return true;

                case 0x09: // Tab walks the three fields: title, artist, album
                    _fieldFocus = _fieldFocus == 3 ? 1 : _fieldFocus + 1;
                    Invalidate();
                    return true;

                case 0x08: // Backspace edits the line with the keyboard
                    if (_fieldFocus == 1)
                    {
                        if (_titleField.Length > 0)
                        {
                            _titleField = _titleField[..^1];
                        }
                    }
                    else if (_fieldFocus == 2)
                    {
                        if (_artistField.Length > 0)
                        {
                            _artistField = _artistField[..^1];
                        }
                    }
                    else if (_albumField.Length > 0)
                    {
                        _albumField = _albumField[..^1];
                    }

                    Invalidate();
                    return true;

                case 0x1B: // Escape leaves the field, not the window
                    _fieldFocus = 0;
                    Invalidate();
                    return true;
            }
        }

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
