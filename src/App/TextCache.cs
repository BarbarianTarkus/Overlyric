using System.Drawing;
using System.Numerics;
using Overlyric.Overlay;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Overlyric.App;

/// <summary>
/// The brushes, text formats and measured widths the custom-drawn windows reuse between frames.
/// </summary>
/// <remarks>
/// One cache per window, created lazily once the composition host exists and dropped with it when a
/// device is lost. It lives here rather than in each window because the same six primitives are what
/// the settings window, the panels and the launcher all need; keeping one copy is also what keeps
/// the two menus from drifting apart.
/// </remarks>
internal sealed class TextCache : IDisposable
{
    private readonly IDWriteFactory _writeFactory;
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = [];
    private readonly Dictionary<(float Size, FontWeight Weight, TextAlignment Align, ParagraphAlignment Paragraph, WordWrapping Wrap), IDWriteTextFormat> _formats = [];
    private readonly Dictionary<(string Text, float Size, FontWeight Weight), float> _widths = [];
    private bool _disposed;

    public TextCache(IDWriteFactory writeFactory)
    {
        ArgumentNullException.ThrowIfNull(writeFactory);
        _writeFactory = writeFactory;
    }

    public void Fill(ID2D1DeviceContext context, Rect rect, uint argb) =>
        context.FillRectangle(rect, Brush(context, argb));

    public void FillRound(
        ID2D1DeviceContext context,
        float left,
        float top,
        float width,
        float height,
        float radius,
        uint argb) =>
        context.FillRoundedRectangle(
            new RoundedRectangle(new RectangleF(left, top, width, height), radius, radius),
            Brush(context, argb));

    public void Label(
        ID2D1DeviceContext context,
        string text,
        float left,
        float top,
        float width,
        float height,
        float size,
        FontWeight weight,
        uint argb,
        TextAlignment align = TextAlignment.Leading,
        ParagraphAlignment paragraph = ParagraphAlignment.Center,
        WordWrapping wrap = WordWrapping.NoWrap)
    {
        if (width <= 0f || height <= 0f)
        {
            return;
        }

        var format = Format(size, weight, align, paragraph, wrap);
        var layout = _writeFactory.CreateTextLayout(text, format, width, height);
        context.DrawTextLayout(new Vector2(left, top), layout, Brush(context, argb));
        layout.Dispose();
    }

    public float Measure(string text, float size, FontWeight weight)
    {
        var key = (text, size, weight);

        if (_widths.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var format = Format(size, weight, TextAlignment.Leading, ParagraphAlignment.Near, WordWrapping.NoWrap);
        var layout = _writeFactory.CreateTextLayout(text, format, 4096f, 100f);
        var width = layout.Metrics.Width;
        layout.Dispose();

        _widths[key] = width;
        return width;
    }

    /// <summary>Measures the wrapped height of a block of text; used by the viewer's line layout.</summary>
    public float MeasureHeight(string text, float width, float size, FontWeight weight)
    {
        var format = Format(size, weight, TextAlignment.Leading, ParagraphAlignment.Near, WordWrapping.Wrap);
        var layout = _writeFactory.CreateTextLayout(text, format, Math.Max(16f, width), 10000f);
        var height = layout.Metrics.Height;
        layout.Dispose();
        return height;
    }

    public IDWriteTextFormat Format(
        float size,
        FontWeight weight,
        TextAlignment align,
        ParagraphAlignment paragraph = ParagraphAlignment.Center,
        WordWrapping wrap = WordWrapping.NoWrap)
    {
        var key = (size, weight, align, paragraph, wrap);

        if (_formats.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var format = _writeFactory.CreateTextFormat(
            "Segoe UI Variable Display",
            null!,
            weight,
            FontStyle.Normal,
            FontStretch.Normal,
            size);

        format.TextAlignment = align;
        format.ParagraphAlignment = paragraph;
        format.WordWrapping = wrap;

        _formats[key] = format;
        return format;
    }

    public ID2D1SolidColorBrush Brush(ID2D1DeviceContext context, uint argb)
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
        _widths.Clear();
    }
}
