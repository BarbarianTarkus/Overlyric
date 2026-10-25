using Overlyric.Overlay;

namespace Overlyric.App;

/// <summary>
/// Turns what the lyric lookup and the player have told us into the one line the info pop-up shows
/// beneath the track name - whether the sheet carries word timings, only line timings, no timings at
/// all, or nothing at all.
/// </summary>
/// <remarks>
/// The precedence follows <see cref="AppHost.BuildVisual"/> rather than inventing a second one: an
/// instrumental answer outranks a missing playback position, because there is nothing to place in
/// time either way, and a player that publishes no position is worth naming only when there is
/// something the position would have moved.
/// </remarks>
internal static class LyricStatus
{
    public const string LookingUpDetail = "Looking up lyrics\u2026";
    public const string InstrumentalDetail = "Instrumental \u2014 no lyrics to sync";
    public const string NoLyricsDetail = "No lyrics found";
    public const string NoPositionDetail = "Player reports no playback position";
    public const string NotSyncedDetail = "Lyrics not synced";
    public const string WordSyncedDetail = "Word-synced lyrics";
    public const string SyncedDetail = "Synced lyrics (line timings)";

    /// <summary>The detail line and tone for the current state of the world.</summary>
    public static (string Detail, NoticeTone Tone) Describe(
        bool lookupComplete,
        bool isInstrumental,
        bool hasLines,
        bool isSynced,
        bool hasWordTimings,
        bool positionIsUsable)
    {
        if (!lookupComplete)
        {
            return (LookingUpDetail, NoticeTone.Info);
        }

        if (isInstrumental)
        {
            return (InstrumentalDetail, NoticeTone.Info);
        }

        if (!hasLines)
        {
            return (NoLyricsDetail, NoticeTone.Warning);
        }

        if (!positionIsUsable)
        {
            return (NoPositionDetail, NoticeTone.Warning);
        }

        if (!isSynced)
        {
            return (NotSyncedDetail, NoticeTone.Warning);
        }

        return hasWordTimings
            ? (WordSyncedDetail, NoticeTone.Info)
            : (SyncedDetail, NoticeTone.Info);
    }
}
