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
public readonly record struct LyricsLookup(LyricsResult? Result, bool Definitive, bool LocalFileMissing = false);

/// <summary>
/// One manual search: whether lrclib answered at all, and every record it offered. An unavailable
/// answer is not the same as "no records", because the window has to say which one happened.
/// </summary>
public readonly record struct LyricsSearchOutcome(bool Available, IReadOnlyList<LyricsCandidate> Candidates);

/// <summary>
/// Resolves the lyrics for a track: a version pinned by the listener, then the cache, then lrclib's
/// exact endpoint using a <em>cleaned</em> title and album, then its fuzzy search ranked by likelihood.
/// </summary>
/// <remarks>
/// The cleaning step is the fix for a real, reproducible failure. TIDAL reports
/// <c>A Song For All Seasons (2019 Remaster)</c>, and lrclib holds a record whose name carries that
/// exact suffix - with plain lyrics only. Asking for the suffixed name returns precisely that family
/// of records, so the app reported "Lyrics not synced". Asking for <c>A Song For All Seasons</c>
/// returns twenty candidates including a synced record with an identical duration and album.
/// <para>
/// Albums carry the same kind of suffix and need the same cleaning. TIDAL reports
/// <c>The Lamb Lies Down on Broadway (2007 Stereo Mix)</c>; when lrclib files that edition under the
/// plain album, the exact lookup with the suffixed name finds nothing and the album-scoped search -
/// the one place an album is used as a filter - finds nothing either. The reported name is tried
/// first because it selects the edition actually playing when lrclib does hold it, then the cleaned
/// name, and only then is the album dropped.
/// </para>
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
    private readonly LyricSelectionStore _selections;
    private readonly LocalLyricsStore _localLyrics;

    /// <summary>One gate per key, so concurrent callers resolve a track once between them.</summary>
    private readonly Dictionary<string, SemaphoreSlim> _singleFlight = new(StringComparer.Ordinal);
    private readonly Lock _singleFlightGate = new();

    private readonly Lock _candidateGate = new();
    private string? _candidateKey;
    private IReadOnlyList<LyricsCandidate> _candidates = [];

    public LyricsService(
        LrclibClient client,
        LyricsCache cache,
        LyricSelectionStore selections,
        LocalLyricsStore localLyrics)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(localLyrics);

        _client = client;
        _cache = cache;
        _selections = selections;
        _localLyrics = localLyrics;
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
                .LookupSearchDetailedAsync(
                    artist,
                    MetadataNormaliser.CleanTitle(track, artist),
                    MetadataNormaliser.CleanAlbum(album),
                    durationSeconds,
                    ct)
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

    /// <summary>
    /// Free-text search, for the listener who wants to correct a query the automatic matching could
    /// not make work. The text is sent to lrclib verbatim - it is the user's question, not metadata to
    /// clean - and the records come back ranked by how well their length fits the playing track.
    /// </summary>
    /// <remarks>
    /// Nothing is cached: a manual search is an explicit question, so it is asked each time rather
    /// than remembered as this track's answer. The result is deliberately a list: the whole point is
    /// that the listener picks the record, because the automatic rank got the track wrong.
    /// </remarks>
    public async Task<LyricsSearchOutcome> SearchAsync(
        string query,
        double? durationSeconds,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query))
        {
            return new LyricsSearchOutcome(true, []);
        }

        try
        {
            var (status, candidates) = await _client
                .SearchByQueryAsync(query.Trim(), durationSeconds, ct)
                .ConfigureAwait(false);

            return new LyricsSearchOutcome(status != LookupStatus.Unavailable, candidates);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new LyricsSearchOutcome(false, []);
        }
    }

    /// <summary>Pin a specific lrclib record for this track and remember the choice.</summary>
    public void PinVersion(string artist, string track, string? album, long lrclibId)
    {
        _selections.SetLrclib(LyricsCache.MakeKey(artist, track, album), lrclibId);
        _selections.Save();
    }

    /// <summary>Drop any explicit choice and go back to automatic matching.</summary>
    public void ClearVersion(string artist, string track, string? album)
    {
        _selections.Clear(LyricsCache.MakeKey(artist, track, album));
        _selections.Save();
    }

    /// <summary>Reports the lrclib record pinned for this track, if the listener chose one.</summary>
    public bool TryGetPinnedVersion(string artist, string track, string? album, out long lrclibId)
    {
        lrclibId = 0;

        return _selections.TryGet(LyricsCache.MakeKey(artist, track, album), out var selection)
               && selection is not null
               && selection.Kind == LyricSelectionKind.Lrclib
               && (lrclibId = selection.RecordId) > 0;
    }

    /// <summary>The explicit choice for this track, whatever kind it is, for the windows.</summary>
    public bool TryGetSelection(string artist, string track, string? album, out LyricSelection selection) =>
        _selections.TryGet(LyricsCache.MakeKey(artist, track, album), out selection);

    /// <summary>Prefills the import dialog's preview without storing anything.</summary>
    public LocalLyricsPreview PreviewImport(string sourcePath, double? durationSeconds) =>
        LocalLyricsStore.Preview(sourcePath, durationSeconds);

    /// <summary>
    /// Stores an imported sheet and makes it this track's explicit selection. The cached automatic
    /// answer is forgotten rather than deleted from the selection: switching back to Automatic can
    /// still resolve it.
    /// </summary>
    public bool TryCommitImport(
        string artist,
        string track,
        string? album,
        LocalLyricsPreview preview,
        string sourcePath,
        out string? error)
    {
        error = null;

        if (!preview.Ok)
        {
            error = preview.Error ?? "That file could not be read.";
            return false;
        }

        var key = LyricsCache.MakeKey(artist, track, album);
        var metadata = new LocalLyricsMetadata
        {
            Artist = artist,
            Title = track,
            Album = album,
            SourcePath = sourcePath,
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Note = preview.IsSynced ? null : "plain lyrics: no timestamps, so it cannot follow the music"
        };

        if (!_localLyrics.Save(key, preview.Text, metadata))
        {
            error = "The lyric file could not be saved.";
            return false;
        }

        _selections.SetLocal(key, Path.GetFileName(sourcePath));
        _selections.Save();

        // The automatic answer is not the current answer any more; ask again if the listener returns
        // to Automatic, and drop the stored candidate list so the selector is honest.
        _cache.Forget(key);

        lock (_candidateGate)
        {
            _candidateKey = null;
            _candidates = [];
        }

        return true;
    }

    /// <summary>
    /// Writes the timed sheet currently in force - local or cached - as an LRC file. A plain sheet
    /// has no timestamps to write, and that is reported instead of producing an empty file.
    /// </summary>
    public bool TryExport(
        string artist,
        string track,
        string? album,
        double? durationSeconds,
        string destinationPath,
        out string? error)
    {
        error = null;
        var key = LyricsCache.MakeKey(artist, track, album);
        IReadOnlyList<LyricLine>? lines = null;

        if (_selections.TryGet(key, out var selection) && selection is not null
            && selection.Kind == LyricSelectionKind.Local
            && _localLyrics.TryLoad(key, out var local) && local is not null
            && local.IsSynced)
        {
            lines = local.Lines;
        }
        else if (_cache.TryGet(key, out var cached) && cached is not null && cached.IsSynced)
        {
            lines = cached.Lines;
        }

        if (lines is null || lines.Count == 0)
        {
            error = "There is no timed lyric sheet to export for this track.";
            return false;
        }

        try
        {
            var text = LyricTextCodec.EncodeLrc(artist, track, album, durationSeconds, lines);
            File.WriteAllBytes(destinationPath, LyricTextCodec.EncodeUtf8(text));
            return true;
        }
        catch (Exception)
        {
            error = "The file could not be written.";
            return false;
        }
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

        if (_selections.TryGet(key, out var selection) && selection is not null
            && selection.Kind == LyricSelectionKind.Lrclib
            && selection.RecordId > 0)
        {
            _cache.Forget(VersionOverrides.VersionKey(key, selection.RecordId));
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
        var localFileMissing = false;

        // One explicit selection outranks anything automatic, and the layers do not stack: a local
        // file, a pinned record, or the automatic pipeline - exactly one of them answers.
        if (_selections.TryGet(key, out var selection) && selection is not null)
        {
            switch (selection.Kind)
            {
                case LyricSelectionKind.Local:
                    if (_localLyrics.TryLoad(key, out var local) && local is not null)
                    {
                        return new LyricsLookup(LocalResult(local), true);
                    }

                    // The stored file was deleted or became unreadable. Fall through to automatic,
                    // but report it once: a silent switch would look like the import never happened.
                    localFileMissing = true;
                    break;

                case LyricSelectionKind.Lrclib when selection.RecordId > 0:
                    var pinned = await LookupPinnedAsync(key, selection.RecordId, ct).ConfigureAwait(false);

                    if (pinned is not null)
                    {
                        return pinned.Value;
                    }

                    break;
            }
        }

        var automatic = await ResolveAutomaticAsync(key, artist, track, album, durationSeconds, ct)
            .ConfigureAwait(false);

        return localFileMissing ? automatic with { LocalFileMissing = true } : automatic;
    }

    /// <summary>The automatic pipeline: cache, exact lookup, then the ranked search.</summary>
    private async Task<LyricsLookup> ResolveAutomaticAsync(
        string key,
        string artist,
        string track,
        string? album,
        double? durationSeconds,
        CancellationToken ct)
    {
        var startedAt = Environment.TickCount64;
        var cleanTitle = MetadataNormaliser.CleanTitle(track, artist);
        var cleanAlbum = MetadataNormaliser.CleanAlbum(album);

        if (_cache.TryGet(key, out var cached) && cached is not null)
        {
            return new LyricsLookup(cached, true);
        }

        if (_cache.IsMiss(key))
        {
            // A tombstone is only ever written for a definitive answer, so it stays one.
            return new LyricsLookup(null, true);
        }

        var exact = await LookupExactWithAlbumAsync(
                artist, cleanTitle, album, cleanAlbum, durationSeconds, startedAt, ct)
            .ConfigureAwait(false);

        if (exact.Status == LookupStatus.Unavailable)
        {
            return Unknown();
        }

        var best = exact.Lyrics;

        // Missing timings deserve a second opinion: lrclib frequently holds a synced sibling, and the
        // ranked search is what turns "Lyrics not synced" into genuinely synced lyrics.
        if ((best is null || !best.IsSynced) && WithinBudget(startedAt))
        {
            var (status, searched, candidates) = await _client
                .LookupSearchDetailedAsync(artist, cleanTitle, cleanAlbum, durationSeconds, ct)
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
    /// The exact lookup's album ladder: the reported name, then the cleaned name, then no album.
    /// </summary>
    /// <remarks>
    /// The album is the most precise part of the question, so it goes first. It is sent verbatim
    /// because lrclib matches it exactly and many players report it faithfully - TIDAL's
    /// "The Lamb Lies Down on Broadway (2007 Stereo Mix)" is a real lrclib album, and that name
    /// selects the edition actually playing.
    /// <para>
    /// When the verbatim name finds nothing, the release suffix is the first suspect: TIDAL also
    /// reports editions lrclib files under the plain album, and asking for the suffixed name then
    /// matches nothing at all. The cleaned name is tried before the album is dropped, so a track
    /// from such an edition still resolves to the right album instead of whichever record an
    /// album-less exact lookup happens to answer first.
    /// </para>
    /// <para>
    /// Only then is the album dropped entirely, which is the broadest question and the one that can
    /// still answer when the player's album does not exist in lrclib under any name.
    /// </para>
    /// <para>
    /// A definitive miss on the reported name is the only status that continues the ladder. Found
    /// and Unavailable both stop it: the first has the answer, and the second is lrclib saying it
    /// cannot answer now, which the next rung would hear as well.
    /// </para>
    /// </remarks>
    private async Task<LookupOutcome> LookupExactWithAlbumAsync(
        string artist,
        string title,
        string? album,
        string cleanAlbum,
        double? durationSeconds,
        long startedAt,
        CancellationToken ct)
    {
        var reportedAlbum = (album ?? string.Empty).Trim();

        var exact = await _client
            .LookupExactAsync(artist, title, album, durationSeconds, ct)
            .ConfigureAwait(false);

        if (exact.Status != LookupStatus.NotFound || reportedAlbum.Length == 0)
        {
            return exact;
        }

        // The comparison trims the reported name first, so a player that pads its metadata does not
        // buy a redundant request for an album that is already clean.
        if (!string.Equals(cleanAlbum, reportedAlbum, StringComparison.Ordinal)
            && WithinBudget(startedAt))
        {
            exact = await _client
                .LookupExactAsync(artist, title, cleanAlbum, durationSeconds, ct)
                .ConfigureAwait(false);

            if (exact.Status != LookupStatus.NotFound)
            {
                return exact;
            }
        }

        if (!WithinBudget(startedAt))
        {
            return exact;
        }

        return await _client
            .LookupExactAsync(artist, title, null, durationSeconds, ct)
            .ConfigureAwait(false);
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
            return new LyricsLookup(cached.WithSource(LyricSource.Pinned), true);
        }

        var pinned = await _client.LookupByIdAsync(pinnedId, ct).ConfigureAwait(false);

        switch (pinned.Status)
        {
            case LookupStatus.Found when pinned.Lyrics is not null:
                _cache.Set(versionKey, pinned.Lyrics);
                return new LyricsLookup(pinned.Lyrics.WithSource(LyricSource.Pinned), true);

            case LookupStatus.Unavailable:
                // Not a reason to ask four more questions of a server that just failed.
                return Unknown();

            default:
                // The record this track was pinned to has disappeared; automatic matching decides.
                return null;
        }
    }

    /// <summary>A stored local sheet as the same result shape every other source produces.</summary>
    private static LyricsResult LocalResult(LocalLyricsDocument local) => new()
    {
        Lines = local.Lines,
        IsSynced = local.IsSynced,
        PlainLyrics = local.IsSynced ? null : local.Text,
        TrackName = local.Metadata?.Title,
        ArtistName = local.Metadata?.Artist,
        Source = LyricSource.Local
    };

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
