using Overlyric.Lyrics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

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
/// The viewer follows the track while it is open: each new lyric result replaces the sheet, and a new
/// record resets the scroll to the line being sung.
/// </para>
/// </remarks>
internal sealed class LyricViewerWindow : PanelWindow
{
    private const int ScrollbarControl = 1;

    private const float Pad = 28f;
    private const float HeaderHeight = 98f;
    private const float ListTop = 106f;
    private const float FooterHeight = 38f;
    private const float TimestampLeft = 28f;
    private const float TimestampWidth = 62f;
    private const float TextLeft = 100f;
    private const float TextRightPadding = 52f;
    private const float ContentPaddingY = 8f;

    private const float ActiveFontSize = 16.5f;
    private const float InactiveFontSize = 14.5f;

    private static readonly uint ColorReader = 0xFFC9D0DC;
    private static readonly uint ColorReaderActive = 0xFFF5F7FA;

    private sealed record DisplayLine(string Timestamp, string Text, bool Active);

    private readonly List<DisplayLine> _lines = [];
    private readonly List<float> _prefix = [];
    private readonly List<float> _heights = [];

    private LyricSheet? _sheet;
    private bool _measured;
    private bool _scrollToActive;
    private float _contentHeight;
    private float _scroll;
    private int _activeIndex = -1;

    protected override float WidthDip => 900f;

    protected override float HeightDip => 620f;

    protected override string Title => "Lyrics";

    private float ListBottom => HeightDip - FooterHeight;

    private float TextWidth => WidthDip - TextLeft - TextRightPadding;

    /// <summary>Open the window on a sheet, scrolled to the line being sung.</summary>
    public void ShowSheet(LyricSheet sheet)
    {
        _sheet = sheet;
        _measured = false;
        _scrollToActive = true;
        _scroll = 0f;

        ShowPanel();
    }

    /// <summary>
    /// Replace the document while the window is open. The scroll is preserved when the same record is
    /// still being shown, and jumps back to the active line when a different one arrives.
    /// </summary>
    public void UpdateSheet(LyricSheet sheet)
    {
        var changed = !string.Equals(_sheet?.Key, sheet.Key, StringComparison.Ordinal);
        _sheet = sheet;

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

    protected override void DrawBody(ID2D1DeviceContext context)
    {
        var sheet = _sheet;

        DrawHeader(context, sheet);

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

        if (sheet.LineCount > 0)
        {
            Badge(context, LyricText.FormatLineCount(sheet.LineCount), right, 26f, ColorTextDim, 0x1AFFFFFF);
        }

        var record = sheet.RecordId is { } id ? $"lrclib #{id}" : "No record";

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

        if (!string.IsNullOrWhiteSpace(sheet.Status))
        {
            Label(
                context,
                LyricText.Fit(sheet.Status, 58),
                WidthDip - Pad - 440f,
                66f,
                440f,
                18f,
                11f,
                FontWeight.Normal,
                ColorWarning,
                TextAlignment.Trailing);
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

    private void MeasureIfNeeded(LyricSheet sheet)
    {
        if (_measured)
        {
            return;
        }

        _measured = true;
        _lines.Clear();
        _prefix.Clear();
        _heights.Clear();

        _activeIndex = sheet.IsSynced ? LyricText.ActiveLine(sheet.Lines, sheet.Position) : -1;

        var total = 0f;

        for (var i = 0; i < sheet.Lines.Count; i++)
        {
            var line = sheet.Lines[i];
            var active = i == _activeIndex;
            var size = active ? ActiveFontSize : InactiveFontSize;
            var weight = active ? FontWeight.SemiBold : FontWeight.Normal;
            var text = line.Text.Length == 0 ? " " : line.Text;
            var height = MathF.Max(26f, MeasureHeight(text, TextWidth, size, weight) + 12f);

            _lines.Add(new DisplayLine(
                sheet.IsSynced ? LyricText.FormatTimestamp(line.Start) : "",
                text,
                active));

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
            { Lines.Count: 0 } => "Try Choose lyrics in the overlay to look at another record.",
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
    }

    private void DrawFooter(ID2D1DeviceContext context, LyricSheet? sheet)
    {
        Fill(context, R(Pad, ListBottom, WidthDip - Pad, ListBottom + 1f), ColorLine);

        var left = sheet is { LineCount: > 0 }
            ? "Read-only for now. Editing this sheet and uploading it to LRCLIB will live here."
            : "Esc closes.";

        Label(
            context,
            left,
            Pad,
            ListBottom + 9f,
            WidthDip - (2 * Pad) - 260f,
            20f,
            11f,
            FontWeight.Normal,
            ColorTextFaint);

        var right = sheet is { LineCount: > 0 }
            ? $"{sheet.LineCount} lines"
            : "Esc closes";

        Label(
            context,
            right,
            WidthDip - Pad - 240f,
            ListBottom + 9f,
            240f,
            20f,
            11f,
            FontWeight.Normal,
            ColorTextFaint,
            TextAlignment.Trailing);
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

    protected override void OnBodyWheel(int notches)
    {
        var viewport = ListBottom - ListTop;
        var maxScroll = Math.Max(0f, _contentHeight - viewport);

        if (maxScroll <= 0f || notches == 0)
        {
            return;
        }

        var steps = Math.Max(1, Math.Abs(notches) / 120);
        NextScroll(_scroll + (notches > 0 ? -72f : 72f) * steps, maxScroll);
    }

    protected override bool OnBodyKey(int virtualKey)
    {
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
}
