using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Overlyric.Lyrics;

/// <summary>
/// Paces lrclib requests: at least <see cref="MinimumGap"/> between the starts of two requests, and
/// never more than <see cref="WindowBudget"/> of them inside <see cref="Window"/>.
/// </summary>
/// <remarks>
/// lrclib asks clients to send requests sequentially and to leave 200-500 ms between them, and it
/// allows roughly 90 requests a minute to a client that identifies itself. A lyric resolution spends
/// one to three requests, so the gap costs a fraction of a second on a cold track and keeps the app
/// an order of magnitude inside the allowance even while someone skips through a playlist.
/// <para>
/// A request that would have to wait longer than <see cref="MaximumWait"/> is not sent at all:
/// reporting "unknown" and letting the host retry later is better than holding a track's lyrics
/// behind an unbounded queue. Nothing here ever blocks a caller indefinitely.
/// </para>
/// </remarks>
internal sealed class LrclibPacer
{
    /// <summary>Floor between two request starts. The low end of the range lrclib asks for.</summary>
    private static readonly long MinimumGapMs = 200;

    /// <summary>Added at random, so two instances started together do not march in step.</summary>
    private const int JitterMs = 50;

    /// <summary>Requests allowed inside <see cref="Window"/>. Well under the ~90/min allowance.</summary>
    private const int WindowBudget = 50;

    private const long WindowMs = 60_000;

    /// <summary>Longer than this and the request is abandoned instead of queued.</summary>
    private static readonly long MaximumWaitMs = (long)TimeSpan.FromSeconds(5).TotalMilliseconds;

    private readonly Lock _gate = new();
    private readonly Queue<long> _starts = new();

    /// <summary>Earliest tick at which the next request may start.</summary>
    private long _nextStart;

    /// <summary>
    /// Waits for this request's turn. Returns false when the wait would be longer than the caller is
    /// willing to hold a lyrics lookup open; the request must then not be sent.
    /// </summary>
    public async Task<bool> WaitAsync(CancellationToken ct)
    {
        long delayMs;

        lock (_gate)
        {
            var now = Environment.TickCount64;

            while (_starts.Count > 0 && _starts.Peek() <= now - WindowMs)
            {
                _starts.Dequeue();
            }

            var start = Math.Max(now, _nextStart);

            if (_starts.Count >= WindowBudget)
            {
                // The window is full: the earliest this request may go out is when the oldest
                // reservation leaves it.
                start = Math.Max(start, _starts.Peek() + WindowMs);
            }

            delayMs = start - now;

            if (delayMs > MaximumWaitMs)
            {
                return false;
            }

            var jitter = Random.Shared.Next(0, JitterMs);
            _nextStart = start + MinimumGapMs + jitter;
            _starts.Enqueue(start);
        }

        if (delayMs > 0)
        {
            await Task.Delay((int)delayMs, ct).ConfigureAwait(false);
        }

        return true;
    }
}

/// <summary>
/// Honours lrclib's <c>Retry-After</c>. While the window the server named is open no request is
/// sent, and a request that would have to wait too long reports "unknown" instead.
/// </summary>
/// <remarks>
/// The API documentation is explicit that ignoring this header "may result in a temporary ban", and
/// the service really does answer in runs: four consecutive 503s, each carrying <c>Retry-After: 1</c>,
/// were observed while this was written. The old client waited a fixed 600 ms and asked again, which
/// is precisely the behaviour that header exists to prevent.
/// <para>
/// The delay is stretched by up to 30 % at random, as the documentation asks, so that many clients
/// throttled at the same instant do not come back in a synchronized wave.
/// </para>
/// </remarks>
internal sealed class LrclibBackoff
{
    private static readonly long MaximumWaitMs = (long)TimeSpan.FromSeconds(5).TotalMilliseconds;
    private static readonly long MaximumDeferMs = (long)TimeSpan.FromSeconds(60).TotalMilliseconds;

    private readonly Lock _gate = new();

    /// <summary>Tick at which requests may resume. Zero means "now".</summary>
    private long _resumeAt;

    /// <summary>
    /// Waits out the server's window when it is short enough to be worth waiting for. Returns false
    /// when the caller should give up and report the answer as unknown.
    /// </summary>
    public async Task<bool> WaitAsync(CancellationToken ct)
    {
        long remaining;

        lock (_gate)
        {
            remaining = _resumeAt - Environment.TickCount64;
        }

        if (remaining <= 0)
        {
            return true;
        }

        if (remaining > MaximumWaitMs)
        {
            return false;
        }

        await Task.Delay((int)remaining, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Closes the window for <paramref name="delay"/>, jittered and capped.</summary>
    public void Defer(TimeSpan delay)
    {
        var milliseconds = (long)delay.TotalMilliseconds;

        if (milliseconds <= 0)
        {
            return;
        }

        // Up to 30 % longer than asked for: the documentation's own advice for many clients
        // throttled at once.
        milliseconds += Random.Shared.Next(0, (int)Math.Max(1, milliseconds * 3 / 10));
        milliseconds = Math.Clamp(milliseconds, 250, MaximumDeferMs);

        var resumeAt = Environment.TickCount64 + milliseconds;

        lock (_gate)
        {
            if (resumeAt > _resumeAt)
            {
                _resumeAt = resumeAt;
            }
        }
    }
}
