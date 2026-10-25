namespace Overlyric.App;

/// <summary>Which Windows notification the lyric lookup warrants, if any.</summary>
internal enum LyricsAlertKind
{
    None = 0,

    /// <summary>lrclib has no record with lyrics for this track.</summary>
    NoLyrics,

    /// <summary>The words exist, but the record carries no timings, so they cannot follow the music.</summary>
    NoSyncedLyrics
}

/// <summary>
/// Decides when a lyric lookup is worth a Windows notification, and what it should say.
/// </summary>
/// <remarks>
/// The two cases are different answers and both are invisible otherwise: with "hide when no lyrics"
/// on - the default - the overlay is simply absent, and untimed lyrics show up as text that never
/// follows the song. Neither is a reason to notify on every retry or on every poll, so the host asks
/// this per track (not per fetch) and only after the lookup has settled.
/// <para>
/// <c>definitive</c> is the distinction that keeps the notification honest. lrclib throttles bursts
/// and regularly fails with <c>503</c>; a failed request is not evidence that a track has no lyrics,
/// and announcing "no lyrics found" for one would be a lie the retry then contradicts. Only a
/// definitive absence is announced, which is also the rule the cache tombstones.
/// </para>
/// </remarks>
internal static class LyricsAlert
{
    public static LyricsAlertKind Decide(bool definitive, bool isInstrumental, bool hasLines, bool isSynced)
    {
        if (isInstrumental)
        {
            // "Nothing to sing" is an answer, not a failure, and the overlay already says so.
            return LyricsAlertKind.None;
        }

        if (!hasLines)
        {
            return definitive ? LyricsAlertKind.NoLyrics : LyricsAlertKind.None;
        }

        return isSynced ? LyricsAlertKind.None : LyricsAlertKind.NoSyncedLyrics;
    }

    /// <summary>The tray notification's title and body for a kind, or empty strings for none.</summary>
    public static (string Title, string Text) Describe(LyricsAlertKind kind, string track) => kind switch
    {
        LyricsAlertKind.NoLyrics => (
            "Overlyric \u2014 no lyrics found",
            $"lrclib has no lyrics for \u201c{track}\u201d, so there is nothing to show for it."),
        LyricsAlertKind.NoSyncedLyrics => (
            "Overlyric \u2014 lyrics are not synced",
            $"lrclib has the words for \u201c{track}\u201d but no timings, so they cannot follow the music."),
        _ => ("", "")
    };
}
