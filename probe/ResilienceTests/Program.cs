using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Overlyric.Lyrics;

namespace Overlyric.ResilienceTests;

/// <summary>
/// Verifies the one behaviour that decides whether a temporary lrclib outage can permanently hide
/// a track's lyrics: a throttled or failed request must never be remembered as "no lyrics".
/// A local stub server is aimed at through the client's internal base-URL seam.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static async Task<int> Main()
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "overlyric-resilience-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDirectory);

        var cache = new LyricsCache(cacheDirectory);
        const string Artist = "Caravan";
        const string Track = "Can't Be Long Now";

        await using var server = new StubServer(503, "{}");
        using var client = new LrclibClient("Overlyric.Tests/1.0", server.BaseUrl);
        var service = new LyricsService(client, cache);

        // ---- 1. Server is throttled / broken: the answer is unknown, not "absent". ----
        var unavailable = await client.LookupExactAsync(Artist, Track, null, 300, CancellationToken.None);
        Check("503 is classified as Unavailable, not NotFound", unavailable.Status == LookupStatus.Unavailable);

        server.StatusCode = 429;
        var throttled = await client.LookupExactAsync(Artist, Track, null, 300, CancellationToken.None);
        Check("429 is classified as Unavailable", throttled.Status == LookupStatus.Unavailable);

        server.StatusCode = 503;
        var duringOutage = await service.GetLyricsAsync(Artist, Track, null, 300, CancellationToken.None);
        Check("a transient outage yields no lyrics", duringOutage is null);
        Check(
            "a transient outage is NOT tombstoned (the track stays retryable)",
            !cache.IsMiss(LyricsCache.MakeKey(Artist, Track, null)));

        // ---- 2. Server recovers: the very next attempt must find lyrics. ----
        server.StatusCode = 200;
        server.Body = """
            {"id":42,"trackName":"Can't Be Long Now","artistName":"Caravan","albumName":null,
             "duration":300,"instrumental":false,
             "plainLyrics":"line one\nline two",
             "syncedLyrics":"[00:01.00] line one\n[00:05.00] line two"}
            """;

        var recovered = await client.LookupExactAsync(Artist, Track, null, 300, CancellationToken.None);
        Check("200 with synced lyrics is classified as Found", recovered.Status == LookupStatus.Found);
        Check("recovered lyrics are synced and parsed", recovered.Lyrics is { IsSynced: true, Lines.Count: 2 });
        Check(
            "the first line is at 1 s",
            recovered.Lyrics is { } r && r.Lines[0].Start == TimeSpan.FromSeconds(1));

        var afterRecovery = await service.GetLyricsAsync(Artist, Track, null, 300, CancellationToken.None);
        Check("the service now returns lyrics for the same track", afterRecovery is { Lines.Count: 2 });

        // ---- 3. A true 404 is definitive and must be remembered. ----
        var otherCache = new LyricsCache(Path.Combine(cacheDirectory, "definitive"));
        var otherService = new LyricsService(client, otherCache);
        server.StatusCode = 404;
        server.Body = "{}";

        var notFound = await client.LookupExactAsync("Nobody", "Nothing", null, 100, CancellationToken.None);
        Check("404 is classified as NotFound", notFound.Status == LookupStatus.NotFound);

        var definitive = await otherService.GetLyricsAsync("Nobody", "Nothing", null, 100, CancellationToken.None);
        Check("a definitive miss yields no lyrics", definitive is null);
        Check(
            "a definitive miss IS tombstoned",
            otherCache.IsMiss(LyricsCache.MakeKey("Nobody", "Nothing", null)));

        // ---- 4. A 200 whose record carries no lyrics at all is also definitive. ----
        var bareCache = new LyricsCache(Path.Combine(cacheDirectory, "bare"));
        var bareService = new LyricsService(client, bareCache);
        server.StatusCode = 200;
        server.Body = """{"id":7,"trackName":"Bare","artistName":"Nobody","duration":90,"instrumental":false,"plainLyrics":null,"syncedLyrics":null}""";

        var bare = await bareService.GetLyricsAsync("Nobody", "Bare", null, 90, CancellationToken.None);
        Check("a lyric-less record yields no lyrics", bare is null);
        Check(
            "a lyric-less record IS tombstoned",
            bareCache.IsMiss(LyricsCache.MakeKey("Nobody", "Bare", null)));

        // ---- 5. Instrumental detection: a body of placeholders is a statement, not a lyric. ----
        var classificationCases = new (string Name, string Json, bool ExpectInstrumental, int ExpectLines)[]
        {
            (
                "lrclib flags the record instrumental",
                """{"id":11,"instrumental":true,"plainLyrics":null,"syncedLyrics":null}""",
                true, 0),
            (
                "plain body is just the word Instrumental",
                """{"id":12,"instrumental":false,"plainLyrics":"Instrumental","syncedLyrics":null}""",
                true, 0),
            (
                "synced body is a bracketed marker after a timestamp",
                """{"id":13,"instrumental":false,"plainLyrics":null,"syncedLyrics":"[00:01.00] [Instrumental]"}""",
                true, 0),
            (
                "synced body is a bare [Instrumental] with no timestamp",
                """{"id":14,"instrumental":false,"plainLyrics":null,"syncedLyrics":"[Instrumental]"}""",
                true, 0),
            (
                "body is only section markers and a musical note",
                """{"id":15,"instrumental":false,"plainLyrics":"[Intro]\n\u266a\n[Solo]\n[Outro]","syncedLyrics":null}""",
                true, 0),
            (
                "synced marker plus a real plain body keeps the words",
                """{"id":18,"instrumental":false,"plainLyrics":"A real sung line","syncedLyrics":"[Instrumental]"}""",
                false, 1),
            (
                "real lyrics containing an [Instrumental Break] line are NOT instrumental",
                """{"id":16,"instrumental":false,"plainLyrics":null,"syncedLyrics":"[00:01.00] First real line\n[00:05.00] [Instrumental Break]\n[00:20.00] Second real line"}""",
                false, 3),
            (
                "ordinary lyrics are unaffected",
                """{"id":17,"instrumental":false,"plainLyrics":"Just a normal line","syncedLyrics":null}""",
                false, 1)
        };

        foreach (var (name, json, expectInstrumental, expectLines) in classificationCases)
        {
            server.StatusCode = 200;
            server.Body = json;
            server.SearchBody = "[]";

            var outcome = await client.LookupExactAsync("Somebody", name, null, 200, CancellationToken.None);
            var lyrics = outcome.Lyrics;

            var ok = outcome.Status == LookupStatus.Found
                     && lyrics is not null
                     && lyrics.IsInstrumental == expectInstrumental
                     && lyrics.Lines.Count == expectLines;

            Check(
                $"classification: {name}"
                + (ok
                    ? $" -> instrumental={expectInstrumental}, lines={expectLines}"
                    : $" -> expected instrumental={expectInstrumental}/lines={expectLines}"
                      + $" but got status={outcome.Status}, instrumental={lyrics?.IsInstrumental}, lines={lyrics?.Lines.Count}"),
                ok);
        }

        // ---- 6. Title/album fallback, consulted only when lrclib has no record at all. ----
        Check(
            "title heuristic: '(Instrumental)' in the title",
            InstrumentalDetector.TitleIndicatesInstrumental("Song (Instrumental)", null));
        Check(
            "title heuristic: album called Instrumentals",
            InstrumentalDetector.TitleIndicatesInstrumental("Track 4", "Instrumentals"));
        Check(
            "title heuristic: ordinary title is not flagged",
            !InstrumentalDetector.TitleIndicatesInstrumental(
                "Free Hand (2012 Remaster)", "The Power and the Glory"));
        Check(
            "title heuristic: karaoke is NOT instrumental, the words are still wanted",
            !InstrumentalDetector.TitleIndicatesInstrumental("Song (Karaoke Version)", null));

        // Normalisation is what makes all of the above work, so pin its behaviour down.
        Check(
            "normalise drops timestamps but keeps bracketed words",
            InstrumentalDetector.Normalise("[00:01.00] [Instrumental]") == "instrumental");
        Check(
            "normalise drops metadata tags entirely",
            InstrumentalDetector.Normalise("[ar:Someone]") == "");
        Check(
            "normalise reduces a musical note to nothing",
            InstrumentalDetector.Normalise("\u266a") == "");
        Check(
            "normalise keeps real lyric words",
            InstrumentalDetector.Normalise("[00:01.00] Falling around me lay") == "falling around me lay");

        // ---- 7. Metadata cleaning: the fix for a real, reproducible mismatch. ----
        Check(
            "clean: strips a remaster suffix",
            MetadataNormaliser.CleanTitle("A Song For All Seasons (2019 Remaster)") == "A Song For All Seasons");
        Check(
            "clean: strips a remastered-and-expanded edition",
            MetadataNormaliser.CleanTitle("Song (2019 Remastered & Expanded Edition)") == "Song");
        Check(
            "clean: strips a dashed remaster suffix",
            MetadataNormaliser.CleanTitle("The Hermit - Remastered 2005") == "The Hermit");
        Check(
            "clean: strips stacked qualifiers",
            MetadataNormaliser.CleanTitle("Song (Remastered) (Deluxe Edition)") == "Song");
        Check(
            "clean: leaves a live version alone, it is a different performance",
            MetadataNormaliser.CleanTitle("Song (Live at the Capitol Theater - June 18, 1978)")
                == "Song (Live at the Capitol Theater - June 18, 1978)");
        Check(
            "clean: leaves an acoustic version alone",
            MetadataNormaliser.CleanTitle("Song (Acoustic)") == "Song (Acoustic)");
        Check(
            "clean: leaves an ordinary title alone",
            MetadataNormaliser.CleanTitle("Free Hand") == "Free Hand");
        Check(
            "compare: ignores case and punctuation",
            MetadataNormaliser.ForComparison("A Song For All Seasons!")
                == MetadataNormaliser.ForComparison("a song for all seasons"));

        // ---- 8. Ranking picks the record matching the performance, not merely the title. ----
        // Mirrors the real lrclib data for "A Song For All Seasons": the record whose name carries the
        // remaster suffix has no timings, while the cleanly named one is synced and the same length.
        server.StatusCode = 200;
        server.Body = "{}";
        server.Routes.Clear();
        server.SearchBody = """
            [
              {"id":17191936,"trackName":"A Song For All Seasons (2019 Remaster)","albumName":"A Song For All Seasons (2019 Remastered & Expanded Edition)","duration":660,"instrumental":false,"plainLyrics":"word\nword","syncedLyrics":null},
              {"id":34870508,"trackName":"A Song For All Seasons","albumName":"A Song For All Seasons (2019 Remastered & Expanded Edition)","duration":660,"instrumental":false,"plainLyrics":"word\nword","syncedLyrics":"[00:01.00] word\n[00:05.00] word"}
            ]
            """;

        var rankingService = new LyricsService(client, new LyricsCache(Path.Combine(cacheDirectory, "ranking")));

        var ranked = await rankingService.GetLyricsAsync(
            "Renaissance",
            "A Song For All Seasons (2019 Remaster)",
            "A Song For All Seasons (2019 Remastered & Expanded Edition)",
            660,
            CancellationToken.None);

        Check(
            "ranking: the synced record wins over an equally long untimed one",
            ranked is { IsSynced: true, LrclibId: 34870508 });

        // A synced sheet for a different performance must not beat untimed lyrics for the right one:
        // it would drift out of step with the music.
        server.SearchBody = """
            [
              {"id":501,"trackName":"A Song For All Seasons","albumName":"Live At The Capitol Theater","duration":743,"instrumental":false,"plainLyrics":null,"syncedLyrics":"[00:01.00] live words"},
              {"id":502,"trackName":"A Song For All Seasons","albumName":"A Song For All Seasons","duration":660,"instrumental":false,"plainLyrics":"right words","syncedLyrics":null}
            ]
            """;

        var durationService = new LyricsService(client, new LyricsCache(Path.Combine(cacheDirectory, "duration")));

        var durationRanked = await durationService.GetLyricsAsync(
            "Renaissance", "A Song For All Seasons", "A Song For All Seasons", 660, CancellationToken.None);

        Check(
            "ranking: a synced live take 83 s longer does not beat the right-length untimed record",
            durationRanked is { IsSynced: false, LrclibId: 502 });

        // ---- 9. A pinned version outranks automatic matching, and is remembered. ----
        server.Body = """{"id":100,"trackName":"Track","albumName":"Album","duration":660,"instrumental":false,"plainLyrics":"pinned words","syncedLyrics":null}""";
        server.Routes["/api/get/100"] = server.Body;
        server.SearchBody = "[]";

        var versionsPath = Path.Combine(cacheDirectory, "versions.json");
        var overrides = VersionOverrides.Load(versionsPath);
        var pinService = new LyricsService(client, new LyricsCache(Path.Combine(cacheDirectory, "pin")), overrides);

        var automatic = await pinService.GetLyricsAsync("Artist", "Track", "Album", 660, CancellationToken.None);
        Check("pinned: automatic matching is used when nothing is pinned", automatic is { LrclibId: 100 });

        pinService.PinVersion("Artist", "Track", "Album", 100);
        Check(
            "pinned: the choice is recorded",
            pinService.TryGetPinnedVersion("Artist", "Track", "Album", out var pinnedOut) && pinnedOut == 100);
        Check(
            "pinned: the choice survives a reload",
            VersionOverrides.Load(versionsPath)
                .TryGet(LyricsCache.MakeKey("Artist", "Track", "Album"), out var persisted) && persisted == 100);

        pinService.ClearVersion("Artist", "Track", "Album");
        Check(
            "pinned: clearing returns to automatic matching",
            !pinService.TryGetPinnedVersion("Artist", "Track", "Album", out _));

        // ---- 10. Movement listings: some players report a multi-part work as one huge title. ----
        const string MovementTitle =
            "Song of Scheherazade / I. Fanfare - II. / The Betrayal / III. The Sultan / IV. Love Theme / "
            + "V. The Young Prince and Princess as Told by Scheharazade / VI. Festival Preparations / "
            + "VII. Fugue for the Sultan";

        Check(
            "clean: cuts a movement listing down to the work it belongs to",
            MetadataNormaliser.CleanTitle(MovementTitle) == "Song of Scheherazade");
        Check(
            "clean: leaves a slash that is part of the name alone",
            MetadataNormaliser.CleanTitle("Love / Hate") == "Love / Hate");
        Check(
            "clean: a leading word that merely looks roman is not a movement number",
            MetadataNormaliser.CleanTitle("I Will Always Love You / The Remix") == "I Will Always Love You / The Remix");

        // Real lrclib candidates for this track: a misspelt-but-close one and the correctly named one.
        // The name match is what separates them once durations are both within tolerance.
        server.Body = "{}";
        server.Routes.Clear();
        server.SearchBody = """
            [
              {"id":17452847,"trackName":"Song of Sheherazade","albumName":"Scheherazade and Other Stories","duration":1484,"instrumental":false,"plainLyrics":"w","syncedLyrics":"[00:01.00] a"},
              {"id":34551752,"trackName":"song of scheherazade","albumName":"scheherazade and other stories","duration":1476,"instrumental":false,"plainLyrics":"w","syncedLyrics":"[00:01.00] b"}
            ]
            """;

        var movementService = new LyricsService(client, new LyricsCache(Path.Combine(cacheDirectory, "movement")));

        var movementResult = await movementService.GetLyricsAsync(
            "Renaissance",
            MovementTitle,
            "Scheherazade and Other Stories (Remastered)",
            1480.35,
            CancellationToken.None);

        Check(
            "ranking: a movement-listing title resolves to the correctly named synced record",
            movementResult is { IsSynced: true, LrclibId: 34551752 });

        // A remastered album suffix must not stop the album bonus matching lrclib's plain album name.
        Check(
            "ranking: a remastered album still counts as an album match",
            movementResult?.LrclibId == 34551752);

        // ---- 11. The synced-same-duration rule: a timed sheet for this performance beats plain lyrics. ----
        server.StatusCode = 200;
        server.Routes.Clear();
        server.Body = "{}";
        server.AlbumSearchBody = null;

        // The real "For Nobody" shape: the untimed record matches the playing track almost exactly
        // (246 s against 246.4 s), while the synced sheet runs 244.744127 s - 1.66 s short, inside the
        // max(2.0, 0.5%) window - and sits on another album, so it ranks below the plain record.
        server.SearchBody = """
            [
              {"id":3463399,"trackName":"For Nobody","albumName":"For Nobody","duration":246,"instrumental":false,"plainLyrics":"word\nword","syncedLyrics":null},
              {"id":16359843,"trackName":"09 - For nobody","albumName":"Another Compilation","duration":244.744127,"instrumental":false,"plainLyrics":null,"syncedLyrics":"[00:01.00] word"}
            ]
            """;

        var sameDurationService = new LyricsService(
            client,
            new LyricsCache(Path.Combine(cacheDirectory, "synced-same-duration")));

        var sameDuration = await sameDurationService.GetLyricsAsync(
            "Artist", "For Nobody", "For Nobody", 246.4, CancellationToken.None);

        Check(
            "synced rule: the 244.744 s synced sheet beats the 246 s plain record at target 246.4",
            sameDuration is { IsSynced: true, LrclibId: 16359843 });

        // Same lengths and the same title, but the synced sheet also matches the album, so it ranks
        // first outright and the rule has nothing to repair.
        server.SearchBody = """
            [
              {"id":3463399,"trackName":"For Nobody","albumName":"For Nobody","duration":246,"instrumental":false,"plainLyrics":"word\nword","syncedLyrics":null},
              {"id":16359843,"trackName":"For Nobody","albumName":"For Nobody","duration":244.744127,"instrumental":false,"plainLyrics":null,"syncedLyrics":"[00:01.00] word"}
            ]
            """;

        var sameAlbumService = new LyricsService(
            client,
            new LyricsCache(Path.Combine(cacheDirectory, "synced-same-album")));

        var sameAlbum = await sameAlbumService.GetLyricsAsync(
            "Artist", "For Nobody", "For Nobody", 246.4, CancellationToken.None);

        Check(
            "synced rule: the synced record wins outright when it also ranks first",
            sameAlbum is { IsSynced: true, LrclibId: 16359843 });

        // A synced sheet already inside the window in the primary answer is enough on its own, so the
        // album-scoped request must not be spent.
        server.SearchBody = """
            [
              {"id":4861,"trackName":"Winning","albumName":"Winning","duration":256,"instrumental":false,"plainLyrics":"word\nword","syncedLyrics":null},
              {"id":4862,"trackName":"Winning","albumName":"Winning","duration":257.52,"instrumental":false,"plainLyrics":null,"syncedLyrics":"[00:01.00] word"}
            ]
            """;
        server.ResetRequests();

        var primaryOnlyService = new LyricsService(
            client,
            new LyricsCache(Path.Combine(cacheDirectory, "synced-primary-only")));

        var primaryOnly = await primaryOnlyService.GetLyricsAsync(
            "Artist", "Winning", "Winning", 256, CancellationToken.None);

        Check(
            "synced rule: a synced in-window record in the primary answer wins",
            primaryOnly is { IsSynced: true, LrclibId: 4862 });
        Check(
            "synced rule: a primary answer that already has a synced match costs no album-scoped search",
            AlbumScopedSearchCount(server) == 0);

        // ---- 12. Album-scoped supplement: the twenty-record cap can hide the synced sheet. ----
        server.Body = "{}";
        server.SearchBody = """
            [
              {"id":7001,"trackName":"Hidden Sheet","albumName":"Hidden Sheet","duration":300,"instrumental":false,"plainLyrics":"a\nb","syncedLyrics":null},
              {"id":7002,"trackName":"Hidden Sheet","albumName":"Hidden Sheet","duration":300.5,"instrumental":false,"plainLyrics":"a\nb","syncedLyrics":null}
            ]
            """;
        server.AlbumSearchBody = """
            [
              {"id":7002,"trackName":"Hidden Sheet","albumName":"Hidden Sheet","duration":300.5,"instrumental":false,"plainLyrics":"a\nb","syncedLyrics":null},
              {"id":7003,"trackName":"Hidden Sheet","albumName":"Hidden Sheet","duration":300.4,"instrumental":false,"plainLyrics":null,"syncedLyrics":"[00:01.00] a"}
            ]
            """;
        server.ResetRequests();

        var supplementService = new LyricsService(
            client,
            new LyricsCache(Path.Combine(cacheDirectory, "album-supplement")));

        var supplemented = await supplementService.GetLyricsAsync(
            "Artist", "Hidden Sheet", "Hidden Sheet", 300, CancellationToken.None);

        Check(
            "album supplement: the synced record only the album-scoped search can see wins",
            supplemented is { IsSynced: true, LrclibId: 7003 });
        Check(
            "album supplement: exactly one album-scoped search is made",
            AlbumScopedSearchCount(server) == 1);

        // The version menu is fed from the same merged, ranked union: the record both searches return
        // must appear exactly once, and the record only the supplement found must be in there.
        var supplementVersions = await supplementService.GetVersionsAsync(
            "Artist", "Hidden Sheet", "Hidden Sheet", 300, CancellationToken.None);

        Check(
            "album supplement: the merged menu dedupes the overlapping record",
            supplementVersions.Count == 3
            && supplementVersions.Count(c => c.Id == 7002) == 1
            && supplementVersions.Count(c => c.Id == 7003) == 1);

        // ---- 13. No synced candidate matches the length: the plain result stands. ----
        server.Body = "{}";
        server.SearchBody = """
            [
              {"id":8001,"trackName":"Wrong Length","albumName":"Wrong Length","duration":300,"instrumental":false,"plainLyrics":"a\nb","syncedLyrics":null},
              {"id":8002,"trackName":"Wrong Length","albumName":"Wrong Length","duration":340,"instrumental":false,"plainLyrics":null,"syncedLyrics":"[00:01.00] a"}
            ]
            """;
        server.AlbumSearchBody = """
            [
              {"id":8003,"trackName":"Wrong Length","albumName":"Wrong Length","duration":335,"instrumental":false,"plainLyrics":null,"syncedLyrics":"[00:01.00] b"}
            ]
            """;

        var wrongLengthService = new LyricsService(
            client,
            new LyricsCache(Path.Combine(cacheDirectory, "wrong-length")));

        var wrongLength = await wrongLengthService.GetLyricsAsync(
            "Artist", "Wrong Length", "Wrong Length", 300, CancellationToken.None);

        Check(
            "synced rule: no synced candidate matches the length, so the plain result is kept",
            wrongLength is { IsSynced: false, LrclibId: 8001 });

        // ---- 14. Unknown duration: the rule cannot apply, and the album is not searched. ----
        server.Body = "{}";
        server.SearchBody = """
            [
              {"id":8101,"trackName":"Unknown Length","albumName":"Unknown Length","duration":300,"instrumental":false,"plainLyrics":"a\nb","syncedLyrics":null},
              {"id":8102,"trackName":"Unknown Length (Live)","albumName":"Some Live Album","duration":340,"instrumental":false,"plainLyrics":null,"syncedLyrics":"[00:01.00] a"}
            ]
            """;
        server.AlbumSearchBody = """
            [
              {"id":8103,"trackName":"Unknown Length","albumName":"Unknown Length","duration":300.2,"instrumental":false,"plainLyrics":null,"syncedLyrics":"[00:01.00] b"}
            ]
            """;
        server.ResetRequests();

        var unknownDurationService = new LyricsService(
            client,
            new LyricsCache(Path.Combine(cacheDirectory, "unknown-duration")));

        var unknownDuration = await unknownDurationService.GetLyricsAsync(
            "Artist", "Unknown Length", "Unknown Length", null, CancellationToken.None);

        Check(
            "synced rule: with no known duration the ranked plain result is kept",
            unknownDuration is { IsSynced: false, LrclibId: 8101 });
        Check(
            "synced rule: an unknown duration makes no album-scoped search",
            AlbumScopedSearchCount(server) == 0);

        // ---- 15. Entries and tombstones written by an older matcher are re-resolved once. ----
        server.Routes.Clear();
        server.Body = "{}";
        server.AlbumSearchBody = null;
        server.SearchBody = """
            [{"id":901,"trackName":"Legacy Entry","albumName":"Old Album","duration":300,"instrumental":false,"plainLyrics":"new words","syncedLyrics":null}]
            """;

        var legacyCacheDirectory = Path.Combine(cacheDirectory, "legacy");
        var legacyCache = new LyricsCache(legacyCacheDirectory);
        var legacyKey = LyricsCache.MakeKey("Artist", "Legacy Entry", "Old Album");

        // Exactly the shape the cache wrote before the stamp existed: same envelope, no matchVersion.
        File.WriteAllText(
            Path.Combine(legacyCacheDirectory, CacheHash(legacyKey) + ".json"),
            LegacyEntryJson(legacyKey, 900));

        server.ResetRequests();
        var legacyService = new LyricsService(client, legacyCache);

        var legacyResolved = await legacyService.GetLyricsAsync(
            "Artist", "Legacy Entry", "Old Album", 300, CancellationToken.None);

        Check(
            "matcher version: an entry with no matchVersion is ignored and re-resolved",
            legacyResolved is { LrclibId: 901 });
        Check(
            "matcher version: the stale entry is rewritten with the current stamp",
            legacyCache.TryGet(legacyKey, out var rewritten) && rewritten is { LrclibId: 901 });

        server.ResetRequests();
        var legacyCachedAgain = await legacyService.GetLyricsAsync(
            "Artist", "Legacy Entry", "Old Album", 300, CancellationToken.None);

        Check(
            "matcher version: the rewritten entry is served from cache with no request",
            legacyCachedAgain is { LrclibId: 901 } && server.Requests.Count == 0);

        var legacyMissKey = LyricsCache.MakeKey("Artist", "Legacy Tombstone", "Old Album");
        File.WriteAllText(
            Path.Combine(legacyCacheDirectory, CacheHash(legacyMissKey) + ".miss.json"),
            "{\"key\":" + JsonSerializer.Serialize(legacyMissKey) + ",\"result\":null}");

        server.SearchBody = """
            [{"id":902,"trackName":"Legacy Tombstone","albumName":"Old Album","duration":300,"instrumental":false,"plainLyrics":"found again","syncedLyrics":null}]
            """;

        var legacyMissResolved = await legacyService.GetLyricsAsync(
            "Artist", "Legacy Tombstone", "Old Album", 300, CancellationToken.None);

        Check(
            "matcher version: a tombstone with no matchVersion does not short-circuit the lookup",
            legacyMissResolved is { LrclibId: 902 });
        Check(
            "matcher version: the stale tombstone is cleared once the track resolves",
            !legacyCache.IsMiss(legacyMissKey) && legacyCache.TryGet(legacyMissKey, out _));

        // Controls: the same hand-written files with the current stamp ARE trusted, so the checks
        // above are about the stamp and not about hand-written JSON being unreadable for some other
        // reason.
        var stampedKey = LyricsCache.MakeKey("Artist", "Stamped Entry", "Old Album");
        File.WriteAllText(
            Path.Combine(legacyCacheDirectory, CacheHash(stampedKey) + ".json"),
            LegacyEntryJson(stampedKey, 905)
                .Replace("\"result\"", "\"matchVersion\":1,\"result\"", StringComparison.Ordinal));
        server.ResetRequests();
        var stamped = await new LyricsService(client, legacyCache).GetLyricsAsync(
            "Artist", "Stamped Entry", "Old Album", 300, CancellationToken.None);

        Check(
            "matcher version control: a stamped entry is served from cache with no request",
            stamped is { LrclibId: 905 } && server.Requests.Count == 0);

        var stampedMissKey = LyricsCache.MakeKey("Artist", "Stamped Tombstone", "Old Album");
        File.WriteAllText(
            Path.Combine(legacyCacheDirectory, CacheHash(stampedMissKey) + ".miss.json"),
            "{\"key\":" + JsonSerializer.Serialize(stampedMissKey) + ",\"matchVersion\":1,\"result\":null}");

        Check("matcher version control: a stamped tombstone is respected", legacyCache.IsMiss(stampedMissKey));
        server.ResetRequests();
        var stampedMiss = await legacyService.GetLyricsAsync(
            "Artist", "Stamped Tombstone", "Old Album", 300, CancellationToken.None);

        Check(
            "matcher version control: a stamped tombstone short-circuits the lookup",
            stampedMiss is null && server.Requests.Count == 0);

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");

        try
        {
            Directory.Delete(cacheDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }

        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool passed)
    {
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} | {name}");

        if (!passed)
        {
            _failures++;
        }
    }

    /// <summary>Search requests that carried an album, i.e. the album-scoped supplement.</summary>
    private static int AlbumScopedSearchCount(StubServer server)
    {
        return server.Requests.Count(r =>
            r.Contains("/api/search", StringComparison.OrdinalIgnoreCase)
            && r.Contains("album_name=", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Cache file name for a key: the lowercase hex SHA-256 of the key plus <c>.json</c>.</summary>
    private static string CacheHash(string key)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>
    /// The on-disk shape the cache used before the matcher-version stamp existed: the same envelope
    /// and cached result, and no <c>matchVersion</c> at all. A check writes this by hand so that the
    /// migration path is exercised from the file the old build would really have left behind.
    /// </summary>
    private static string LegacyEntryJson(string key, long lrclibId)
    {
        return "{\"key\":" + JsonSerializer.Serialize(key)
            + ",\"result\":{\"isSynced\":false,\"isInstrumental\":false,\"plainLyrics\":\"old words\","
            + "\"lrclibId\":" + lrclibId.ToString(CultureInfo.InvariantCulture)
            + ",\"trackName\":\"Legacy Entry\",\"artistName\":\"Artist\",\"durationSeconds\":300,"
            + "\"lines\":[{\"startTicks\":0,\"text\":\"old words\"}]}}";
    }
}

/// <summary>A bare TCP HTTP responder: enough to return a chosen status code and body.</summary>
internal sealed class StubServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public StubServer(int statusCode, string body)
    {
        StatusCode = statusCode;
        Body = body;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{port}";

        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int StatusCode { get; set; }

    /// <summary>Body returned for <c>/api/get</c>.</summary>
    public string Body { get; set; }

    /// <summary>Body returned for <c>/api/search</c>, which must be a JSON array.</summary>
    public string SearchBody { get; set; } = "[]";

    /// <summary>
    /// Body returned for an <c>/api/search</c> that carries <c>album_name</c> - the album-scoped
    /// supplement. Left null, every search keeps answering <see cref="SearchBody"/>, which is the
    /// behaviour the older checks rely on.
    /// </summary>
    public string? AlbumSearchBody { get; set; }

    /// <summary>Per-path overrides, matched by substring, for things like <c>/api/get/123</c>.</summary>
    public Dictionary<string, string> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _requests = [];
    private readonly object _requestGate = new();

    /// <summary>Raw request text of every request served so far, oldest first.</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requestGate)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>Forgets the recorded requests, so a check can count only the requests it caused.</summary>
    public void ResetRequests()
    {
        lock (_requestGate)
        {
            _requests.Clear();
        }
    }

    public string BaseUrl { get; }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient connection;

            try
            {
                connection = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = RespondAsync(connection);
        }
    }

    private string ResolvePayload(string requestLine)
    {
        foreach (var route in Routes)
        {
            if (requestLine.Contains(route.Key, StringComparison.OrdinalIgnoreCase))
            {
                return route.Value;
            }
        }

        if (requestLine.Contains("/api/search", StringComparison.OrdinalIgnoreCase))
        {
            // The album-scoped supplement is the same path with album_name on the query string; it
            // answers from its own body when one is set, so a check can tell the two searches apart.
            return AlbumSearchBody is not null
                   && requestLine.Contains("album_name=", StringComparison.OrdinalIgnoreCase)
                ? AlbumSearchBody
                : SearchBody;
        }

        return Body;
    }

    private async Task RespondAsync(TcpClient connection)
    {
        try
        {
            using (connection)
            using (var stream = connection.GetStream())
            {
                var requestBuffer = new byte[8192];
                var bytesRead = await stream.ReadAsync(requestBuffer, _cts.Token);
                var requestLine = Encoding.ASCII.GetString(requestBuffer, 0, bytesRead);

                lock (_requestGate)
                {
                    _requests.Add(requestLine);
                }

                var payload = ResolvePayload(requestLine);

                var body = Encoding.UTF8.GetBytes(payload);
                var reason = StatusCode switch
                {
                    404 => "Not Found",
                    429 => "Too Many Requests",
                    503 => "Service Unavailable",
                    _ => "OK"
                };

                var header =
                    $"HTTP/1.1 {StatusCode} {reason}\r\n" +
                    "Content-Type: application/json\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Connection: close\r\n\r\n";

                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _cts.Token);
                await stream.WriteAsync(body, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
        }
        catch (Exception)
        {
            // The client may hang up early; irrelevant to the test.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();

        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
            // Shutting down.
        }

        _cts.Dispose();
    }
}
