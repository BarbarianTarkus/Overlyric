using System.Drawing;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Size = Vortice.Mathematics.Size;

namespace Overlyric.Overlay;

/// <summary>
/// Draws lyric frames with Direct2D and DirectWrite.
/// <para>
/// Text formats, brushes and text layouts are all created once and reused. Layouts are cached per
/// line, so a redraw caused by the progress bar advancing costs no allocations at all, and only a
/// genuine line change creates a new one. Every DirectWrite object is owned by this class and
/// released in <see cref="Dispose"/>.
/// </para>
/// </summary>
internal sealed class LyricsRenderer : IDisposable
{
    /// <summary>Upper bound on cached layouts; a long track is comfortably under this.</summary>
    private const int LayoutCacheLimit = 256;

    private readonly IDWriteFactory _writeFactory;
    private OverlayTheme _theme;
    private readonly Dictionary<(float Size, FontWeight Weight), IDWriteTextFormat> _formats = [];
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = [];
    private readonly Dictionary<(string Text, float Size, FontWeight Weight), IDWriteTextLayout> _layouts = [];

    private float _layoutWidth = -1f;
    private float _layoutHeight = 200f;
    private bool _disposed;

    public LyricsRenderer(IDWriteFactory writeFactory, OverlayTheme theme)
    {
        _writeFactory = writeFactory;
        _theme = theme;
    }

    /// <summary>Bounds of the drawn content in device-independent pixels, for hit testing.</summary>
    public Rect ContentBounds { get; private set; }

    /// <summary>
    /// Adopt a new theme without rebuilding the renderer. Text formats, cached layouts and brushes
    /// all bake in the old sizes and colours, so they are released rather than reused.
    /// </summary>
    public void SetTheme(OverlayTheme theme)
    {
        _theme = theme;

        // Layouts reference formats, so they go first.
        ClearLayouts();

        foreach (var brush in _brushes.Values)
        {
            brush.Dispose();
        }

        _brushes.Clear();

        foreach (var format in _formats.Values)
        {
            format.Dispose();
        }

        _formats.Clear();

        _layoutWidth = -1f;
    }

    public void Draw(ID2D1DeviceContext context, Size viewport, OverlayVisual visual)
    {
        ContentBounds = default;

        var width = viewport.Width;
        var height = viewport.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _layoutHeight = height;

        var margin = 8f;
        var available = Math.Max(32f, width - (2 * margin));

        // Layouts bake in their width constraint, so a resize invalidates all of them.
        if (Math.Abs(available - _layoutWidth) > 0.01f)
        {
            ClearLayouts();
            _layoutWidth = available;
        }

        if (visual.Mode == OverlayMode.SingleLine)
        {
            DrawSingleLine(context, visual, width, height, margin, available);
        }
        else
        {
            DrawMultiLine(context, visual, width, height, margin, available);
        }
    }

    private void DrawSingleLine(
        ID2D1DeviceContext context,
        OverlayVisual visual,
        float width,
        float height,
        float margin,
        float available)
    {
        var text = ResolveSingleLineText(visual);
        if (text.Length == 0)
        {
            return;
        }

        var lineLayout = GetLayout(text, _theme.ActiveFontSize, FontWeight.SemiBold);

        var hasHeader = visual.ShowHeader && !string.IsNullOrWhiteSpace(visual.Header);
        var headerLayout = hasHeader
            ? GetLayout(visual.Header!, _theme.HeaderFontSize, FontWeight.Normal)
            : null;

        var showProgress = visual.ShowProgressBar && ResolveProgress(visual) >= 0f;
        var progressExtra = showProgress ? _theme.ProgressBarHeight + _theme.ProgressBarInset : 0f;

        var lineWidth = Math.Min(lineLayout.Metrics.Width, available);
        var pillWidth = Math.Min(lineWidth + (2 * _theme.PaddingX), width);
        var pillHeight = lineLayout.Metrics.Height + (2 * _theme.PaddingY) + progressExtra;

        var headerHeight = 0f;
        var headerWidth = 0f;
        if (headerLayout is not null)
        {
            headerHeight = headerLayout.Metrics.Height + (2 * (_theme.PaddingY * 0.55f)) + 4f;
            headerWidth = Math.Min(headerLayout.Metrics.Width + (2 * _theme.PaddingX * 0.8f), width);
        }

        var totalHeight = pillHeight + headerHeight;
        var top = (height - totalHeight) / 2f;

        var left = (width - pillWidth) / 2f;
        var contentLeft = left;
        var contentRight = left + pillWidth;

        if (headerLayout is not null)
        {
            var headerLeft = (width - headerWidth) / 2f;
            context.FillRoundedRectangle(
                Rounded(headerLeft, top, headerWidth, headerHeight - 4f, _theme.CornerRadius * 0.7f),
                GetBrush(context, _theme.BackgroundColor));

            DrawTextShadows(
                context,
                headerLayout,
                new Vector2(margin, top + (_theme.PaddingY * 0.55f)),
                GetBrush(context, _theme.HeaderTextColor));

            top += headerHeight;
            contentLeft = Math.Min(contentLeft, headerLeft);
            contentRight = Math.Max(contentRight, headerLeft + headerWidth);
        }

        context.FillRoundedRectangle(
            Rounded(left, top, pillWidth, pillHeight, _theme.CornerRadius),
            GetBrush(context, _theme.BackgroundColor));

        if (showProgress)
        {
            DrawProgressBar(context, left, top, pillWidth, pillHeight, ResolveProgress(visual));
        }

        // Drawn from `margin`, not from the pill edge: the layout centres text inside its own
        // box, so the origin must be the same box the width was measured against.
        DrawTextShadows(
            context,
            lineLayout,
            new Vector2(margin, top + _theme.PaddingY),
            GetBrush(context, _theme.ActiveTextColor));

        ContentBounds = new Rect(RectangleF.FromLTRB(contentLeft, top - headerHeight, contentRight, top + pillHeight));
    }

    private void DrawMultiLine(
        ID2D1DeviceContext context,
        OverlayVisual visual,
        float width,
        float height,
        float margin,
        float available)
    {
        var lines = visual.Lines;
        if (lines.Count == 0)
        {
            DrawSingleLine(context, visual, width, height, margin, available);
            return;
        }

        var layouts = new IDWriteTextLayout[lines.Count];
        var heights = new float[lines.Count];

        var contentWidth = 0f;
        var contentHeight = 0f;

        for (var i = 0; i < lines.Count; i++)
        {
            var isActive = i == visual.ActiveIndex;
            var text = lines[i].Length == 0 ? " " : lines[i];

            var layout = GetLayout(
                text,
                isActive ? _theme.ActiveFontSize : _theme.InactiveFontSize,
                isActive ? FontWeight.SemiBold : FontWeight.Normal);

            layouts[i] = layout;
            heights[i] = layout.Metrics.Height;

            contentWidth = Math.Max(contentWidth, layout.Metrics.Width);
            contentHeight += heights[i];
            if (i < lines.Count - 1)
            {
                contentHeight += _theme.LineSpacing;
            }
        }

        IDWriteTextLayout? headerLayout = null;
        var headerBlockHeight = 0f;
        if (visual.ShowHeader && !string.IsNullOrWhiteSpace(visual.Header))
        {
            headerLayout = GetLayout(visual.Header!, _theme.HeaderFontSize, FontWeight.Normal);
            headerBlockHeight = headerLayout.Metrics.Height + _theme.LineSpacing + 2f;
            contentWidth = Math.Max(contentWidth, headerLayout.Metrics.Width);
        }

        var showProgress = visual.ShowProgressBar && ResolveProgress(visual) >= 0f;
        var progressExtra = showProgress ? _theme.ProgressBarHeight + _theme.ProgressBarInset : 0f;

        var boxWidth = Math.Min(contentWidth + (2 * _theme.PaddingX), width);
        var boxHeight = contentHeight + headerBlockHeight + (2 * _theme.PaddingY) + progressExtra;
        var left = (width - boxWidth) / 2f;
        var top = (height - boxHeight) / 2f;

        context.FillRoundedRectangle(
            Rounded(left, top, boxWidth, boxHeight, _theme.CornerRadius),
            GetBrush(context, _theme.BackgroundColor));

        var y = top + _theme.PaddingY;

        if (headerLayout is not null)
        {
            DrawTextShadows(
                context,
                headerLayout,
                new Vector2(margin, y),
                GetBrush(context, _theme.HeaderTextColor));
            y += headerBlockHeight;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var isActive = i == visual.ActiveIndex;

            if (isActive)
            {
                var highlight = Rounded(
                    left + 6f,
                    y - 3f,
                    boxWidth - 12f,
                    heights[i] + 6f,
                    _theme.CornerRadius * 0.5f);
                context.FillRoundedRectangle(highlight, GetBrush(context, 0x1FFFFFFF));
            }

            DrawTextShadows(
                context,
                layouts[i],
                new Vector2(margin, y),
                GetBrush(context, isActive ? _theme.ActiveTextColor : _theme.InactiveTextColor));

            y += heights[i] + _theme.LineSpacing;
        }

        if (showProgress)
        {
            DrawProgressBar(context, left, top, boxWidth, boxHeight, ResolveProgress(visual));
        }

        ContentBounds = new Rect(RectangleF.FromLTRB(left, top, left + boxWidth, top + boxHeight));
    }

    /// <summary>
    /// Text is drawn five times, four of them offset as a soft shadow, so lyrics stay readable over
    /// bright or busy backgrounds such as games and video.
    /// </summary>
    private void DrawTextShadows(
        ID2D1DeviceContext context,
        IDWriteTextLayout layout,
        Vector2 origin,
        ID2D1SolidColorBrush foreground)
    {
        var shadow = GetBrush(context, _theme.ShadowColor);

        context.DrawTextLayout(origin + new Vector2(0f, 1.5f), layout, shadow);
        context.DrawTextLayout(origin + new Vector2(0f, -1f), layout, shadow);
        context.DrawTextLayout(origin + new Vector2(1.5f, 0f), layout, shadow);
        context.DrawTextLayout(origin + new Vector2(-1f, 0f), layout, shadow);
        context.DrawTextLayout(origin, layout, foreground);
    }

    private void DrawProgressBar(
        ID2D1DeviceContext context,
        float left,
        float top,
        float width,
        float height,
        float progress)
    {
        var inset = _theme.ProgressBarInset;
        var barWidth = Math.Max(0f, width - (2 * inset));
        var barHeight = _theme.ProgressBarHeight;
        var barTop = top + height - inset - barHeight;

        if (barWidth <= 0f || barHeight <= 0f)
        {
            return;
        }

        var radius = barHeight / 2f;

        context.FillRoundedRectangle(
            Rounded(left + inset, barTop, barWidth, barHeight, radius),
            GetBrush(context, _theme.ProgressTrackColor));

        var filled = Math.Clamp(progress, 0f, 1f) * barWidth;
        if (filled <= 0.5f)
        {
            return;
        }

        filled = Math.Max(filled, barHeight);

        context.FillRoundedRectangle(
            Rounded(left + inset, barTop, filled, barHeight, radius),
            GetBrush(context, _theme.ProgressColor));
    }

    private static string ResolveSingleLineText(OverlayVisual visual)
    {
        if (!string.IsNullOrWhiteSpace(visual.Status))
        {
            return visual.Status!;
        }

        if (visual.Lines.Count == 0)
        {
            return string.Empty;
        }

        var index = visual.ActiveIndex;
        if (index < 0 || index >= visual.Lines.Count)
        {
            index = 0;
        }

        return visual.Lines[index];
    }

    /// <summary>Prefers whole-track progress, falling back to progress within the active line.</summary>
    private static float ResolveProgress(OverlayVisual visual) =>
        visual.TrackProgress >= 0f ? visual.TrackProgress : visual.LineProgress;

    private static RoundedRectangle Rounded(float left, float top, float width, float height, float radius) =>
        new(new RectangleF(left, top, width, height), radius, radius);

    /// <summary>
    /// Returns a cached layout for the text, creating one only when the text is new. This is what
    /// keeps a progress-bar redraw allocation-free.
    /// </summary>
    private IDWriteTextLayout GetLayout(string text, float size, FontWeight weight)
    {
        var key = (text, size, weight);
        if (_layouts.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_layouts.Count >= LayoutCacheLimit)
        {
            ClearLayouts();
        }

        var layout = _writeFactory.CreateTextLayout(
            text,
            GetFormat(size, weight),
            Math.Max(32f, _layoutWidth),
            _layoutHeight);

        _layouts[key] = layout;
        return layout;
    }

    private void ClearLayouts()
    {
        foreach (var layout in _layouts.Values)
        {
            layout.Dispose();
        }

        _layouts.Clear();
    }

    private IDWriteTextFormat GetFormat(float size, FontWeight weight)
    {
        var key = (size, weight);
        if (_formats.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var format = _writeFactory.CreateTextFormat(
            _theme.FontFamily,
            null!,
            weight,
            FontStyle.Normal,
            FontStretch.Normal,
            size);

        format.TextAlignment = TextAlignment.Center;
        format.ParagraphAlignment = ParagraphAlignment.Near;

        _formats[key] = format;
        return format;
    }

    private ID2D1SolidColorBrush GetBrush(ID2D1DeviceContext context, uint argb)
    {
        if (_brushes.TryGetValue(argb, out var cached))
        {
            return cached;
        }

        var brush = context.CreateSolidColorBrush(OverlayTheme.ToColor4(argb));
        _brushes[argb] = brush;
        return brush;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        ClearLayouts();

        foreach (var brush in _brushes.Values)
        {
            brush.Dispose();
        }

        foreach (var format in _formats.Values)
        {
            format.Dispose();
        }

        _brushes.Clear();
        _formats.Clear();
    }
}
