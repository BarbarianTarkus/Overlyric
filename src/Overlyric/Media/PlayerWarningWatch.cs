namespace Overlyric.Media;

/// <summary>What the user needs to be told about their player, if anything.</summary>
internal enum PlayerWarningKind
{
    None,

    /// <summary>
    /// Something is audibly playing, but no application publishes to Windows media controls at all -
    /// the player has no SMTC support, or none of it is switched on.
    /// </summary>
    NothingPublishes,

    /// <summary>
    /// A player publishes its track but no playback position, so synced lyrics have nothing to
    /// follow. Overlyric would otherwise sit on the first line and look broken.
    /// </summary>
    NoPlaybackPosition
}

/// <summary>
/// Decides when to tell the user that their player is not giving Overlyric anything to work with,
/// and why. Both cases are silent failures from the user's point of view: the overlay simply never
/// appears, or it shows the first line of the song for its whole length, and nothing on screen
/// explains that no amount of waiting will help.
/// </summary>
/// <remarks>
/// Two rules keep this from becoming noise. The condition has to hold for <see cref="Sustain"/>
/// before anything is said, so a track that starts at 00:00 without a stamp - which every player
/// does for a moment - never triggers it; and the answer is only announced once per situation, with
/// the tray tooltip and menu carrying it from then on.
/// </remarks>
internal sealed class PlayerWarningWatch
{
    /// <summary>How often the audio engine is asked, while nothing is publishing.</summary>
    private static readonly TimeSpan AudioCheckInterval = TimeSpan.FromSeconds(2);

    /// <summary>How long the condition must hold before it is worth mentioning.</summary>
    private static readonly TimeSpan Sustain = TimeSpan.FromSeconds(10);

    private long _nextAudioCheckAt;
    private bool _audioIsPlaying;

    private PlayerWarningKind _pending;
    private string? _pendingSource;
    private long _pendingSince;
    private bool _announced;

    /// <summary>The warning worth showing right now, or <see cref="PlayerWarningKind.None"/>.</summary>
    public PlayerWarningKind Kind { get; private set; }

    /// <summary>Display name of the player the warning is about, when it is about one.</summary>
    public string? SourceApp { get; private set; }

    /// <summary>True on the single tick where the warning becomes worth showing.</summary>
    public bool IsNewSituation { get; private set; }

    public void Update(NowPlayingSnapshot snapshot, bool positionIsUsable)
    {
        var now = Environment.TickCount64;
        var kind = PlayerWarningKind.None;
        string? source = null;

        if (snapshot.HasTrack)
        {
            // A player is publishing, so there is nothing to probe for.
            _audioIsPlaying = false;

            if (snapshot.IsPlaying && !positionIsUsable)
            {
                kind = PlayerWarningKind.NoPlaybackPosition;
                source = snapshot.SourceAppFriendly;
            }
        }
        else
        {
            if (now >= _nextAudioCheckAt)
            {
                _nextAudioCheckAt = now + (long)AudioCheckInterval.TotalMilliseconds;
                _audioIsPlaying = AudioSessions.IsAnythingAudible();
            }

            if (_audioIsPlaying)
            {
                kind = PlayerWarningKind.NothingPublishes;
            }
        }

        if (kind != _pending || !string.Equals(source, _pendingSource, StringComparison.Ordinal))
        {
            _pending = kind;
            _pendingSource = source;
            _pendingSince = kind == PlayerWarningKind.None ? 0 : now;
            _announced = false;
        }

        IsNewSituation = false;
        Kind = kind;
        SourceApp = source;

        if (kind == PlayerWarningKind.None || now - _pendingSince < Sustain.TotalMilliseconds)
        {
            Kind = PlayerWarningKind.None;
            SourceApp = null;
            return;
        }

        if (!_announced)
        {
            _announced = true;
            IsNewSituation = true;
        }
    }

    /// <summary>Forget everything, for when the user turns the warning off.</summary>
    public void Reset()
    {
        _pending = PlayerWarningKind.None;
        _pendingSource = null;
        _pendingSince = 0;
        _announced = false;
        _audioIsPlaying = false;
        Kind = PlayerWarningKind.None;
        SourceApp = null;
        IsNewSituation = false;
    }
}
