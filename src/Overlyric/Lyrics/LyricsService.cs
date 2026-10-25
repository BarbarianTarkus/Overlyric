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
/// </remarks>
public sealed class LyricsService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(600);
    private const int AttemptsPerEndpoint = 2;

    private readonly LrclibClient _client;
    private readonly LyricsCache _cache;
    private readonly VersionOverrides? _overrides;

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
        var sawUnavailable = false;
        void MarkUnavailable() => sawUnavailable = true;

        // A version chosen by hand outranks anything automatic. It is cached under its own key, so
        // the automatic answer survives for when the listener switches back.
        if (_overrides is not null && _overrides.TryGet(key, out var pinnedId))
        {
            var versionKey = VersionOverrides.VersionKey(key, pinnedId);

            if (_cache.TryGet(versionKey, out var pinnedResult) && pinnedResult is not null)
            {
                return new LyricsLookup(pinnedResult, true);
            }

            var pinnedLyrics = await LookupWithRetryAsync(
                token => _client.LookupByIdAsync(pinnedId, token),
                MarkUnavailable,
                ct).ConfigureAwait(false);

            if (pinnedLyrics is not null)
            {
                _cache.Set(versionKey, pinnedLyrics);
                return new LyricsLookup(pinnedLyrics, true);
            }

            // The pinned record has disappeared; fall through to automatic matching.
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

        var cleanTitle = MetadataNormaliser.CleanTitle(track);

        var best = await LookupWithRetryAsync(
            token => _client.LookupExactAsync(artist, cleanTitle, album, durationSeconds, token),
            MarkUnavailable,
            ct).ConfigureAwait(false);

        // The album is sent verbatim at first, because lrclib matches it exactly and players usually
        // report it faithfully; it is only dropped when that finds nothing.
        if (best is null && !string.IsNullOrWhiteSpace(album))
        {
            best = await LookupWithRetryAsync(
                token => _client.LookupExactAsync(artist, cleanTitle, null, durationSeconds, token),
                MarkUnavailable,
                ct).ConfigureAwait(false);
        }

        // Missing timings deserve a second opinion: lrclib frequently holds a synced sibling, and the
        // ranked search is what turns "Lyrics not synced" into genuinely synced lyrics.
        if (best is null || !best.IsSynced)
        {
            var searched = await LookupSearchAsync(artist, cleanTitle, album, durationSeconds, key, MarkUnavailable, ct)
                .ConfigureAwait(false);

            if (searched is not null && (best is null || searched.IsSynced))
            {
                best = searched;
            }
        }

        if (best is not null)
        {
            _cache.Set(key, best);
            return new LyricsLookup(best, true);
        }

        // Only a definitive "lrclib has nothing" is worth remembering. Caching a transient outage
        // would hide this track's lyrics forever after a single bad moment on lrclib's side.
        if (!sawUnavailable)
        {
            _cache.SetMiss(key);
        }

        return new LyricsLookup(null, !sawUnavailable);
    }

    /// <summary>
    /// Lists every lrclib record that could serve this track, most likely first, for the version
    /// menu. Memoised per track so repeatedly opening the menu costs nothing.
    /// </summary>
    public async Task<IReadOnlyList<LyricsCandidate>> GetVersionsAsync(
        string artist,
        string track,
        string? album,
        double? durationSeconds,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var key = LyricsCache.MakeKey(artist, track, album);

        lock (_candidateGate)
        {
            if (string.Equals(_candidateKey, key, StringComparison.Ordinal))
            {
                return _candidates;
            }
        }

        try
        {
            var (status, _, candidates) = await _client
                .LookupSearchDetailedAsync(artist, MetadataNormaliser.CleanTitle(track), album, durationSeconds, ct)
                .ConfigureAwait(false);

            if (status != LookupStatus.Found)
            {
                return [];
            }

            // An empty list is not memoised, so a menu opened after a failed lookup tries again
            // rather than showing "nothing" forever.
            if (candidates.Count > 0)
            {
                RememberCandidates(key, candidates);
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
    /// Forgets the cached answer for one track, including the copy stored under a pinned record.
    /// The version choice itself is kept: "refresh" means ask again, not "go back to automatic".
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

    private async Task<LyricsResult?> LookupSearchAsync(
        string artist,
        string track,
        string? album,
        double? durationSeconds,
        string key,
        Action onUnavailable,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < AttemptsPerEndpoint; attempt++)
        {
            var (status, best, candidates) = await _client
                .LookupSearchDetailedAsync(artist, track, album, durationSeconds, ct)
                .ConfigureAwait(false);

            if (status == LookupStatus.Found)
            {
                RememberCandidates(key, candidates);
                return best;
            }

            if (status == LookupStatus.NotFound)
            {
                RememberCandidates(key, []);
                return null;
            }

            onUnavailable();

            if (attempt + 1 < AttemptsPerEndpoint)
            {
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }

        return null;
    }

    private void RememberCandidates(string key, IReadOnlyList<LyricsCandidate> candidates)
    {
        lock (_candidateGate)
        {
            _candidateKey = key;
            _candidates = candidates;
        }
    }

    /// <summary>
    /// Runs one endpoint, retrying when lrclib was merely unavailable. Stops immediately on a
    /// definitive answer or a hit.
    /// </summary>
    private static async Task<LyricsResult?> LookupWithRetryAsync(
        Func<CancellationToken, Task<LookupOutcome>> lookup,
        Action onUnavailable,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < AttemptsPerEndpoint; attempt++)
        {
            var outcome = await lookup(ct).ConfigureAwait(false);

            if (outcome.Status == LookupStatus.Found)
            {
                return outcome.Lyrics;
            }

            if (outcome.Status == LookupStatus.NotFound)
            {
                // lrclib genuinely has nothing here; retrying would be pointless.
                return null;
            }

            onUnavailable();

            if (attempt + 1 < AttemptsPerEndpoint)
            {
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }

        return null;
    }
}
