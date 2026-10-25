namespace Overlyric.Overlay;

/// <summary>How a transient notice should read: information, or something that needs attention.</summary>
public enum NoticeTone
{
    /// <summary>The answer is what the user wants to hear, or at least neutral.</summary>
    Info = 0,

    /// <summary>Lyrics are missing, untimed, or the player is not saying where it is.</summary>
    Warning = 1
}

/// <summary>
/// A short-lived message drawn inside the overlay itself: the track that has just started and what
/// the lyric lookup found for it - word timings, line timings only, no timings, or nothing at all.
/// </summary>
/// <remarks>
/// This is deliberately separate from <see cref="OverlayVisual.Status"/>, which replaces the lyric
/// line for as long as the condition lasts. A notice is an answer to "what is happening", so it is
/// time-boxed by the host and disappears on its own; the status is a steady state.
/// </remarks>
public sealed record OverlayNotice
{
    /// <summary>The song, normally <c>Artist - Title</c>.</summary>
    public string Title { get; init; } = "";

    /// <summary>One line describing the lyric sheet, for example <c>Word-synced lyrics</c>.</summary>
    public string Detail { get; init; } = "";

    public NoticeTone Tone { get; init; } = NoticeTone.Info;

    public bool IsEmpty => Title.Length == 0 && Detail.Length == 0;
}
