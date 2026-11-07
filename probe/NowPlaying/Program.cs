using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Overlyric.Lyrics;
using Windows.Media.Control;

namespace Overlyric.NowPlaying;

/// <summary>
/// Reproduction and acceptance harness for lyric matching. It answers, for the track on the machine
/// right now - or for a track named on the command line - "what is playing, what does lrclib hold for
/// it, and what would Overlyric actually show?".
/// <para>
/// The point of the harness is that its verdict is the overlay's verdict: the app's own
/// <see cref="LrclibClient"/>, <see cref="LyricsService"/> and <see cref="LyricsCache"/> are compiled
/// in from <c>src\Overlyric\Lyrics\**</c> and used directly. The two raw <c>/api/search</c> probes at
/// the top are a diagnostic only (they exist to show lrclib's 20-record cap), never a second opinion
/// on the match: the deciding answer is the <see cref="LyricsService.GetLyricsAsync"/> result and the
/// cache entry it leaves behind.
/// </para>
/// </summary>
internal static class Program
{
    private const string UserAgent = "Overlyric/0.1.0 (probe NowPlaying)";
    private const string BaseUrl = "https://lrclib.net";

    /// <summary>lrclib's <c>/api/search</c> never returns more records than this.</summary>
    private const int SearchCap = 20;

    /// <summary>Gap between network steps so the probe does not trip lrclib's burst throttling.</summary>
    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(900);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var query = args.Length >= 2
            ? new TrackQuery(
                args[0],
                args[1],
                args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : null,
                ParseDuration(args.Length > 3 ? args[3] : null))
            : await ReadNowPlayingAsync().ConfigureAwait(false);

        if (query is null)
        {
            Console.WriteLine("NO_SESSION: nothing is publishing to Windows media controls.");
            return 1;
        }

        var appDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Overlyric");
        var cacheDirectory = Path.Combine(appDirectory, "cache");

        var cache = new LyricsCache(cacheDirectory);
        using var client = new LrclibClient(UserAgent);

        // The explicit choices are part of the overlay's verdict: a pinned record or an imported
        // file outranks the automatic answer, so the harness has to read the same two stores the app
        // reads or it would report a different track than the one on screen.
        var service = new LyricsService(
            client,
            cache,
            LyricSelectionStore.Load(Path.Combine(appDirectory, "selections.json")),
            new LocalLyricsStore(Path.Combine(appDirectory, "lyrics")));

        var cleanTitle = MetadataNormaliser.CleanTitle(query.Title, query.Artist);
        var key = LyricsCache.MakeKey(query.Artist, query.Title, query.Album);
        var target = query.DurationSeconds;
        var tolerance = target is { } t0 ? Math.Max(2.0, t0 * 0.005) : (double?)null;

        Console.WriteLine("===============================================================");
        Console.WriteLine("Overlyric live lyrics harness (runs the app's own Lyrics/* code)");
        Console.WriteLine("===============================================================");
        Console.WriteLine($"app      : {query.SourceApp ?? "(command line)"}");
        Console.WriteLine($"artist   : {query.Artist}");
        Console.WriteLine($"title    : {query.Title}");
        if (!string.Equals(cleanTitle, query.Title, StringComparison.Ordinal))
        {
            Console.WriteLine($"cleaned  : '{cleanTitle}'   (what is sent to lrclib)");
        }

        Console.WriteLine($"album    : {query.Album ?? "(none)"}");
        Console.WriteLine($"duration : {Describe(query.DurationSeconds)}");
        if (query.Position is not null)
        {
            Console.WriteLine($"position : {query.Position.Value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)}  status={query.Status}");
        }

        Console.WriteLine($"expected : {ExpectedNote(query) ?? "(no recorded expectation for this track - reporting whatever lrclib returns)"}");

        Console.WriteLine();
        Console.WriteLine("=== 1. what the real cache holds for this track (what the overlay shows) ===");
        DumpCached(cache, cacheDirectory, key);

        Console.WriteLine();
        Console.WriteLine("=== 2. /api/get (exact, cleaned title, real album, real duration) ===");
        var plain = DumpExact(client, query, cleanTitle);

        await PauseAsync().ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("=== 3. raw /api/search: PRIMARY (track_name + artist_name) ===");
        var primary = await RawSearchAsync(BuildSearchUrl(query.Artist, cleanTitle, null)).ConfigureAwait(false);
        PrintRawSearch(primary, target, tolerance, missedByPrimary: null);

        await PauseAsync().ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("=== 4. raw /api/search: ALBUM-SCOPED (track_name + artist_name + album_name) ===");
        RawSearch? scoped = null;
        if (string.IsNullOrWhiteSpace(query.Album))
        {
            Console.WriteLine("(the track metadata carries no album, so the album-scoped query cannot be formed)");
        }
        else
        {
            scoped = await RawSearchAsync(BuildSearchUrl(query.Artist, cleanTitle, query.Album)).ConfigureAwait(false);
            var primaryIds = primary.Failure is null ? primary.Records.Select(r => r.Id).ToHashSet() : [];
            PrintRawSearch(scoped, target, tolerance, missedByPrimary: primaryIds);
        }

        DumpCoverageDiff(primary, scoped, target, tolerance);

        await PauseAsync().ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("=== 5. the ranked candidate list the app sees after the merge (LyricsService.GetVersionsAsync) ===");
        var versions = await service
            .GetVersionsAsync(query.Artist, query.Title, query.Album, query.DurationSeconds, CancellationToken.None)
            .ConfigureAwait(false);

        var rulePick = PrintVersions(versions, target, tolerance);
        DumpVersionsGap(scoped, versions, primary, target, tolerance);

        await PauseAsync().ConfigureAwait(false);

        // LyricsResult carries no album, so the album for whatever id a lookup answers with is looked
        // up in the raw lrclib responses above (best effort; "(unknown)" when absent).
        var albumById = new Dictionary<long, string?>();
        foreach (var record in primary.Records)
        {
            if (record.Id > 0)
            {
                albumById[record.Id] = record.AlbumName;
            }
        }

        if (scoped is not null)
        {
            foreach (var record in scoped.Records)
            {
                if (record.Id > 0)
                {
                    albumById[record.Id] = record.AlbumName;
                }
            }
        }

        string AlbumOf(long? id) => id is { } value && albumById.TryGetValue(value, out var album) && !string.IsNullOrWhiteSpace(album)
            ? $"\"{album}\""
            : "(unknown)";

        Console.WriteLine();
        Console.WriteLine("=== 6. cold replay: a stale cache entry re-resolved over the network (temp dir) ===");
        Console.WriteLine("why      : a warm cache entry can answer the decisive check below without touching the network; this seeds a");
        Console.WriteLine("           matchVersion=0 copy of the /api/get record in a temp dir and shows the app ignore it and resolve live");

        if (plain is null)
        {
            Console.WriteLine("(skipped: /api/get returned nothing this run, so the pre-fix plain record could not be reseeded)");
        }
        else
        {
            var seedDirectory = Path.Combine(Path.GetTempPath(), "overlyric-probe-cold-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(seedDirectory);

                var staleEntry = new LyricsCacheEntry
                {
                    Key = key,
                    MatchVersion = 0,
                    Result = CachedLyrics.FromLyricsResult(plain),
                };

                File.WriteAllText(
                    Path.Combine(seedDirectory, Hash(key) + ".json"),
                    JsonSerializer.Serialize(staleEntry, LyricsCacheJsonContext.Default.LyricsCacheEntry),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                Console.WriteLine($"tempDir  : {seedDirectory}   (the real cache directory is not touched by this section)");
                Console.WriteLine($"seeded   : matchVersion=0 (the pre-fix stamp) with the /api/get record: {DescribeResult(plain, AlbumOf)}");
                Console.WriteLine($"on-disk  : {PeekRawCacheFile(seedDirectory, key)}");

                var coldCache = new LyricsCache(seedDirectory);
                var staleReadable = TryReadCache(coldCache, key, out var stale);
                Console.WriteLine($"app reader on the stale entry: {DescribeCached(stale, staleReadable)}");
                Console.WriteLine("source   : that reader refused the entry, so the answer below was produced without a usable cache entry (the network)");

                using var coldClient = new LrclibClient(UserAgent);
                var coldService = new LyricsService(
                    coldClient,
                    coldCache,
                    LyricSelectionStore.Load(Path.Combine(seedDirectory, "selections.json")),
                    new LocalLyricsStore(Path.Combine(seedDirectory, "lyrics")));
                var cold = await coldService
                    .GetLyricsAsync(query.Artist, query.Title, query.Album, query.DurationSeconds, CancellationToken.None)
                    .ConfigureAwait(false);

                Console.WriteLine($"result   : {DescribeResult(cold, AlbumOf)}");
                if (cold is { IsSynced: true })
                {
                    Console.WriteLine($"first timed line: {FirstTimedLine(cold.Lines)}");
                }

                Console.WriteLine($"on-disk  : {PeekRawCacheFile(seedDirectory, key)}");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(seedDirectory))
                    {
                        Directory.Delete(seedDirectory, recursive: true);
                        Console.WriteLine($"CLEANUP  : deleted {seedDirectory}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"CLEANUP  : could not delete {seedDirectory}: {ex.GetType().Name}");
                }
            }
        }

        await PauseAsync().ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("=== 7. decisive: LyricsService.GetLyricsAsync (the app's real cache directory, real network) ===");
        Console.WriteLine($"cacheDir : {cacheDirectory}");
        Console.WriteLine($"cacheKey : \"{key.Replace("\n", "\\n", StringComparison.Ordinal)}\"");

        // Read-only look at the app's pin file. The harness deliberately builds LyricsService without
        // overrides: a hand-pinned version would mask the automatic rule that is under test here.
        var overridesPath = Path.Combine(Path.GetDirectoryName(cacheDirectory) ?? cacheDirectory, "versions.json");
        var overrides = VersionOverrides.Load(overridesPath);
        Console.WriteLine(overrides.TryGet(key, out var pinnedId)
            ? $"pin      : {overridesPath} pins this track to lrclib id {pinnedId}; the app's LyricsService is built with that override and answers with the pin, "
              + "whereas this harness builds it WITHOUT overrides so the automatic rule is what gets measured"
            : $"pin      : (no hand-pinned version for this track in {overridesPath})");

        var cacheHadEntry = TryReadCache(cache, key, out var before);
        Console.WriteLine($"before   : {DescribeCached(before, cacheHadEntry)}");
        Console.WriteLine($"on-disk  : {PeekRawCacheFile(cacheDirectory, key)}");

        LyricsResult? result;
        try
        {
            result = await service
                .GetLyricsAsync(query.Artist, query.Title, query.Album, query.DurationSeconds, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("result   : (cancelled)");
            result = null;
        }

        var cacheHasEntry = TryReadCache(cache, key, out var after);
        Console.WriteLine($"result   : {DescribeResult(result, AlbumOf)}");

        if (result is { IsSynced: true })
        {
            Console.WriteLine($"first timed line: {FirstTimedLine(result.Lines)}");
        }

        Console.WriteLine($"after    : {DescribeCached(after, cacheHasEntry)}");
        Console.WriteLine($"on-disk  : {PeekRawCacheFile(cacheDirectory, key)}");

        Console.WriteLine();
        Console.WriteLine("=== verdict ===");
        DumpVerdict(result, rulePick, plain, AlbumOf, cacheHadEntry && before is { IsSynced: true });

        // The acceptance evidence for the user's request: what Overlyric would show for this track,
        // taken from the app's own service rather than from the harness's reading of lrclib.
        Console.WriteLine();
        Console.WriteLine($"VERDICT : {VerdictLine(result, AlbumOf, cacheHadEntry)}");

        return 0;
    }

    private static async Task<TrackQuery?> ReadNowPlayingAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var session = manager.GetCurrentSession();
        if (session is null)
        {
            return null;
        }

        var props = await session.TryGetMediaPropertiesAsync();
        var timeline = session.GetTimelineProperties();
        var playback = session.GetPlaybackInfo();

        var duration = (timeline.EndTime - timeline.StartTime).TotalSeconds;

        return new TrackQuery(
            props?.Artist ?? string.Empty,
            props?.Title ?? string.Empty,
            string.IsNullOrWhiteSpace(props?.AlbumTitle) ? null : props!.AlbumTitle,
            duration > 0 ? duration : null)
        {
            SourceApp = session.SourceAppUserModelId,
            Position = timeline.Position,
            Status = playback.PlaybackStatus.ToString(),
        };
    }

    // ---------------------------------------------------------------- section 1: cache

    private static void DumpCached(LyricsCache cache, string directory, string key)
    {
        if (cache.IsMiss(key))
        {
            Console.WriteLine("negative-cached: this track is remembered as having no lyrics at all.");
            Console.WriteLine($"on-disk  : {PeekRawCacheFile(directory, key, miss: true)}");
            return;
        }

        if (TryReadCache(cache, key, out var cached))
        {
            Console.WriteLine($"lrclibId : {cached!.LrclibId?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}");
            Console.WriteLine($"synced   : {cached.IsSynced}   instrumental: {cached.IsInstrumental}   lines: {cached.Lines.Count} ({CountTimed(cached.Lines)} timed)");
            Console.WriteLine($"record   : {cached.TrackName} / {cached.ArtistName} / {Describe(cached.DurationSeconds)}");
        }
        else
        {
            Console.WriteLine("(no entry)");
        }

        Console.WriteLine($"on-disk  : {PeekRawCacheFile(directory, key)}");
    }

    private static bool TryReadCache(LyricsCache cache, string key, out LyricsResult? result)
    {
        try
        {
            return cache.TryGet(key, out result) && result is not null;
        }
        catch (Exception)
        {
            result = null;
            return false;
        }
    }

    private static string DescribeCached(LyricsResult? cached, bool present)
    {
        if (!present || cached is null)
        {
            return "(no usable entry: absent, unreadable, or written by an older matcher)";
        }

        return $"{(cached.IsSynced ? "SYNCED" : cached.IsInstrumental ? "instrumental" : "PLAIN")} "
               + $"id={cached.LrclibId?.ToString(CultureInfo.InvariantCulture) ?? "(none)"} "
               + $"{cached.TrackName ?? "(unnamed)"} / {Describe(cached.DurationSeconds)} "
               + $"lines={cached.Lines.Count} ({CountTimed(cached.Lines)} timed)";
    }

    /// <summary>The cache's on-disk file name, reproduced so the harness can read the file the app wrote.</summary>
    private static string Hash(string key)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>
    /// Reads the cache file the app wrote, bypassing the app's own reader. This is how the harness can
    /// see that a stale entry is still on disk while the app ignores it, and can show any matcher
    /// version stamp the entry carries.
    /// </summary>
    private static string PeekRawCacheFile(string directory, string key, bool miss = false)
    {
        var hash = Hash(key);
        var entryPath = Path.Combine(directory, hash + ".json");
        var missPath = Path.Combine(directory, hash + ".miss.json");
        var wanted = miss ? missPath : entryPath;

        try
        {
            foreach (var candidate in new[] { wanted, miss ? entryPath : missPath })
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var json = File.ReadAllText(candidate, Encoding.UTF8);
                var stamp = new FileInfo(candidate).LastWriteTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

                if (string.IsNullOrWhiteSpace(json))
                {
                    return $"{Path.GetFileName(candidate)}  {stamp}  (empty file)";
                }

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                var extras = root
                    .EnumerateObject()
                    .Select(p => p.Name)
                    .Where(n => !string.Equals(n, "key", StringComparison.Ordinal)
                                && !string.Equals(n, "result", StringComparison.Ordinal))
                    .ToArray();

                var resultField = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
                if (resultField.ValueKind != JsonValueKind.Object)
                {
                    return $"{Path.GetFileName(candidate)}  {stamp}  tombstone (result=null)"
                           + (extras.Length > 0 ? $"  extras=[{string.Join(",", extras)}]" : string.Empty);
                }

                var isSynced = resultField.TryGetProperty("isSynced", out var s) && s.ValueKind is JsonValueKind.True;
                var id = resultField.TryGetProperty("lrclibId", out var i) && i.ValueKind == JsonValueKind.Number
                    ? i.GetInt64().ToString(CultureInfo.InvariantCulture)
                    : "(none)";
                var duration = resultField.TryGetProperty("durationSeconds", out var d) && d.ValueKind == JsonValueKind.Number
                    ? d.GetDouble().ToString("0.###", CultureInfo.InvariantCulture) + "s"
                    : "(unknown)";
                var trackName = resultField.TryGetProperty("trackName", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()
                    : "(unnamed)";
                var lineCount = resultField.TryGetProperty("lines", out var l) && l.ValueKind == JsonValueKind.Array
                    ? l.GetArrayLength()
                    : 0;

                return $"{Path.GetFileName(candidate)}  {stamp}  isSynced={isSynced.ToString().ToLowerInvariant()} "
                       + $"id={id} {trackName} {duration} lines={lineCount}"
                       + $"  extras=[{(extras.Length > 0 ? string.Join(",", extras) : "none")}]"
                       + $"  innerKeys=[{(resultField.ValueKind == JsonValueKind.Object ? string.Join(",", resultField.EnumerateObject().Select(p => p.Name)) : string.Empty)}]";
            }

            return $"(no file: {Path.GetFileName(wanted)} does not exist)";
        }
        catch (Exception ex)
        {
            return $"(unreadable: {ex.GetType().Name})";
        }
    }

    // ---------------------------------------------------------------- section 2: /api/get

    private static LyricsResult? DumpExact(LrclibClient client, TrackQuery query, string cleanTitle)
    {
        var result = client
            .GetExactAsync(query.Artist, cleanTitle, query.Album, query.DurationSeconds, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (result is null)
        {
            Console.WriteLine("(nothing)");
            return null;
        }

        Console.WriteLine($"lrclibId : {result.LrclibId?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}   synced: {result.IsSynced}   instrumental: {result.IsInstrumental}   lines: {result.Lines.Count} ({CountTimed(result.Lines)} timed)");
        Console.WriteLine($"record   : {result.TrackName} / {result.ArtistName} / {Describe(result.DurationSeconds)}");
        return result;
    }

    // ---------------------------------------------------------------- sections 3 and 4: raw searches

    private static async Task<RawSearch> RawSearchAsync(string url)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
                var code = (int)response.StatusCode;

                // lrclib throttles bursts: 429/5xx is "ask again", never a data point about the track.
                if (code is 429 || code >= 500)
                {
                    Console.WriteLine($"  attempt {attempt}: HTTP {code} (lrclib busy - transient, retrying)");
                    await Task.Delay(TimeSpan.FromMilliseconds(1200 * attempt)).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new RawSearch(url, code, [], $"HTTP {code}");
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var records = JsonSerializer.Deserialize(json, LrclibJsonContext.Default.LrclibGetResponseArray) ?? [];
                return new RawSearch(url, code, records, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                Console.WriteLine($"  attempt {attempt}: {ex.GetType().Name} (transient, retrying)");
                await Task.Delay(TimeSpan.FromMilliseconds(1200 * attempt)).ConfigureAwait(false);
            }
        }

        return new RawSearch(url, 0, [], "gave up after 5 attempts (lrclib stayed busy - not treated as a data point)");
    }

    private static void PrintRawSearch(RawSearch search, double? target, double? tolerance, HashSet<long>? missedByPrimary)
    {
        Console.WriteLine($"url      : {search.Url}");

        if (search.Failure is not null)
        {
            Console.WriteLine($"status   : {search.Failure}");
            Console.WriteLine("(no data: reporting nothing rather than guessing)");
            return;
        }

        Console.WriteLine($"status   : {search.StatusCode}   records: {search.Records.Count}{(search.Records.Count >= SearchCap ? $"  (hit lrclib's {SearchCap}-record cap)" : string.Empty)}");
        Console.WriteLine($"target   : {Describe(target)}   tolerance for \"same duration\": max(2.0, duration*0.005) = {DescribeTolerance(tolerance)}");
        Console.WriteLine();

        var syncedHere = 0;
        var syncedMissed = 0;

        for (var i = 0; i < search.Records.Count; i++)
        {
            var record = search.Records[i];
            var synced = IsSyncedRecord(record);
            if (synced)
            {
                syncedHere++;
            }

            var missed = synced && missedByPrimary is not null && !missedByPrimary.Contains(record.Id);
            if (missed)
            {
                syncedMissed++;
            }

            Console.WriteLine(
                $"  [{i,2}] {Delta(record.Duration, target)}  {Kind(record),-13} id={record.Id,-9} {record.TrackName}  ({record.AlbumName})"
                + (missed ? "   <== MISSED BY THE PRIMARY QUERY" : string.Empty));
        }

        Console.WriteLine();
        Console.WriteLine($"synced records in this response : {syncedHere}");
        if (missedByPrimary is not null)
        {
            Console.WriteLine($"synced records the primary query misses : {syncedMissed}");
        }
    }

    private static void DumpCoverageDiff(RawSearch primary, RawSearch? scoped, double? target, double? tolerance)
    {
        Console.WriteLine();
        Console.WriteLine("=== coverage: what the 20-record cap costs the primary query ===");

        if (primary.Failure is not null || scoped?.Failure is not null)
        {
            Console.WriteLine("(incomplete raw data, see above - not treated as a data point)");
            return;
        }

        if (scoped is null)
        {
            Console.WriteLine("(no album available: nothing to compare)");
            return;
        }

        var primaryIds = primary.Records.Select(r => r.Id).ToHashSet();
        var missed = scoped.Records.Where(r => IsSyncedRecord(r) && !primaryIds.Contains(r.Id)).ToList();
        var missedSameDuration = missed
            .Where(r => MatchesDuration(r.Duration, target, tolerance))
            .ToList();

        Console.WriteLine($"primary  : {primary.Records.Count} record(s), {primary.Records.Count(r => IsSyncedRecord(r))} synced");
        Console.WriteLine($"album    : {scoped.Records.Count} record(s), {scoped.Records.Count(r => IsSyncedRecord(r))} synced");
        Console.WriteLine($"synced only in the album-scoped response : {missed.Count}");

        if (missed.Count == 0)
        {
            Console.WriteLine("  (none)");
        }
        else
        {
            foreach (var record in missed)
            {
                var same = MatchesDuration(record.Duration, target, tolerance);
                Console.WriteLine($"  -> id={record.Id} \"{record.TrackName}\" {Describe(record.Duration)} album \"{record.AlbumName}\""
                                  + (same ? $"   <== synced AND within tolerance {DescribeTolerance(tolerance)}" : "   (synced, but outside the duration tolerance)"));
            }
        }

        Console.WriteLine($"...of which carry the playing track's duration : {missedSameDuration.Count}"
                          + (missedSameDuration.Count > 0 ? $" -> {string.Join(", ", missedSameDuration.Select(r => r.Id.ToString(CultureInfo.InvariantCulture)))}" : string.Empty));

        // The reproduction, stated from the data in this very run: the pre-T1 candidate universe was the
        // primary response alone, and here that response holds no synced record of the right length.
        var primaryHasSyncedMatch = primary.Records.Any(r => IsSyncedRecord(r) && MatchesDuration(r.Duration, target, tolerance));
        if (!primaryHasSyncedMatch && missedSameDuration.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("pre-fix shape : with the primary response as the only candidate universe this track had "
                              + $"{primary.Records.Count} record(s), 0 of them synced within tolerance, so the rule could not fire and the plain /api/get record was kept.");
            Console.WriteLine($"                asking again with album_name is what surfaces id={missedSameDuration[0].Id}; that second question is the fix.");
        }
    }

    // ---------------------------------------------------------------- section 5: the app's ranked list

    private static LyricsCandidate? PrintVersions(IReadOnlyList<LyricsCandidate> versions, double? target, double? tolerance)
    {
        Console.WriteLine($"count     : {versions.Count} candidate(s)");
        Console.WriteLine("ranking   : duration within max(2.0, duration*0.005) -> +1000; title exact +300; album exact +250; synced +200 (tie: lrclib's own order)");
        Console.WriteLine($"target    : {Describe(target)}   tolerance: {DescribeTolerance(tolerance)}");
        Console.WriteLine("source    : LyricsService.GetVersionsAsync -> LrclibClient.LookupSearchDetailedAsync (the same merged, ranked list the version menu shows)");
        Console.WriteLine("supplement: the album-scoped search is only spent when the primary leaves the track unsynced, so a track the first search resolves costs one /api/search");

        LyricsCandidate? pick = null;
        if (target is not null && tolerance is not null)
        {
            foreach (var candidate in versions)
            {
                if (candidate.HasSynced && !candidate.IsInstrumental
                    && candidate.DurationSeconds is { } d
                    && Math.Abs(d - target.Value) <= tolerance.Value)
                {
                    pick = candidate;
                    break;
                }
            }
        }

        Console.WriteLine();
        if (versions.Count == 0)
        {
            Console.WriteLine("  (none)");
        }

        for (var i = 0; i < versions.Count; i++)
        {
            var candidate = versions[i];
            var note = string.Empty;

            if (pick is not null && candidate.Id == pick.Id)
            {
                note = $"   <== RULE PICKS THIS: the first synced candidate in rank order, "
                       + $"|{candidate.DurationSeconds:0.###} - {target:0.###}| = {Math.Abs(candidate.DurationSeconds!.Value - target!.Value):0.###}s <= {tolerance:0.###}s";
            }
            else if (candidate.HasSynced && target is null)
            {
                note = "   (synced, but the playing track's duration is unknown, so the rule cannot apply)";
            }
            else if (candidate.HasSynced && candidate.DurationSeconds is null)
            {
                note = "   (synced, but lrclib reported no duration to compare)";
            }
            else if (candidate.HasSynced && MatchesDuration(candidate.DurationSeconds, target, tolerance))
            {
                note = "   (synced and within tolerance, but ranked below the pick)";
            }
            else if (candidate.HasSynced)
            {
                note = $"   (synced, but off by {Math.Abs(candidate.DurationSeconds!.Value - target!.Value):0.###}s > {DescribeTolerance(tolerance)})";
            }

            Console.WriteLine(
                $"  [{i,2}] {Delta(candidate.DurationSeconds, target)}  {CandidateKind(candidate),-13} id={candidate.Id,-9} {candidate.TrackName}  ({candidate.AlbumName}){note}");
        }

        Console.WriteLine();
        var seenIds = new HashSet<long>();
        var duplicates = new List<long>();
        foreach (var candidate in versions)
        {
            if (!seenIds.Add(candidate.Id))
            {
                duplicates.Add(candidate.Id);
            }
        }

        Console.WriteLine(duplicates.Count == 0
            ? "duplicates: (none) - every lrclib id appears exactly once in the merged list"
            : $"duplicates: {string.Join(", ", duplicates.Select(id => id.ToString(CultureInfo.InvariantCulture)))}");

        if (pick is null)
        {
            Console.WriteLine("rule pick: (none) - no synced candidate within tolerance in the list the app has");
        }
        else
        {
            Console.WriteLine($"rule pick: id={pick.Id} \"{pick.TrackName}\" {Describe(pick.DurationSeconds)} album \"{pick.AlbumName}\"");
        }

        return pick;
    }

    private static void DumpVersionsGap(RawSearch? scoped, IReadOnlyList<LyricsCandidate> versions, RawSearch primary, double? target, double? tolerance)
    {
        if (scoped is null || scoped.Failure is not null)
        {
            return;
        }

        var seen = versions.Select(c => c.Id).ToHashSet();
        var primaryIds = primary.Failure is null ? primary.Records.Select(r => r.Id).ToHashSet() : [];
        var missing = scoped.Records
            .Where(r => IsSyncedRecord(r) && !primaryIds.Contains(r.Id) && !seen.Contains(r.Id))
            .ToList();

        if (missing.Count == 0)
        {
            Console.WriteLine("coverage : the app's list already carries every synced record the album-scoped query found");
            return;
        }

        // Whether the absence of these records is a problem depends on why the album question was or
        // was not asked: the app only spends it when the primary search leaves the track unsynced.
        var primaryAlreadyAnswers = primary.Failure is null
            && primary.Records.Any(r => IsSyncedRecord(r) && MatchesDuration(r.Duration, target, tolerance));

        Console.WriteLine($"{missing.Count} synced record(s) the album-scoped query found are NOT in the app's list:");
        foreach (var record in missing)
        {
            Console.WriteLine($"  -> id={record.Id} \"{record.TrackName}\" {Describe(record.Duration)} album \"{record.AlbumName}\"");
        }

        if (primaryAlreadyAnswers)
        {
            Console.WriteLine("coverage : expected here - the primary search already holds a synced record the same length as the track,");
            Console.WriteLine("           so the app does not spend the album-scoped question at all (one /api/search saved).");
            Console.WriteLine("           This is only a gap when the primary search leaves the track unsynced - which is the case the rule exists for.");
        }
        else
        {
            Console.WriteLine("coverage GAP: the primary search holds no synced record of the right length, and the app's list is still missing");
            Console.WriteLine($"           the synced record(s) above - lrclib capped the primary query at {SearchCap} records. This is the failure shape the rule must fix.");
        }
    }

    // ---------------------------------------------------------------- section 6: verdict

    private static void DumpVerdict(LyricsResult? result, LyricsCandidate? rulePick, LyricsResult? exact, Func<long?, string> albumOf, bool servedFromSyncedCache)
    {
        switch (result)
        {
            case null:
                Console.WriteLine("GetLyricsAsync : (none) - lrclib has nothing usable for this track (a definitive miss "
                                  + "and a transient outage look identical here)");
                Console.WriteLine("synced         : no");
                break;
            case { IsSynced: true } synced:
                Console.WriteLine($"GetLyricsAsync : SYNCED lrclib id {synced.LrclibId?.ToString(CultureInfo.InvariantCulture) ?? "(none)"} "
                                  + $"\"{synced.TrackName}\" album {albumOf(synced.LrclibId)} {Describe(synced.DurationSeconds)} "
                                  + $"lines={synced.Lines.Count} ({CountTimed(synced.Lines)} timed)"
                                  + (servedFromSyncedCache ? "  [served from the existing cache entry]" : string.Empty));
                Console.WriteLine("synced         : yes");
                break;
            case { IsInstrumental: true } instrumental:
                Console.WriteLine($"GetLyricsAsync : instrumental, id {instrumental.LrclibId?.ToString(CultureInfo.InvariantCulture) ?? "(none)"} "
                                  + $"\"{instrumental.TrackName}\" album {albumOf(instrumental.LrclibId)} {Describe(instrumental.DurationSeconds)}");
                Console.WriteLine("synced         : no");
                break;
            default:
                Console.WriteLine($"GetLyricsAsync : NOT SYNCED (plain) id {result.LrclibId?.ToString(CultureInfo.InvariantCulture) ?? "(none)"} "
                                  + $"\"{result.TrackName}\" album {albumOf(result.LrclibId)} {Describe(result.DurationSeconds)} "
                                  + $"lines={result.Lines.Count} ({CountTimed(result.Lines)} timed)"
                                  + (servedFromSyncedCache ? "  [served from the existing cache entry]" : string.Empty));
                Console.WriteLine("synced         : no");
                break;
        }

        if (result is null)
        {
            Console.WriteLine("rule           : GetLyricsAsync answered nothing, so there was no pick to upgrade");
        }
        else if (rulePick is not null && result.LrclibId == rulePick.Id)
        {
            Console.WriteLine($"rule           : the app's answer IS the record the harness marked as the rule's pick (id {rulePick.Id})");
        }
        else if (exact is { IsSynced: true } exactSynced && result.LrclibId == exactSynced.LrclibId)
        {
            Console.WriteLine($"rule           : not needed - /api/get already answered SYNCED (id {exactSynced.LrclibId}), and the rule only ever "
                              + "upgrades a non-synced answer");
            Console.WriteLine($"                 (for reference, section 5's ranked list would pick id "
                              + $"{rulePick?.Id.ToString(CultureInfo.InvariantCulture) ?? "(none)"} if the exact lookup had been plain)");
        }
        else if (result is { IsSynced: true })
        {
            Console.WriteLine($"rule           : GetLyricsAsync answered SYNCED id {result.LrclibId?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}, "
                              + $"while the harness marked id {rulePick?.Id.ToString(CultureInfo.InvariantCulture) ?? "(none)"} from the ranked list - "
                              + "the annotation is a reading of that list, not the app's decision");
        }
        else if (rulePick is not null)
        {
            Console.WriteLine($"rule           : GetLyricsAsync came back NOT SYNCED even though the ranked list holds synced id {rulePick.Id} "
                              + "within tolerance - the rule did not fire");
        }
        else
        {
            Console.WriteLine("rule           : no synced candidate within tolerance was in the app's list, so the rule could not fire");
        }
    }

    private static string VerdictLine(LyricsResult? result, Func<long?, string> albumOf, bool servedFromCache)
    {
        var source = servedFromCache ? "existing cache entry" : "re-resolved against lrclib";

        return result switch
        {
            null => "(none) - lrclib returned nothing usable for this track (see the rule line above)",
            { IsSynced: true } synced =>
                $"SYNCED · lrclib id {synced.LrclibId?.ToString(CultureInfo.InvariantCulture) ?? "(none)"} · \"{synced.TrackName}\" · album {albumOf(synced.LrclibId)} · "
                + $"{Describe(synced.DurationSeconds)} · first timed line {FirstTimedLine(synced.Lines)} · ({source})",
            { IsInstrumental: true } instrumental =>
                $"INSTRUMENTAL (nothing to sync) · lrclib id {instrumental.LrclibId?.ToString(CultureInfo.InvariantCulture) ?? "(none)"} · \"{instrumental.TrackName}\" · album {albumOf(instrumental.LrclibId)} · {Describe(instrumental.DurationSeconds)} · ({source})",
            _ =>
                $"NOT SYNCED (plain lyrics only) · lrclib id {result.LrclibId?.ToString(CultureInfo.InvariantCulture) ?? "(none)"} · \"{result.TrackName}\" · album {albumOf(result.LrclibId)} · "
                + $"{Describe(result.DurationSeconds)} · {result.Lines.Count} line(s), {CountTimed(result.Lines)} timed · ({source})"
        };
    }

    // ---------------------------------------------------------------- helpers

    private static string? ExpectedNote(TrackQuery query)
    {
        if (Matches(query, "Gentle Giant", "For Nobody"))
        {
            return "synced record id 16359843 \"09 - For nobody\", 244.744127 s (the recorded live case)";
        }

        if (Matches(query, "Gentle Giant", "Winning"))
        {
            return "a SYNCED record the same length as the track - 14916772 \"Winning\" 257.52 s is the one the user had to pin by hand, "
                   + "and the lead's post-T1 live run picked 36334456 (257 s) automatically because it ranks first";
        }

        return null;
    }

    private static bool Matches(TrackQuery query, string artist, string title)
    {
        return string.Equals(
                   MetadataNormaliser.ForComparison(query.Artist),
                   MetadataNormaliser.ForComparison(artist),
                   StringComparison.Ordinal)
               && string.Equals(
                   MetadataNormaliser.ForComparison(MetadataNormaliser.CleanTitle(query.Title, query.Artist)),
                   MetadataNormaliser.ForComparison(MetadataNormaliser.CleanTitle(title)),
                   StringComparison.Ordinal);
    }

    private static string BuildSearchUrl(string artist, string track, string? album)
    {
        var url = new StringBuilder(BaseUrl).Append("/api/search");
        var first = true;

        AppendQuery(url, ref first, "track_name", track);
        AppendQuery(url, ref first, "artist_name", artist);
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

    private static async Task PauseAsync()
    {
        await Task.Delay(Pace).ConfigureAwait(false);
    }

    private static bool IsSyncedRecord(LrclibGetResponse record)
    {
        return record.Instrumental != true && !string.IsNullOrWhiteSpace(record.SyncedLyrics);
    }

    private static bool MatchesDuration(double? duration, double? target, double? tolerance)
    {
        return duration is { } d && target is { } t && tolerance is { } tol && Math.Abs(d - t) <= tol;
    }

    private static string Kind(LrclibGetResponse record)
    {
        return record.Instrumental == true ? "instrumental"
            : !string.IsNullOrWhiteSpace(record.SyncedLyrics) ? "SYNCED"
            : !string.IsNullOrWhiteSpace(record.PlainLyrics) ? "plain"
            : "no lyrics";
    }

    private static string CandidateKind(LyricsCandidate candidate)
    {
        return candidate.IsInstrumental ? "instrumental"
            : candidate.HasSynced ? "SYNCED"
            : candidate.HasPlain ? "plain"
            : "no lyrics";
    }

    private static string Delta(double? duration, double? target)
    {
        return duration is { } d && target is { } t ? $"{(d - t),+8:0.00}s" : "       ?";
    }

    private static string DescribeTolerance(double? tolerance)
    {
        return tolerance is { } value ? $"{value:0.###}s" : "(n/a: the playing track's duration is unknown)";
    }

    private static string DescribeResult(LyricsResult? result, Func<long?, string> albumOf)
    {
        if (result is null)
        {
            return "(none)";
        }

        var kind = result.IsInstrumental ? "instrumental" : result.IsSynced ? "SYNCED" : "PLAIN";
        return $"{kind} id={result.LrclibId?.ToString(CultureInfo.InvariantCulture) ?? "(none)"} "
               + $"\"{result.TrackName}\" by \"{result.ArtistName}\" album {albumOf(result.LrclibId)} {Describe(result.DurationSeconds)} "
               + $"lines={result.Lines.Count} ({CountTimed(result.Lines)} timed)";
    }

    private static string FirstTimedLine(IReadOnlyList<LyricLine> lines)
    {
        foreach (var line in lines)
        {
            if (line.Start > TimeSpan.Zero)
            {
                return $"[{line.Start.ToString(@"m\:ss\.ff", CultureInfo.InvariantCulture)}] \"{line.Text}\"";
            }
        }

        return lines.Count == 0 ? "(none)" : "(no timed lines: every line starts at 0:00)";
    }

    private static int CountTimed(IReadOnlyList<LyricLine> lines)
    {
        var count = 0;
        foreach (var line in lines)
        {
            if (line.Start > TimeSpan.Zero)
            {
                count++;
            }
        }

        return count;
    }

    private static double? ParseDuration(string? text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string Describe(double? seconds)
    {
        if (seconds is not > 0)
        {
            return "(unknown)";
        }

        var ts = TimeSpan.FromSeconds(seconds.Value);
        return $"{ts.TotalSeconds:0.###}s ({ts:m\\:ss})";
    }

    private sealed record TrackQuery(string Artist, string Title, string? Album, double? DurationSeconds)
    {
        public string? SourceApp { get; init; }

        public TimeSpan? Position { get; init; }

        public string? Status { get; init; }
    }

    private sealed record RawSearch(string Url, int StatusCode, IReadOnlyList<LrclibGetResponse> Records, string? Failure);
}
