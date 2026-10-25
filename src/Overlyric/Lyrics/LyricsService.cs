using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Overlyric.Lyrics;

/// <summary>
/// One lyrics lookup, with the one fact a bare <see cref="LyricsResult"/> cannot carry: whether an
/// empty answer is the truth or merely the absence of one.
/// </summary>
/// <remarks>
/// <see cref="Definitive"/> is false only when lrclib could not be reached or refused to answer -
/// throttling, an outage, a timeout. That distinction decides two things downstream: whether the
/// answer may be cached as a tombstone, and whether it is fair to tell the user there are no lyrics.
/// </remarks>
public readonly record struct LyricsLookup(LyricsResult? Result, bool Definitive);

/// <summary>
/// Resolves the lyrics for a track: a version pinned by the listener, then the cache, then lrclib's
/// exact endpoint using a <em>cleaned</em> title, then its fuzzy search ranked by likelihood.
/// </summary>
/// <remarks>
/// The cleaning step is the fix for a real, reproducible failure. TIDAL reports
/// <c>A Song For All Seasons (2019 Remaster)</c>, and lrclib holds a record whose name carries that
/// exact suffix - with plain lyrics only. Asking for the suffixed name returns precisely that family
/// of records, so the app reported "Lyrics not synced". Asking for <c>A Song For All Seasons</c>
/// returns twenty candidates including a synced record with an identical duration and album.
/// <para>
/// A resolution is deliberately cheap and deliberately short. It asks the exact endpoint first,
/// because that answer is one record and a search answer is twenty; it stops at the first sign that
/// lrclib cannot answer, because the remaining questions would be asked of a server that has just
/// said it is busy; and it never runs twice at once for the same track, because a track change and a
/// selector opening at the same moment are one question, not two.
/// </para>
/// </remarks>
public sealed class LyricsService
{
    /// <summary>
    /// How long one resolution may keep asking questions before it reports what it has. Bounds the
    /// worst case - several slow-but-successful requests - at something a listener will sit through.
    /// </summary>
    private static readonly TimeSpan ResolutionBudget = TimeSpan.FromSeconds(15);

    private readonly LrclibClient _client;
    private readonly LyricsCache _cache;
    private readonly VersionOverrides? _overrides;

    /// <summary>One gate per key, so concurrent callers resolve a track once between them.</summary>
    private readonly Dictionary<string, SemaphoreSlim> _singleFlight = new(StringComparer.Ordinal);
    private readonly Lock _singleFlightGate = new();

    private readonly Lock _candidateGate = new();
    private string? _candidateKey;
    private IReadOnlyList<LyricsCandidate> _candidates = [];

    public LyricsService(LrclibClient client, LyricsCache cache, VersionOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(cache);

        _client = client;
        _cache = cache;
        _overrides = overrides;
    }

    /// <summary>
    /// Resolves lyrics for a track. Returns <see langword="null"/> when lrclib has nothing;
    /// network and cache failures are reported the same way instead of throwing.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    public async Task<LyricsResult?> GetLyricsAsync(
        string artist,
        string track,
        string? album,
        double? durationSeconds,
        CancellationToken ct)
    {
        var lookup = await LookupDetailedAsync(artist, track, album, durationSeconds, ct).ConfigureAwait(false);
        return lookup.Result;
    }

    /// <summary>
    /// The same lookup, reporting whether an empty answer is definitive. The host needs that to decide
    /// between "lrclib has no lyrics for this track" - worth a notification - and "lrclib did not answer
    /// just now", which must stay silent and be retried.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    public async Task<LyricsLookup> LookupDetailedAsync(
        string artist,
        string track,
        string? album,
        double? durationSeconds,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var key = LyricsCache.MakeKey(artist, track, album);
        var gate = SingleFlight("resolve", key);

        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            return await ResolveAsync(key, artist, track, album, durationSeconds, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Lists every lrclib record that could serve this track, most likely first, for the version
    /// menu. Memoised per track, and remembered on disk, so reopening the menu costs nothing - not
    /// even in the next session.
    /// </summary>
    /// <param name="refresh">
    /// True to ignore both the in-memory list and the stored one and ask lrclib again. That is what
    /// the selector's "Look again" means; simply opening the selector does not.
    /// </param>
    public async Task<IReadOnlyList<LyricsCandidate>> GetVersionsAsync(
        string artist,
        string track,
        string? album,
        double? durationSeconds,
        CancellationToken ct,
        bool refresh = false)
    {
        ct.ThrowIfCancellationRequested();

        var key = LyricsCache.MakeKey(artist, track, album);
        var gate = SingleFlight("versions", key);

        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (!refresh)
            {
                lock (_candidateGate)
                {
                    if (string.Equals(_candidateKey, key, StringComparison.Ordinal))
                    {
                        return _candidates;
                    }
                }

                if (_cache.TryGetCandidates(key, out var stored))
                {
                    RememberCandidates(key, stored);
                    return stored;
                }

                // A current tombstone is this app's own verdict that lrclib holds nothing for the
                // track, so there is no list worth fetching - and the host asks for one every time
                // a lyric-less track comes round. "Look again" still asks; that is the whole point
                // of it being the only explicit refresh.
                if (_cache.IsMiss(key))
                {
                    return [];
                }
            }

            var (status, _, candidates) = await _client
                .LookupSearchDetailedAsync(artist, MetadataNormaliser.CleanTitle(track), album, durationSeconds, ct)
                .ConfigureAwait(false);

            // An unavailable answer is not a list of zero alternatives, so it is not remembered:
            // the next attempt - a menu opening, or Look again - asks a server that may have
            // recovered. A definitive empty answer is remembered, because re-asking it would be rude.
            if (status != LookupStatus.Found)
            {
                return [];
            }

            RememberCandidates(key, candidates);

            if (candidates.Count > 0)
            {
                _cache.SetCandidates(key, candidates);
            }

            return candidates;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Pin a specific lrclib record for this track and remember the choice.</summary>
    public void PinVersion(string artist, string track, string? album, long lrclibId)
    {
        _overrides?.Set(LyricsCache.MakeKey(artist, track, album), lrclibId);
        _overrides?.Save();
    }

    /// <summary>Drop any pinned version and go back to automatic matching.</summary>
    public void ClearVersion(string artist, string track, string? album)
    {
        _overrides?.Clear(LyricsCache.MakeKey(artist, track, album));
        _overrides?.Save();
    }

    /// <summary>Reports the lrclib record pinned for this track, if the listener chose one.</summary>
    public bool TryGetPinnedVersion(string artist, string track, string? album, out long lrclibId)
    {
        lrclibId = 0;

        return _overrides is not null
               && _overrides.TryGet(LyricsCache.MakeKey(artist, track, album), out lrclibId);
    }

    /// <summary>
    /// Forgets the cached answer for one track, including the copy stored under a pinned record and
    /// the stored list of alternatives. The version choice itself is kept: "refresh" means ask again,
    /// not "go back to automatic".
    /// </summary>
    public void ForgetCached(string artist, string track, string? album)
    {
        var key = LyricsCache.MakeKey(artist, track, album);
        _cache.Forget(key);

        if (_overrides is not null && _overrides.TryGet(key, out var pinnedId))
        {
            _cache.Forget(VersionOverrides.VersionKey(key, pinnedId));
        }

        lock (_candidateGate)
        {
            _candidateKey = null;
            _candidates = [];
        }
    }

    /// <summary>Drops every lyric answer from disk. The next lookup asks lrclib again.</summary>
    public void ClearCache()
    {
        _cache.Clear();

        lock (_candidateGate)
        {
            _candidateKey = null;
            _candidates = [];
        }
    }

    /// <summary>Cache size, for the settings window. Best effort; zero when nothing is stored.</summary>
    public (int Entries, long Bytes) GetCacheStats() => _cache.GetStats();

    /// <summary>
    /// The whole resolution, run under the track's single-flight gate.
    /// </summary>
    /// <remarks>
    /// Every branch that meets an <see cref="LookupStatus.Unavailable"/> returns immediately instead
    /// of trying the next question. lrclib's failures are server-wide - it is busy, or this client
    /// has been throttled - so the other questions would meet the same answer, and asking them turns
    /// one failure into four. The host retries the track from the start a few seconds later, which is
    /// also the schedule lrclib's own documentation asks for.
    /// </remarks>
    private async Task<LyricsLookup> ResolveAsync(
        string key,
        string artist,
        string track,
        string? album,
        double? durationSeconds,
        CancellationToken ct)
    {
        var startedAt = Environment.TickCount64;
        var cleanTitle = MetadataNormaliser.CleanTitle(track);

        // A version chosen by hand outranks anything automatic. It is cached under its own key, so
        // the automatic answer survives for when the listener switches back.
        if (_overrides is not null && _overrides.TryGet(key, out var pinnedId))
        {
            var pinned = await LookupPinnedAsync(key, pinnedId, ct).ConfigureAwait(false);

            if (pinned is not null)
            {
                return pinned.Value;
            }
        }

        if (_cache.TryGet(key, out var cached) && cached is not null)
        {
            return new LyricsLookup(cached, true);
        }

        if (_cache.IsMiss(key))
        {
            // A tombstone is only ever written for a definitive answer, so it stays one.
            return new LyricsLookup(null, true);
        }

        var exact = await _client
            .LookupExactAsync(artist, cleanTitle, album, durationSeconds, ct)
            .ConfigureAwait(false);

        if (exact.Status == LookupStatus.Unavailable)
        {
            return Unknown();
        }

        var best = exact.Lyrics;

        // The album is sent verbatim at first, because lrclib matches it exactly and players usually
        // report it faithfully; it is only dropped when that finds nothing.
        if (best is null && !string.IsNullOrWhiteSpace(album) && WithinBudget(startedAt))
        {
            var withoutAlbum = await _client
                .LookupExactAsync(artist, cleanTitle, null, durationSeconds, ct)
                .ConfigureAwait(false);

            if (withoutAlbum.Status == LookupStatus.Unavailable)
            {
                return Unknown();
            }

            best = withoutAlbum.Lyrics;
        }

        // Missing timings deserve a second opinion: lrclib frequently holds a synced sibling, and the
        // ranked search is what turns "Lyrics not synced" into genuinely synced lyrics.
        if ((best is null || !best.IsSynced) && WithinBudget(startedAt))
        {
            var (status, searched, candidates) = await _client
                .LookupSearchDetailedAsync(artist, cleanTitle, album, durationSeconds, ct)
                .ConfigureAwait(false);

            if (status == LookupStatus.Found)
            {
                // Remembered whether or not it changed the pick: it is what the selector shows, and
                // it is the most expensive thing this resolution downloaded.
                RememberCandidates(key, candidates);
                _cache.SetCandidates(key, candidates);

                if (searched is not null && (best is null || searched.IsSynced))
                {
                    best = searched;
                }
            }
            else if (status == LookupStatus.NotFound)
            {
                // A search answering 404 is lrclib saying it holds nothing for this track. That is
                // a definitive miss, and the tombstone below records it.
                RememberCandidates(key, []);
            }
            else if (best is null)
            {
                return Unknown();
            }
        }

        if (best is not null)
        {
            _cache.Set(key, best);
            return new LyricsLookup(best, true);
        }

        // Only a definitive "lrclib has nothing" is worth remembering, and only for as long as the
        // cache's tombstone lifetime: lrclib backfills missing tracks, so "nothing" is not forever.
        _cache.SetMiss(key);
        return new LyricsLookup(null, true);
    }

    /// <summary>
    /// The pinned record, when there is one. A null answer means "no pinned answer today, carry on
    /// with automatic matching" - which is what a deleted record deserves.
    /// </summary>
    private async Task<LyricsLookup?> LookupPinnedAsync(string key, long pinnedId, CancellationToken ct)
    {
        var versionKey = VersionOverrides.VersionKey(key, pinnedId);

        if (_cache.TryGet(versionKey, out var cached) && cached is not null)
        {
            return new LyricsLookup(cached, true);
        }

        var pinned = await _client.LookupByIdAsync(pinnedId, ct).ConfigureAwait(false);

        switch (pinned.Status)
        {
            case LookupStatus.Found when pinned.Lyrics is not null:
                _cache.Set(versionKey, pinned.Lyrics);
                return new LyricsLookup(pinned.Lyrics, true);

            case LookupStatus.Unavailable:
                // Not a reason to ask four more questions of a server that just failed.
                return Unknown();

            default:
                // The record this track was pinned to has disappeared; automatic matching decides.
                return null;
        }
    }

    /// <summary>True while this resolution still has budget left for another question.</summary>
    private static bool WithinBudget(long startedAt) =>
        Environment.TickCount64 - startedAt < (long)ResolutionBudget.TotalMilliseconds;

    private static LyricsLookup Unknown() => new(null, false);

    private void RememberCandidates(string key, IReadOnlyList<LyricsCandidate> candidates)
    {
        lock (_candidateGate)
        {
            _candidateKey = key;
            _candidates = candidates;
        }
    }

    /// <summary>
    /// One gate per key, so a track change, a manual refresh and the selector opening at the same
    /// moment resolve the track once between them instead of once each. The table only has to hold
    /// what is in flight right now - a track nobody is resolving is an unreachable key - so it is
    /// cleared wholesale when it grows, which keeps it bounded without a timer.
    /// </summary>
    private SemaphoreSlim SingleFlight(string kind, string key)
    {
        var name = string.Concat(kind, "\n", key);

        lock (_singleFlightGate)
        {
            if (_singleFlight.TryGetValue(name, out var existing))
            {
                return existing;
            }

            if (_singleFlight.Count >= 256)
            {
                _singleFlight.Clear();
            }

            var gate = new SemaphoreSlim(1, 1);
            _singleFlight[name] = gate;
            return gate;
        }
    }
}
