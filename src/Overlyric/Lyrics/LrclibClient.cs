using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Overlyric.Lyrics;

/// <summary>How a lookup ended. The distinction between the last two is what keeps the cache honest.</summary>
internal enum LookupStatus
{
    /// <summary>lrclib answered with usable lyrics.</summary>
    Found,

    /// <summary>lrclib answered definitively: it does not have this track.</summary>
    NotFound,

    /// <summary>lrclib could not be reached, or refused the request. The answer is simply unknown.</summary>
    Unavailable
}

/// <summary>
/// A lookup result that remembers <em>why</em> it was empty.
/// <para>
/// This matters because lrclib throttles bursts and periodically answers 503 "server is busy".
/// Treating that as "no lyrics" and negative-caching it would hide a track's lyrics permanently
/// after one momentary outage, so callers must be able to tell the two apart.
/// </para>
/// </summary>
internal readonly record struct LookupOutcome(LookupStatus Status, LyricsResult? Lyrics)
{
    public static LookupOutcome Found(LyricsResult lyrics) => new(LookupStatus.Found, lyrics);

    public static LookupOutcome NotFound => new(LookupStatus.NotFound, null);

    public static LookupOutcome Unavailable => new(LookupStatus.Unavailable, null);
}

/// <summary>
/// Minimal client for <see href="https://lrclib.net"/>.
/// </summary>
/// <remarks>
/// The underlying <see cref="HttpClient"/> is shared process wide (one connection pool, no
/// per-request sockets) and the <c>User-Agent</c> header is attached per request because lrclib
/// requires one. The public methods report every failure as <see langword="null"/> for convenience;
/// the <c>Lookup*</c> methods additionally report whether the failure was definitive.
/// </remarks>
public sealed class LrclibClient : IDisposable
{
    private const string BaseUrl = "https://lrclib.net";

    /// <summary>How many records one <c>/api/search</c> can return. There is no pagination.</summary>
    private const int SearchResultCap = 20;

    /// <summary>
    /// Used when a 429 arrives without <c>Retry-After</c>, which should not happen but does. Kept
    /// inside the client's own patience: waiting is better than refusing, but not for long.
    /// </summary>
    private static readonly TimeSpan DefaultThrottleDelay = TimeSpan.FromSeconds(2);

    /// <summary>lrclib's documentation says a 503 usually asks for one second.</summary>
    private static readonly TimeSpan DefaultBusyDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The header lrclib asks every client to send: name, version and a link. The version is read
    /// from the assembly rather than written here, because a literal drifts the moment the product
    /// version does - and lrclib uses this header to tell clients apart and to contact whoever runs
    /// one that misbehaves.
    /// </summary>
    public static string DefaultUserAgent { get; } = BuildDefaultUserAgent();

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly LrclibJsonContext Json = LrclibJsonContext.Default;

    private readonly string _userAgent;
    private readonly string _baseUrl;

    /// <summary>Null when the caller asked for an unpaced client, which only probes do.</summary>
    private readonly LrclibPacer? _pacer;
    private readonly LrclibBackoff _backoff = new();

    public LrclibClient(string userAgent)
        : this(userAgent, BaseUrl)
    {
    }

    /// <summary>
    /// Test seam: lets a probe aim the client at a local server that misbehaves on purpose.
    /// </summary>
    /// <param name="paced">
    /// False to drop the request pacing, which exists to protect a real service from a real app.
    /// A probe that makes sixty requests in a second - far more than any listening session - would
    /// otherwise spend that second waiting for its own allowance, and would be measuring the pacer
    /// rather than the behaviour it came to check. The pacing itself is measured by
    /// <c>probe/ApiBudget</c>, which uses one client per scenario.
    /// </param>
    internal LrclibClient(string userAgent, string baseUrl, bool paced = true)
    {
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent.Trim();
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? BaseUrl : baseUrl.TrimEnd('/');
        _pacer = paced ? new LrclibPacer() : null;
    }

    /// <summary>
    /// Exact lookup: <c>GET /api/get</c>. Returns <see langword="null"/> on 404 or any failure.
    /// </summary>
    public async Task<LyricsResult?> GetExactAsync(string artist, string track, string? album, double? durationSeconds, CancellationToken ct)
    {
        var outcome = await LookupExactAsync(artist, track, album, durationSeconds, ct).ConfigureAwait(false);
        return outcome.Lyrics;
    }

    /// <summary>
    /// Fuzzy lookup: <c>GET /api/search</c>. Picks the best candidate and returns
    /// <see langword="null"/> when the array is empty or nothing usable came back.
    /// </summary>
    public async Task<LyricsResult?> SearchAsync(string artist, string track, string? album, double? durationSeconds, CancellationToken ct)
    {
        var outcome = await LookupSearchAsync(artist, track, album, durationSeconds, ct).ConfigureAwait(false);
        return outcome.Lyrics;
    }

    /// <summary>Lookup by lrclib record id: <c>GET /api/get/{id}</c>.</summary>
    public async Task<LyricsResult?> GetByIdAsync(long id, CancellationToken ct)
    {
        var outcome = await LookupByIdAsync(id, ct).ConfigureAwait(false);
        return outcome.Lyrics;
    }

    /// <summary>Exact lookup that reports whether an empty answer was definitive.</summary>
    internal Task<LookupOutcome> LookupExactAsync(string artist, string track, string? album, double? durationSeconds, CancellationToken ct)
    {
        return GetRecordAsync(BuildGetUrl(artist, track, album, durationSeconds), ct);
    }

    /// <summary>Fuzzy lookup that reports whether an empty answer was definitive.</summary>
    internal async Task<LookupOutcome> LookupSearchAsync(string artist, string track, string? album, double? durationSeconds, CancellationToken ct)
    {
        var (status, best, _) = await LookupSearchDetailedAsync(artist, track, album, durationSeconds, ct)
            .ConfigureAwait(false);

        return new LookupOutcome(status, best);
    }

    /// <summary>
    /// Fuzzy lookup that also hands back every candidate, so the listener can be offered a choice of
    /// versions rather than being stuck with the automatic pick.
    /// </summary>
    /// <remarks>
    /// Two rules shape the answer. First, when the ranked pick carries no timings, the <em>first</em>
    /// record that does and that runs for the same length as the track being played is preferred, so
    /// "Lyrics not synced" becomes timed lyrics whenever lrclib holds a sheet for this performance.
    /// Second, lrclib caps <c>/api/search</c> at twenty records and that cap can hide the very record
    /// the first rule is looking for, so the search is repeated with the album and both arrays are
    /// ranked together. The merged list is also what the version menu shows.
    /// <para>
    /// The second search is only worth its cost when the first one came back <em>full</em>.
    /// <c>album_name</c> narrows a search rather than widening it - asking for an album that does not
    /// exist returns nothing at all, measured - so an album-scoped answer is always a subset of the
    /// unfiltered match set, and that subset can only be missing from a page that was truncated. A
    /// search that returned fewer than twenty records was complete, and the supplement cannot add a
    /// record to it.
    /// </para>
    /// </remarks>
    internal async Task<(LookupStatus Status, LyricsResult? Best, IReadOnlyList<LyricsCandidate> Candidates)>
        LookupSearchDetailedAsync(
            string artist,
            string track,
            string? album,
            double? durationSeconds,
            CancellationToken ct)
    {
        var (status, records) = await GetJsonAsync(
                BuildSearchUrl(artist, track, null), Json.LrclibGetResponseArray, ct)
            .ConfigureAwait(false);

        if (status != LookupStatus.Found || records is null)
        {
            return (status, null, Array.Empty<LyricsCandidate>());
        }

        // Ranked once and used for every purpose, so the version menu lists the most likely record
        // first and the automatic pick is the best of them that actually carries lyrics.
        var ranked = RankCandidates(records, track, album, durationSeconds);
        var best = Choose(ranked, durationSeconds);

        if (NeedsSyncedSibling(best)
            && durationSeconds is > 0
            && !string.IsNullOrWhiteSpace(album)
            && records.Length >= SearchResultCap)
        {
            var (extraStatus, extraRecords) = await GetJsonAsync(
                    BuildSearchUrl(artist, track, album), Json.LrclibGetResponseArray, ct)
                .ConfigureAwait(false);

            // A supplement that fails is not a failure: the primary answer still stands. The extra
            // request is only ever spent on a track the first search left unsynced, so a good match
            // still costs one request.
            if (extraStatus == LookupStatus.Found && extraRecords is { Length: > 0 })
            {
                ranked = RankCandidates(Merge(ranked, extraRecords), track, album, durationSeconds);
                best = Choose(ranked, durationSeconds);
            }
        }

        return (LookupStatus.Found, best, BuildCandidates(ranked));
    }

    /// <summary>Lookup by id that reports whether an empty answer was definitive.</summary>
    internal Task<LookupOutcome> LookupByIdAsync(long id, CancellationToken ct)
    {
        var url = string.Concat(_baseUrl, "/api/get/", id.ToString(CultureInfo.InvariantCulture));
        return GetRecordAsync(url, ct);
    }

    /// <summary>
    /// No-op: the <see cref="HttpClient"/> is shared process wide and intentionally outlives any
    /// single client instance, so there is nothing per-instance to release.
    /// </summary>
    public void Dispose()
    {
    }

    private static string BuildDefaultUserAgent()
    {
        // Guarded because this runs during static initialisation: if it threw, the first lyric
        // lookup would take the whole client down with a TypeInitializationException. A version
        // string is never worth that - a name and a link still identify the client to lrclib.
        try
        {
            var version = typeof(LrclibClient).Assembly.GetName().Version;
            var text = version is null ? "0.0.0" : version.ToString(3);

            return string.Concat("Overlyric/", text, " (+https://github.com/overlyric/overlyric)");
        }
        catch (Exception)
        {
            return "Overlyric (+https://github.com/overlyric/overlyric)";
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    private async Task<LookupOutcome> GetRecordAsync(string url, CancellationToken ct)
    {
        var (status, record) = await GetJsonAsync(url, Json.LrclibGetResponse, ct).ConfigureAwait(false);
        if (status != LookupStatus.Found)
        {
            return new LookupOutcome(status, null);
        }

        var result = ToResult(record);
        return result is null ? LookupOutcome.NotFound : LookupOutcome.Found(result);
    }

    /// <summary>
    /// Performs the request and classifies the result. A 404 means lrclib genuinely has no such
    /// track; a 429, a 5xx, a timeout and every other unexpected status mean it could not answer,
    /// which is emphatically not the same thing.
    /// </summary>
    /// <remarks>
    /// Two gates come before the send. The backoff gate honours <c>Retry-After</c>, and the pacer
    /// keeps requests sequential with a gap between them, as the documentation asks. When either
    /// gate would hold the request longer than a lyrics lookup can afford, nothing is sent and the
    /// answer is reported as unknown: a request that has not been made cannot be throttled.
    /// </remarks>
    private async Task<(LookupStatus Status, T? Value)> GetJsonAsync<T>(
        string url,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
        where T : class
    {
        try
        {
            if (!await _backoff.WaitAsync(ct).ConfigureAwait(false)
                || (_pacer is not null && !await _pacer.WaitAsync(ct).ConfigureAwait(false)))
            {
                return (LookupStatus.Unavailable, null);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await SharedHttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (LookupStatus.NotFound, null);
            }

            // Throttled. The window the server named applies to the whole client, so every later
            // request in this resolution waits for it too rather than asking again immediately.
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _backoff.Defer(RetryDelayFor(response, DefaultThrottleDelay));
                return (LookupStatus.Unavailable, null);
            }

            // Busy or broken: retryable, and never worth remembering as a miss. A 503 carries
            // Retry-After in practice, which is the server telling every client to come back later.
            if ((int)response.StatusCode >= 500)
            {
                _backoff.Defer(RetryDelayFor(response, DefaultBusyDelay));
                return (LookupStatus.Unavailable, null);
            }

            // Anything else - 400 ValidationError, 401 for a header we got wrong, a framework
            // rejection with no JSON body - is a fault on this side or a status we do not know.
            // It is not the statement "lrclib has no lyrics for this track", so it must never be
            // allowed to become a tombstone.
            if (!response.IsSuccessStatusCode)
            {
                return (LookupStatus.Unavailable, null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var value = await JsonSerializer.DeserializeAsync(stream, typeInfo, ct).ConfigureAwait(false);
            return (LookupStatus.Found, value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller cancelled: that must stay observable.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or System.IO.IOException)
        {
            // Timeout, DNS/TLS problem, truncated body: the answer is unknown, not "no lyrics".
            return (LookupStatus.Unavailable, null);
        }
    }

    /// <summary>
    /// How long the server asked clients to wait, from either form the header allows: delta-seconds
    /// or an HTTP date. Falls back to a default when the header is missing, which is what happens
    /// when the network edge rejects a request before the API sees it.
    /// </summary>
    private static TimeSpan RetryDelayFor(HttpResponseMessage response, TimeSpan fallback)
    {
        var header = response.Headers.RetryAfter;

        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return fallback;
    }

    private string BuildGetUrl(string artist, string track, string? album, double? durationSeconds)
    {
        var url = new StringBuilder(_baseUrl).Append("/api/get");
        var first = true;

        AppendQuery(url, ref first, "artist_name", artist);
        AppendQuery(url, ref first, "track_name", track);
        AppendQuery(url, ref first, "album_name", album);

        // lrclib validates the range strictly and answers 400 outside it: a track longer than an
        // hour - a DJ set, a live prog epic, a movement of a mass - would otherwise send a request
        // that cannot succeed. Leaving the parameter out costs the two-second matching window on
        // those tracks, which is far cheaper than a request that is guaranteed to be rejected.
        if (durationSeconds is { } duration
            && !double.IsNaN(duration)
            && !double.IsInfinity(duration)
            && duration is >= 1 and <= 3600)
        {
            AppendQuery(url, ref first, "duration", duration.ToString("0.###", CultureInfo.InvariantCulture));
        }

        return url.ToString();
    }

    private string BuildSearchUrl(string artist, string track, string? album)
    {
        var url = new StringBuilder(_baseUrl).Append("/api/search");
        var first = true;

        AppendQuery(url, ref first, "track_name", track);
        AppendQuery(url, ref first, "artist_name", artist);

        // The album is what makes the second, narrower search find the record the twenty-record cap
        // hid from the first one; it is deliberately left out of the default query.
        AppendQuery(url, ref first, "album_name", album);

        return url.ToString();
    }

    private static void AppendQuery(StringBuilder url, ref bool first, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        url.Append(first ? '?' : '&');
        first = false;
        url.Append(name).Append('=').Append(Uri.EscapeDataString(value.Trim()));
    }

    /// <summary>
    /// Orders candidates by how likely each is to be the recording that is actually playing,
    /// best first.
    /// </summary>
    /// <remarks>
    /// Duration dominates on purpose. A synced lyric sheet for a different performance is worse than
    /// untimed lyrics for the right one, because it would drift out of step with the music; so a
    /// candidate that is minutes out of length can never win on the strength of having timings.
    /// Among candidates whose duration agrees, timings, an exact title and a matching album break the
    /// tie. The title is compared after cleaning, so a remaster suffix does not disqualify the record
    /// that matches the playing track.
    /// </remarks>
    private static List<LrclibGetResponse> RankCandidates(
        IReadOnlyList<LrclibGetResponse> records,
        string? wantedTitle,
        string? wantedAlbum,
        double? durationSeconds)
    {
        var knownDuration = durationSeconds is > 0;
        var target = knownDuration ? durationSeconds!.Value : 0;
        var tightTolerance = MatchTolerance(target);
        var looseTolerance = Math.Max(8.0, target * 0.02);

        var comparisonTitle = MetadataNormaliser.ForComparison(MetadataNormaliser.CleanTitle(wantedTitle));

        // The album is compared after cleaning too, because players append edition suffixes that lrclib
        // omits: "Scheherazade and Other Stories (Remastered)" must still match
        // "Scheherazade and Other Stories".
        var comparisonAlbum = MetadataNormaliser.ForComparison(MetadataNormaliser.CleanTitle(wantedAlbum));

        var scored = new List<(LrclibGetResponse Record, double Score)>(records.Count);

        foreach (var record in records)
        {
            var score = 0.0;

            if (knownDuration && record.Duration is > 0)
            {
                var delta = Math.Abs(record.Duration.Value - target);

                score += delta <= tightTolerance ? 1000.0
                    : delta <= looseTolerance ? 400.0
                    : -delta;
            }

            if (comparisonTitle.Length > 0
                && MetadataNormaliser.ForComparison(MetadataNormaliser.CleanTitle(record.TrackName)) == comparisonTitle)
            {
                score += 300.0;
            }

            if (comparisonAlbum.Length > 0
                && MetadataNormaliser.ForComparison(MetadataNormaliser.CleanTitle(record.AlbumName)) == comparisonAlbum)
            {
                score += 250.0;
            }

            if (!string.IsNullOrWhiteSpace(record.SyncedLyrics))
            {
                score += 200.0;
            }

            // Word timings are what karaoke needs, so a sheet that has them wins against one that
            // only has line timings when everything else is equal.
            if (record.HasWordSync == true)
            {
                score += 250.0;
            }

            scored.Add((record, score));
        }

        // Stable within equal scores, so lrclib's own ordering survives as the final tie-break.
        var ranked = new List<LrclibGetResponse>(scored.Count);
        foreach (var entry in scored.OrderByDescending(e => e.Score))
        {
            ranked.Add(entry.Record);
        }

        return ranked;
    }

    /// <summary>
    /// The band within which two durations count as the same performance. Two seconds may look
    /// generous, but a player's reported length and lrclib's differ by a second or so as a matter of
    /// course - the track playing while this was written reports 246.4 s where the synced sheet runs
    /// 244.7 s - and lrclib's own exact endpoint uses the same two-second window. The proportional
    /// term only matters for very long pieces.
    /// </summary>
    private static double MatchTolerance(double targetSeconds) => Math.Max(2.0, targetSeconds * 0.005);

    private static bool DurationsMatch(double? candidateSeconds, double targetSeconds) =>
        candidateSeconds is > 0
        && Math.Abs(candidateSeconds.Value - targetSeconds) <= MatchTolerance(targetSeconds);

    /// <summary>
    /// True when the pick that was reached is not synced, and a timed sheet for the same performance
    /// would therefore be an improvement over it.
    /// </summary>
    /// <remarks>
    /// Plain lyrics qualify, an empty answer qualifies, and so does a verdict of instrumental: that
    /// verdict is a statement about the record that carried the flag, not a promise that no record
    /// for this track holds timings, and a sheet that is timed <em>and</em> the same length is the
    /// better answer. Nothing is overridden on the strength of timings alone - the length has to
    /// agree - so an instrumental track with no timed sheet of its own keeps saying so.
    /// </remarks>
    private static bool NeedsSyncedSibling(LyricsResult? best) => best is null || !best.IsSynced;

    /// <summary>The highest-ranked record that yields anything usable, or null when none does.</summary>
    private static LyricsResult? FirstUsable(List<LrclibGetResponse> ranked)
    {
        foreach (var record in ranked)
        {
            var result = ToResult(record);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>
    /// The pick for a ranked set: the best record that carries anything, upgraded to the first record
    /// that carries timings for this performance when the best one does not.
    /// </summary>
    private static LyricsResult? Choose(List<LrclibGetResponse> ranked, double? durationSeconds)
    {
        var best = FirstUsable(ranked);

        if (!NeedsSyncedSibling(best))
        {
            return best;
        }

        return FirstSyncedWithMatchingDuration(ranked, durationSeconds) ?? best;
    }

    /// <summary>
    /// The first record in rank order that carries real timings <em>and</em> runs for the same length
    /// as the track being played.
    /// </summary>
    /// <remarks>
    /// Duration is the whole point. A synced sheet for a different performance drifts out of step
    /// with the music, so it is not an answer to "these lyrics are not synced" - it is a different
    /// problem. A record whose timings are real but belongs to another song is rejected here on
    /// length alone, which is the same signal the ranking already trusts. When the playing track's
    /// length is unknown there is nothing to compare against, so the rule cannot apply.
    /// </remarks>
    private static LyricsResult? FirstSyncedWithMatchingDuration(List<LrclibGetResponse> ranked, double? durationSeconds)
    {
        if (durationSeconds is not > 0)
        {
            return null;
        }

        var target = durationSeconds.Value;

        foreach (var record in ranked)
        {
            if (!DurationsMatch(record.Duration, target))
            {
                continue;
            }

            // ToResult only reports IsSynced for a body that parses to real lines, so a placeholder
            // body such as "[Instrumental]" can never be chosen here.
            if (ToResult(record) is { IsSynced: true } synced)
            {
                return synced;
            }
        }

        return null;
    }

    /// <summary>
    /// Concatenates the two search results, dropping records the first search had already returned.
    /// </summary>
    private static List<LrclibGetResponse> Merge(List<LrclibGetResponse> primary, LrclibGetResponse[] extra)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<LrclibGetResponse>(primary.Count + extra.Length);

        Add(primary);
        Add(extra);
        return merged;

        void Add(IReadOnlyList<LrclibGetResponse> records)
        {
            foreach (var record in records)
            {
                if (seen.Add(IdentityOf(record)))
                {
                    merged.Add(record);
                }
            }
        }
    }

    /// <summary>
    /// Identity for de-duplication across the two searches. lrclib sends an id with every record, so
    /// the id is the identity; the composite fallback only covers records that arrive without one.
    /// </summary>
    private static string IdentityOf(LrclibGetResponse record)
    {
        if (record.Id > 0)
        {
            return string.Concat("id:", record.Id.ToString(CultureInfo.InvariantCulture));
        }

        return string.Concat(
            "x:", record.TrackName, "\u0001", record.AlbumName, "\u0001",
            record.Duration?.ToString("0.###", CultureInfo.InvariantCulture),
            "\u0001", string.IsNullOrWhiteSpace(record.SyncedLyrics) ? "plain" : "synced");
    }

    private static List<LyricsCandidate> BuildCandidates(List<LrclibGetResponse> ranked)
    {
        var candidates = new List<LyricsCandidate>(ranked.Count);

        foreach (var record in ranked)
        {
            if (record.Id <= 0)
            {
                continue;
            }

            candidates.Add(ToCandidate(record));
        }

        return candidates;
    }

    /// <summary>
    /// One ranked record described for the selector. The bodies are inspected rather than trusted:
    /// "syncedLyrics is not null" is also true for a record whose whole body is the word
    /// "[Instrumental]", and a badge that promises timings must be able to keep the promise.
    /// </summary>
    private static LyricsCandidate ToCandidate(LrclibGetResponse record)
    {
        var fileLines = record.HasWordSync == true
            ? LyricsFile.ParseLines(record.LyricsFile)
            : Array.Empty<LyricLine>();

        var wordSync = false;
        foreach (var line in fileLines)
        {
            if (line.HasWordTimings)
            {
                wordSync = true;
                break;
            }
        }

        var fileUsable = fileLines.Count > 0 && !InstrumentalDetector.IsPlaceholderOnly(fileLines);

        IReadOnlyList<LyricLine> synced = string.IsNullOrWhiteSpace(record.SyncedLyrics)
            ? []
            : LrcParser.Parse(record.SyncedLyrics!);
        var syncedUsable = synced.Count > 0 && !InstrumentalDetector.IsPlaceholderOnly(synced);

        List<LyricLine> plain = string.IsNullOrWhiteSpace(record.PlainLyrics)
            ? []
            : SplitPlainLyrics(record.PlainLyrics!);
        var plainUsable = plain.Count > 0 && !InstrumentalDetector.IsPlaceholderOnly(plain);

        var bodyDeclaresInstrumental =
            InstrumentalDetector.IsPlaceholderText(record.SyncedLyrics)
            || InstrumentalDetector.IsPlaceholderText(record.PlainLyrics);

        var hasSynced = fileUsable || syncedUsable;

        return new LyricsCandidate
        {
            Id = record.Id,
            TrackName = record.TrackName,
            ArtistName = record.ArtistName,
            AlbumName = record.AlbumName,
            DurationSeconds = record.Duration,
            HasSynced = hasSynced,
            HasPlain = plainUsable,
            HasWordSync = wordSync && fileUsable,
            IsInstrumental = (record.Instrumental ?? false)
                             || (!hasSynced && !plainUsable && bodyDeclaresInstrumental),
            SyncedLineCount = fileUsable ? fileLines.Count : syncedUsable ? synced.Count : 0,
            PlainLineCount = plainUsable ? plain.Count : 0
        };
    }

    private static LyricsResult? ToResult(LrclibGetResponse? record)
    {
        if (record is null)
        {
            return null;
        }

        var instrumental = record.Instrumental ?? false;

        // Synced lyrics win, then plain lyrics. In both cases a body that is nothing but placeholder
        // text ("Instrumental", "[Instrumental]", "[Solo]" ...) is not lyrics and must not be shown
        // as such, so it is skipped in favour of the other body before any decision is made.
        // Word-level timings live only in the lyricsfile, and they are the better source when they
        // are there: they carry the line ends too, which plain LRC has to guess from the next line.
        if (record.HasWordSync == true)
        {
            var fromFile = LyricsFile.ParseLines(record.LyricsFile);

            if (fromFile.Count > 0 && !InstrumentalDetector.IsPlaceholderOnly(fromFile))
            {
                return Build(record, fromFile, isSynced: true, instrumental);
            }
        }

        IReadOnlyList<LyricLine> synced = string.IsNullOrWhiteSpace(record.SyncedLyrics)
            ? Array.Empty<LyricLine>()
            : LrcParser.Parse(record.SyncedLyrics!);

        if (synced.Count > 0 && !InstrumentalDetector.IsPlaceholderOnly(synced))
        {
            return Build(record, synced, isSynced: true, instrumental);
        }

        IReadOnlyList<LyricLine> plain = string.IsNullOrWhiteSpace(record.PlainLyrics)
            ? Array.Empty<LyricLine>()
            : SplitPlainLyrics(record.PlainLyrics!);

        if (plain.Count > 0 && !InstrumentalDetector.IsPlaceholderOnly(plain))
        {
            return Build(record, plain, isSynced: false, instrumental);
        }

        // Neither body holds real lyrics. Either lrclib says so outright, or the body itself declares
        // it - and that second case is worth catching, because the flag is frequently left unset.
        var bodyDeclaresInstrumental =
            InstrumentalDetector.IsPlaceholderText(record.SyncedLyrics)
            || InstrumentalDetector.IsPlaceholderText(record.PlainLyrics);

        if (instrumental || bodyDeclaresInstrumental)
        {
            // A known instrumental: a valid, cacheable "there is nothing to sing" answer.
            return Build(record, Array.Empty<LyricLine>(), isSynced: false, instrumental: true);
        }

        // Nothing usable in this record at all. Reporting the miss lets the service fall through to
        // /api/search and then negative-cache the track.
        return null;
    }

    private static LyricsResult Build(LrclibGetResponse record, IReadOnlyList<LyricLine> lines, bool isSynced, bool instrumental)
    {
        return new LyricsResult
        {
            Lines = lines,
            IsSynced = isSynced,
            IsInstrumental = instrumental,
            PlainLyrics = record.PlainLyrics,
            LrclibId = record.Id > 0 ? record.Id : null,
            TrackName = record.TrackName,
            ArtistName = record.ArtistName,
            DurationSeconds = record.Duration,
        };
    }

    private static List<LyricLine> SplitPlainLyrics(string plainLyrics)
    {
        var lines = new List<LyricLine>();
        foreach (var line in plainLyrics.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            lines.Add(new LyricLine(TimeSpan.Zero, line));
        }

        return lines;
    }
}

/// <summary>One lrclib record (<c>/api/get</c> object or one <c>/api/search</c> array element).</summary>
internal sealed class LrclibGetResponse
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("trackName")]
    public string? TrackName { get; init; }

    [JsonPropertyName("artistName")]
    public string? ArtistName { get; init; }

    [JsonPropertyName("albumName")]
    public string? AlbumName { get; init; }

    [JsonPropertyName("duration")]
    public double? Duration { get; init; }

    /// <summary>Nullable on purpose: lrclib omits or nulls this field on some records.</summary>
    [JsonPropertyName("instrumental")]
    public bool? Instrumental { get; init; }

    /// <summary>Legitimately null even for non-instrumental tracks.</summary>
    [JsonPropertyName("plainLyrics")]
    public string? PlainLyrics { get; init; }

    [JsonPropertyName("syncedLyrics")]
    public string? SyncedLyrics { get; init; }

    /// <summary>True when <see cref="LyricsFile"/> carries word-level timings.</summary>
    [JsonPropertyName("hasWordSync")]
    public bool? HasWordSync { get; init; }

    /// <summary>The raw lyricsfile YAML, which every record carries.</summary>
    [JsonPropertyName("lyricsfile")]
    public string? LyricsFile { get; init; }
}

/// <summary>
/// Source-generated serialisation metadata. Reflection based <c>System.Text.Json</c> is unavailable
/// with <c>PublishAot=true</c>, so every read/write must go through this context.
/// </summary>
[JsonSerializable(typeof(LrclibGetResponse))]
[JsonSerializable(typeof(LrclibGetResponse[]))]
internal sealed partial class LrclibJsonContext : JsonSerializerContext;
