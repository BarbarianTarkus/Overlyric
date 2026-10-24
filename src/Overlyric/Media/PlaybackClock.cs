using System.Diagnostics;

namespace Overlyric.Media;

/// <summary>
/// Produces the current playback position for synced lyrics.
/// <para>
/// Windows players differ wildly in how much they tell us. Some (browsers, Spotify) publish a
/// progressing SMTC timeline; TIDAL publishes metadata once at track start and then freezes the
/// position forever, raising no timeline events at all. So this clock is adaptive:
/// </para>
/// <list type="bullet">
///   <item>if the player demonstrably advances its timeline, we re-anchor to it but only when we
///   have drifted far enough to matter, which suppresses jitter;</item>
///   <item>otherwise we self-clock from a <see cref="Stopwatch"/>, anchored to the player's own
///   position once aged to the present, which is accurate to well under a lyric line over a whole
///   song and correct even when we attach to a track that is already playing.</item>
/// </list>
/// <para>
/// Every observation carries the wall-clock time it was taken (<c>LastUpdatedTime</c>), because the
/// player's number is a snapshot rather than "now". Ageing it is what makes attaching to a track
/// that is already minutes in land on the right lyric instead of the first one.
/// </para>
/// <para>
/// Callers may still nudge the position manually to compensate for a player that cannot be
/// observed seeking.
/// </para>
/// </summary>
public sealed class PlaybackClock
{
    /// <summary>
    /// Re-anchor only once the interpolated position is this far off the reported one. Word-level
    /// karaoke wants a tighter leash than line-level lyrics, which are happy with a wider one, so
    /// this is a property rather than a constant.
    /// </summary>
    public double DriftSnapThresholdMs { get; set; } = 400.0;

    /// <summary>
    /// Oldest stamp that can still be the moment a position was taken. Anything older is a sentinel
    /// rather than an anchor; six hours leaves room for any single track while rejecting leftovers.
    /// </summary>
    private static readonly TimeSpan MaxAnchorAge = TimeSpan.FromHours(6);

    /// <summary>Number of consecutive progressing samples before we trust the timeline as live.</summary>
    private const int LiveAdvanceThreshold = 2;

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Lock _gate = new();

    private TimeSpan _anchorPosition;
    private TimeSpan _anchorAt;
    private TimeSpan _manualOffset;
    private bool _playing;
    private bool _timelineIsLive;
    private bool _seeded;
    private bool _positionIsUsable;
    private int _advanceSamples;
    private long _seekCount;

    /// <summary>True once the player has proven it advances its own timeline.</summary>
    public bool TimelineIsLive
    {
        get { lock (_gate) { return _timelineIsLive; } }
    }

    /// <summary>
    /// True when the position stands on something real: the player advances its timeline, reports a
    /// non-zero position, or stamps that position plausibly. False means the clock is counting from
    /// an unexplained zero - what a player that publishes a track but no timeline at all looks like,
    /// and the case the user has to be told about, because no lyric can follow it.
    /// </summary>
    public bool PositionIsUsable
    {
        get { lock (_gate) { return _positionIsUsable; } }
    }

    public bool IsPlaying
    {
        get { lock (_gate) { return _playing; } }
    }

    /// <summary>User-applied correction added to every reported position.</summary>
    public TimeSpan ManualOffset
    {
        get { lock (_gate) { return _manualOffset; } }
        set { lock (_gate) { _manualOffset = value; } }
    }

    /// <summary>Count of detected timeline jumps large enough to look like a seek.</summary>
    public long SeekCount
    {
        get { lock (_gate) { return _seekCount; } }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                var position = _playing
                    ? _anchorPosition + (_stopwatch.Elapsed - _anchorAt)
                    : _anchorPosition;

                position += _manualOffset;
                if (position < TimeSpan.Zero)
                {
                    position = TimeSpan.Zero;
                }

                return position;
            }
        }
    }

    /// <summary>Re-anchor to a known position, for example at track start.</summary>
    public void Reset(TimeSpan position, bool playing)
    {
        lock (_gate)
        {
            _anchorPosition = position;
            _anchorAt = _stopwatch.Elapsed;
            _playing = playing;
        }
    }

    /// <summary>Full reset for a new track: clears liveness detection and the manual offset.</summary>
    /// <param name="position">Position the player reports for the new track.</param>
    /// <param name="reportedAt">
    /// Wall-clock time <paramref name="position"/> was taken - SMTC's <c>LastUpdatedTime</c>. See
    /// <see cref="Observe"/> for why it matters; pass <c>default</c> when the player offers none.
    /// </param>
    /// <param name="playing">Whether the player reports itself as playing.</param>
    public void ResetForNewTrack(TimeSpan position, DateTimeOffset reportedAt, bool playing)
    {
        lock (_gate)
        {
            _anchorPosition = Age(position, reportedAt, playing);
            _anchorAt = _stopwatch.Elapsed;
            _playing = playing;
            _seeded = playing;
            _timelineIsLive = false;
            _advanceSamples = 0;
            _manualOffset = TimeSpan.Zero;
            _positionIsUsable = position > TimeSpan.Zero || IsUsableStamp(reportedAt);
        }
    }

    /// <summary>Shift the current position by <paramref name="delta"/> and remember the correction.</summary>
    public void Nudge(TimeSpan delta)
    {
        lock (_gate)
        {
            _manualOffset += delta;
            _anchorPosition += delta;
            if (_anchorPosition < TimeSpan.Zero)
            {
                _anchorPosition = TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// Feed one observation from the player.
    /// </summary>
    /// <param name="reportedPosition">Position the player claims.</param>
    /// <param name="reportedAt">
    /// Wall-clock time <paramref name="reportedPosition"/> was taken - SMTC's
    /// <c>LastUpdatedTime</c>. Pass <c>default</c> when the player does not supply one, in which
    /// case the position is trusted as-is.
    /// </param>
    /// <param name="timelineAdvanced">
    /// True when the player's position or last-updated stamp moved since the previous poll,
    /// i.e. it is actually maintaining a timeline.
    /// </param>
    /// <param name="playing">Whether the player reports itself as playing.</param>
    public void Observe(TimeSpan reportedPosition, DateTimeOffset reportedAt, bool timelineAdvanced, bool playing)
    {
        lock (_gate)
        {
            var rawPosition = reportedPosition;
            reportedPosition = Age(reportedPosition, reportedAt, playing);

            if (timelineAdvanced || rawPosition > TimeSpan.Zero || IsUsableStamp(reportedAt))
            {
                _positionIsUsable = true;
            }

            if (timelineAdvanced)
            {
                _advanceSamples++;
                if (_advanceSamples >= LiveAdvanceThreshold)
                {
                    _timelineIsLive = true;
                }
            }
            else
            {
                _advanceSamples = 0;
            }

            // Handle pause/resume by freezing the interpolated position at the transition.
            if (playing != _playing)
            {
                _anchorPosition = ComputePositionUnlocked();
                _anchorAt = _stopwatch.Elapsed;
                _playing = playing;
            }

            if (!_timelineIsLive)
            {
                // Self-clocking: the player's number is not a timeline we can follow. It is still
                // the only thing that can place us on a track that was already minutes in when
                // Overlyric started, so it seeds the anchor once playback is running - including
                // when the app was opened while the track was paused, where the reset could only
                // see a frozen 00:00. Only once: after that the anchor is better than a report
                // that counts any earlier pause as playback.
                if (_playing && !_seeded)
                {
                    _anchorPosition = reportedPosition;
                    _anchorAt = _stopwatch.Elapsed;
                    _seeded = true;
                }

                return;
            }

            var interpolated = ComputePositionUnlocked();
            var drift = reportedPosition + _manualOffset - interpolated;

            if (Math.Abs(drift.TotalMilliseconds) > DriftSnapThresholdMs)
            {
                // Distinguish an ordinary correction from a user seek.
                if (Math.Abs(drift.TotalMilliseconds) > 2000)
                {
                    _seekCount++;
                }

                _anchorPosition = reportedPosition;
                _anchorAt = _stopwatch.Elapsed;
            }
        }
    }

    /// <summary>
    /// Ages a reported position to "now", turning the player's snapshot into a live position.
    /// <para>
    /// <c>Position</c> is only true at <c>LastUpdatedTime</c>. For a player that keeps its timeline
    /// current the two differ by a fraction of a second, but TIDAL stamps both once when the track
    /// starts and never changes them again: read several minutes in it still says 00:00, and
    /// believing that number rewinds the lyrics to the first line and holds them there for the
    /// whole track. Adding the wall-clock age recovers the real position in both cases.
    /// </para>
    /// <para>
    /// Only applied while playing. A paused player's position is not a stale anchor but the exact
    /// spot it stopped at, and its stamp is when it stopped, so ageing it would count the pause
    /// itself as playback.
    /// </para>
    /// </summary>
    private static TimeSpan Age(TimeSpan reportedPosition, DateTimeOffset reportedAt, bool playing)
    {
        if (!playing || !IsUsableStamp(reportedAt))
        {
            return reportedPosition;
        }

        return reportedPosition + (DateTimeOffset.UtcNow - reportedAt);
    }

    /// <summary>
    /// Whether a stamp can be the moment the position was taken. A player that publishes no timeline
    /// tends to leave the field at its zero value, which Windows hands over as 1601-01-01 - the
    /// FILETIME epoch. Ageing that would put the track four centuries in and paint the last line of
    /// the song with the progress bar full, which is worse than not ageing at all.
    /// </summary>
    private static bool IsUsableStamp(DateTimeOffset reportedAt)
    {
        if (reportedAt == default || reportedAt.Year < 2000)
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow - reportedAt;
        return age >= TimeSpan.Zero && age <= MaxAnchorAge;
    }

    private TimeSpan ComputePositionUnlocked() =>
        _playing
            ? _anchorPosition + (_stopwatch.Elapsed - _anchorAt)
            : _anchorPosition;
}
