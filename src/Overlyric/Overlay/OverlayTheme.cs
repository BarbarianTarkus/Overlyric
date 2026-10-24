using Vortice.Mathematics;

namespace Overlyric.Overlay;

public enum OverlayMode
{
    /// <summary>Only the line being sung right now, in a slim pill.</summary>
    SingleLine = 0,

    /// <summary>A block of surrounding lines with the active one emphasised.</summary>
    MultiLine = 1
}

/// <summary>Visual configuration. Colours are 0xAARRGGBB.</summary>
public sealed class OverlayTheme
{
    public string FontFamily { get; init; } = "Segoe UI Variable Display";
    public string FallbackFontFamily { get; init; } = "Segoe UI";

    public float ActiveFontSize { get; init; } = 30f;
    public float InactiveFontSize { get; init; } = 21f;
    public float HeaderFontSize { get; init; } = 13f;

    public uint BackgroundColor { get; init; } = 0xB4101014;
    public uint ActiveTextColor { get; init; } = 0xFFF5F7FA;
    public uint InactiveTextColor { get; init; } = 0x8AF5F7FA;
    public uint HeaderTextColor { get; init; } = 0x99F5F7FA;
    public uint ShadowColor { get; init; } = 0xC0000000;
    public uint ProgressColor { get; init; } = 0xCC7CC4FF;
    public uint ProgressTrackColor { get; init; } = 0x33FFFFFF;

    public float CornerRadius { get; init; } = 14f;
    public float PaddingX { get; init; } = 26f;
    public float PaddingY { get; init; } = 12f;
    public float LineSpacing { get; init; } = 6f;
    public float ProgressBarHeight { get; init; } = 3f;
    public float ProgressBarInset { get; init; } = 10f;

    /// <summary>How many lyric lines a multi-line overlay shows at once.</summary>
    public int MultiLineCount { get; init; } = 5;

    /// <summary>Draw the highlight bar behind the line being sung.</summary>
    public bool ShowActiveLineHighlight { get; init; } = true;

    /// <summary>Draw a hard outline around the text instead of a soft shadow.</summary>
    public bool TextOutline { get; init; }

    public static Color4 ToColor4(uint argb) => new(
        ((argb >> 16) & 0xFF) / 255f,
        ((argb >> 8) & 0xFF) / 255f,
        (argb & 0xFF) / 255f,
        ((argb >> 24) & 0xFF) / 255f);
}

/// <summary>Everything the renderer needs for one frame. Produced on the UI thread.</summary>
public sealed record OverlayVisual
{
    public OverlayMode Mode { get; init; } = OverlayMode.SingleLine;

    /// <summary>The lines to draw, already sliced to the visible window.</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>Index into <see cref="Lines"/> of the line currently being sung, or -1.</summary>
    public int ActiveIndex { get; init; } = -1;

    /// <summary>Small dim label drawn above the lyrics, normally "Artist - Title".</summary>
    public string? Header { get; init; }

    /// <summary>Status text shown instead of lyrics, for example "No lyrics found".</summary>
    public string? Status { get; init; }

    public bool ShowProgressBar { get; init; }
    public bool ShowHeader { get; init; }

    /// <summary>0..1 progress through the active line; negative when unknown.</summary>
    public float LineProgress { get; init; } = -1f;

    /// <summary>0..1 progress through the whole track; negative when unknown.</summary>
    public float TrackProgress { get; init; } = -1f;

    /// <summary>
    /// How many characters of the active line have been sung, or -1 when the sweep is off. The
    /// renderer turns this into a clip; the counting itself lives in <c>KaraokeSweep</c>.
    /// </summary>
    public int SungChars { get; init; } = -1;

    /// <summary>
    /// Burn-in nudge in device-independent pixels, applied to everything drawn. Moving the drawing
    /// rather than the window keeps the window's own hit testing and placement untouched.
    /// </summary>
    public float OffsetX { get; init; }

    public float OffsetY { get; init; }
}
