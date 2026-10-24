namespace Overlyric.Media;

/// <summary>
/// Immutable description of what Windows says is playing right now.
/// Deliberately excludes position: that is read live from <see cref="PlaybackClock"/>
/// because it must advance between polls.
/// </summary>
public sealed record NowPlayingSnapshot
{
    public static readonly NowPlayingSnapshot Empty = new();

    public bool HasSession { get; init; }
    public string SourceApp { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string? Album { get; init; }
    public double? DurationSeconds { get; init; }
    public bool IsPlaying { get; init; }

    /// <summary>
    /// True when this player actually publishes a progressing SMTC timeline.
    /// False for TIDAL, which reports a frozen position and raises no events,
    /// forcing Overlyric to self-clock from the position its frozen stamp implies.
    /// </summary>
    public bool TimelineIsLive { get; init; }

    /// <summary>Increments once per track change, so consumers can detect transitions cheaply.</summary>
    public long Revision { get; init; }

    public bool HasTrack => HasSession && (!string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Artist));

    /// <summary>Human-readable player name derived from the app user model id.</summary>
    public string SourceAppFriendly => FriendlyNameFor(SourceApp);

    /// <summary>
    /// The same mapping for a bare app user model id, because the source menus list every player that
    /// is publishing - not only the one being followed - so they cannot read the name off a snapshot.
    /// </summary>
    public static string FriendlyNameFor(string? aumid) => (aumid ?? "") switch
    {
        "" => "",
        "com.squirrel.TIDAL.TIDAL" => "TIDAL",
        "Spotify.exe" or "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" => "Spotify",
        var s when s.Contains("Spotify", StringComparison.OrdinalIgnoreCase) => "Spotify",
        var s when s.Contains("TIDAL", StringComparison.OrdinalIgnoreCase) => "TIDAL",
        var s when s.Contains("AIMP", StringComparison.OrdinalIgnoreCase) => "AIMP",
        var s when s.Contains("chrome", StringComparison.OrdinalIgnoreCase) => "Chrome",
        var s when s.Contains("msedge", StringComparison.OrdinalIgnoreCase) => "Edge",
        var s when s.Contains("firefox", StringComparison.OrdinalIgnoreCase) => "Firefox",
        // Brave's session can arrive as "Brave._crx_<id>", where the rest of the id names the
        // extension or web app that owns the media session rather than the browser itself.
        var s when s.Contains("Brave", StringComparison.OrdinalIgnoreCase) => "Brave",
        var s when s.Contains("MusicBee", StringComparison.OrdinalIgnoreCase) => "MusicBee",
        var s when s.Contains("foobar", StringComparison.OrdinalIgnoreCase) => "foobar2000",
        var s when s.Contains("vlc", StringComparison.OrdinalIgnoreCase) => "VLC",
        var s when s.Contains("Microsoft.ZuneMusic", StringComparison.OrdinalIgnoreCase) => "Media Player",
        _ => aumid ?? ""
    };

    public string TrackDescription =>
        string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";
}
