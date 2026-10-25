using Overlyric.Overlay;

namespace Overlyric.App;

/// <summary>
/// Lifetime of the transient notice drawn inside the overlay: it is shown for a few seconds when a
/// song starts or the overlay appears, then goes away by itself.
/// </summary>
/// <remarks>
/// The clock is passed in rather than read here, so the expiry rule is a plain comparison that can be
/// exercised without waiting for wall time. <see cref="Start"/> deliberately offers no way to
/// <em>extend</em> a showing: the pop-up is time-boxed from the moment it appears, so a slow lyric
/// lookup cannot keep it on screen. Starting it again - which is what the host does when the verdict
/// arrives after the provisional "looking up" display has already gone - is a new showing with its
/// own deadline.
/// </remarks>
internal sealed class InfoPopup
{
    private long _until;
    private OverlayNotice? _current;

    public bool IsShowing => _current is not null;

    /// <summary>The notice to draw right now, or null when there is nothing to show.</summary>
    public OverlayNotice? Current => _current;

    /// <summary>
    /// Begin (or restart) a showing. Zero seconds turns the feature off and clears anything visible.
    /// </summary>
    public void Start(string title, string detail, NoticeTone tone, long nowMs, int seconds)
    {
        if (seconds <= 0)
        {
            Clear();
            return;
        }

        _current = new OverlayNotice { Title = title, Detail = detail, Tone = tone };
        _until = nowMs + (seconds * 1000L);
    }

    /// <summary>Drop the notice once its time is up. Returns true when it actually changed.</summary>
    public bool Expire(long nowMs)
    {
        if (_current is null || nowMs < _until)
        {
            return false;
        }

        Clear();
        return true;
    }

    public void Clear()
    {
        _current = null;
        _until = 0;
    }
}
