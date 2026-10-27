using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Overlyric.Overlay;

public enum OverlayMode
{
    /// <summary>Only the line being sung right now, in a slim pill.</summary>
    SingleLine = 0,

    /// <summary>A block of surrounding lines with the active one emphasised.</summary>
    MultiLine = 1,

    /// <summary>
    /// One active line with the next line underneath it, smaller and dimmed. It is the middle ground
    /// between a single line and the full five-line block, and it uses the multi-line drawing path.
    /// </summary>
    SingleLineWithNext = 2
}

/// <summary>The actions on the bar that appears over an unlocked overlay.</summary>
public enum OverlayControl
{
    /// <summary>The layout readout, which cycles the mode the way Ctrl+Alt+M does.</summary>
    Layout,

    Settings,
    OffsetBack,
    OffsetReset,
    OffsetForward,
    ViewLyrics,
    ChooseLyrics,

    /// <summary>Lock the overlay. It only exists here because the bar only exists unlocked.</summary>
    Lock,

    /// <summary>Play or pause the current session through Windows media controls.</summary>
    PlayPause,

    /// <summary>Skip to the next track through Windows media controls.</summary>
    Next
}

/// <summary>One live region of the control bar, in device-independent window coordinates.</summary>
public readonly record struct OverlayControlHit(
    OverlayControl Control,
    float Left,
    float Top,
    float Right,
    float Bottom)
{
    public bool Contains(float x, float y) =>
        x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>Visual configuration. Colours are 0xAARRGGBB.</summary>
public sealed class OverlayTheme
{
    public string FontFamily { get; init; } = "Segoe UI Variable Display";
    public string FallbackFontFamily { get; init; } = "Segoe UI";

    public float ActiveFontSize { get; init; } = 30f;
    public float InactiveFontSize { get; init; } = 21f;
    public float HeaderFontSize { get; init; } = 13f;

    /// <summary>Weight of the line being sung; the surrounding lines stay normal.</summary>
    public FontWeight ActiveFontWeight { get; init; } = FontWeight.SemiBold;

    /// <summary>
    /// Full-monitor audience mode: the renderer fills the whole window with the backdrop and centres
    /// the lyric block, instead of drawing a pill over the desktop.
    /// </summary>
    public bool StageMode { get; init; }

    public uint BackgroundColor { get; init; } = 0xB4101014;
    public uint ActiveTextColor { get; init; } = 0xFFF5F7FA;
    public uint InactiveTextColor { get; init; } = 0x8AF5F7FA;

    /// <summary>
    /// Colour of the part of the active line the sweep has not reached yet. Normally the same dim
    /// colour as the surrounding lines; the OLED preset turns it pure black, which on the panel is
    /// an unlit pixel, so only the sung part of the line emits light.
    /// </summary>
    public uint KaraokeUnlitColor { get; init; } = 0x8AF5F7FA;

    public uint HeaderTextColor { get; init; } = 0x99F5F7FA;
    public uint ShadowColor { get; init; } = 0xC0000000;
    public uint ProgressColor { get; init; } = 0xCC7CC4FF;
    public uint ProgressTrackColor { get; init; } = 0x33FFFFFF;

    /// <summary>
    /// Backdrop of the transient notice. It is drawn even when the lyric backdrop is fully
    /// transparent, because the notice is the one thing on an OLED screen whose whole point is that
    /// it goes away again - and it has to stay legible over whatever is behind it until then.
    /// </summary>
    public uint NoticeBackgroundColor { get; init; } = 0xF00C0E12;

    public uint NoticeTitleColor { get; init; } = 0xFFF5F7FA;

    /// <summary>Detail colour for <see cref="NoticeTone.Info"/>.</summary>
    public uint NoticeAccentColor { get; init; } = 0xFF7CC4FF;

    /// <summary>Detail colour for <see cref="NoticeTone.Warning"/>.</summary>
    public uint NoticeWarningColor { get; init; } = 0xFFE0A458;

    public float NoticeTitleFontSize { get; init; } = 13.5f;
    public float NoticeDetailFontSize { get; init; } = 12f;
    public float NoticePaddingX { get; init; } = 14f;
    public float NoticePaddingY { get; init; } = 8f;

    public float CornerRadius { get; init; } = 14f;
    public float PaddingX { get; init; } = 26f;
    public float PaddingY { get; init; } = 12f;
    public float LineSpacing { get; init; } = 6f;
    public float ProgressBarHeight { get; init; } = 3f;
    public float ProgressBarInset { get; init; } = 10f;

    /// <summary>
    /// Backdrop of the control bar. It is drawn even when the lyric backdrop has been turned
    /// transparent: the bar is what lets the user act on the lyrics, so it has to stay readable over
    /// whatever is behind the overlay.
    /// </summary>
    public uint ControlBarColor { get; init; } = 0xE80B0D12;

    public uint ControlButtonColor { get; init; } = 0x14FFFFFF;

    public uint ControlHoverColor { get; init; } = 0x33FFFFFF;

    public uint ControlTextColor { get; init; } = 0xFFF5F7FA;

    public uint ControlMutedColor { get; init; } = 0x99F5F7FA;

    public uint ControlAccentColor { get; init; } = 0xFF7CC4FF;

    public uint ControlDividerColor { get; init; } = 0x22FFFFFF;

    /// <summary>Height of the button row, before its own padding.</summary>
    public float ControlHeight { get; init; } = 32f;

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

    /// <summary>
    /// A second-language line shown under the active lyric, from a user-supplied sidecar. Null when
    /// the track has none or the option is off.
    /// </summary>
    public string? Translation { get; init; }

    /// <summary>True for the full-monitor audience surface; the renderer then paints the whole window.</summary>
    public bool StageMode { get; init; }

    /// <summary>Album art for the info pop-up, premultiplied BGRA8; null when the player has none.</summary>
    public byte[]? Artwork { get; init; }

    public int ArtworkWidth { get; init; }

    public int ArtworkHeight { get; init; }

    /// <summary>Status text shown instead of lyrics, for example "No lyrics found".</summary>
    public string? Status { get; init; }

    /// <summary>
    /// Transient message drawn over the top of the content: the song that just started and what the
    /// lyric lookup found. Null when nothing is being announced. The renderer places it where it
    /// does not collide with the lyrics and gives it its own backdrop, so it survives the OLED
    /// preset's transparent backdrop.
    /// </summary>
    public OverlayNotice? Notice { get; init; }

    public bool ShowProgressBar { get; init; }
    public bool ShowHeader { get; init; }

    /// <summary>0..1 progress through the active line; negative when unknown.</summary>
    public float LineProgress { get; init; } = -1f;

    /// <summary>0..1 progress through the whole track; negative when unknown.</summary>
    public float TrackProgress { get; init; } = -1f;

    /// <summary>
    /// 0..1 progress through a wait before the next line: an intro, or an instrumental interlude.
    /// Negative when the current line is being sung, so the renderer can skip the indicator entirely.
    /// </summary>
    public float GapProgress { get; init; } = -1f;

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

    /// <summary>
    /// True while the mouse is over an unlocked overlay. The bar is deliberately a property of the
    /// frame rather than of the renderer, so hovering repaints without disturbing the lyric state.
    /// </summary>
    public bool ShowControls { get; init; }

    /// <summary>Current timing correction, as the bar prints it: "0.00 s", "+0.25 s".</summary>
    public string OffsetLabel { get; init; } = "0.00 s";

    /// <summary>Dim the reset affordance when there is nothing to reset.</summary>
    public bool OffsetIsDefault { get; init; } = true;

    /// <summary>Which bar button the mouse is over, if any.</summary>
    public OverlayControl? HoveredControl { get; init; }

    /// <summary>The session is playing, so the transport button offers Pause rather than Play.</summary>
    public bool IsPlaying { get; init; }
}
