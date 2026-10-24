using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Overlyric.Lyrics;

var total = 0;
var failures = 0;

Console.WriteLine("Overlyric.Lyrics probe");
Console.WriteLine();

// ---------------------------------------------------------------------------------------------
Console.WriteLine("== 1. LrcParser ==");

const string multiTimestampLrc =
    "[ar:Radiohead]\n" +
    "[ti:Test]\n" +
    "[00:12.00][01:30.00]Chorus\n" +
    "[00:15.50]Verse\n" +
    "[0:05.25]Intro\n" +
    "[00:21]NoFrac\n" +
    "[00:22.5]Half\n" +
    "[00:23.125]Millis\n" +
    "[00:30.00]   \n" +
    "[length:03:12]\n";

var parsed = LrcParser.Parse(multiTimestampLrc);

var expected = new (double Start, string Text)[]
{
    (5.25, "Intro"),
    (12, "Chorus"),
    (15.5, "Verse"),
    (21, "NoFrac"),
    (22.5, "Half"),
    (23.125, "Millis"),
    (90, "Chorus"),
};

var layoutOk = parsed.Count == expected.Length;
for (var i = 0; layoutOk && i < expected.Length; i++)
{
    layoutOk = parsed[i].Text == expected[i].Text
        && parsed[i].Start == TimeSpan.FromSeconds(expected[i].Start);
}

Check(
    "multi-timestamp LRC expands to one line per timestamp, sorted, metadata/blank lines skipped",
    layoutOk,
    $"count={parsed.Count} -> {Describe(parsed)}");

var bomCrlf = LrcParser.Parse("\uFEFF[00:01.00]A\r\n[00:02.00]B\r\n");
Check(
    "UTF-8 BOM and CRLF line endings",
    bomCrlf.Count == 2 && bomCrlf[0].Text == "A" && bomCrlf[1].Text == "B"
        && bomCrlf[1].Start == TimeSpan.FromSeconds(2),
    Describe(bomCrlf));

var leadingSpace = LrcParser.Parse("[00:19.16] When you were here before");
Check(
    "text is trimmed after the timestamp",
    leadingSpace.Count == 1 && leadingSpace[0].Text == "When you were here before",
    Describe(leadingSpace));

// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("== 2. [offset:] sign convention (timestamp = raw - offset) ==");

var negativeOffset = LrcParser.Parse("[offset:-500]\n[00:10.00]A");
Check(
    "[offset:-500] shifts timestamps LATER by 500 ms",
    negativeOffset.Count == 1 && negativeOffset[0].Start == TimeSpan.FromMilliseconds(10500),
    Describe(negativeOffset));

var positiveOffset = LrcParser.Parse("[offset:+500]\n[00:10.00]A");
Check(
    "[offset:+500] shifts timestamps EARLIER by 500 ms",
    positiveOffset.Count == 1 && positiveOffset[0].Start == TimeSpan.FromMilliseconds(9500),
    Describe(positiveOffset));

var trailingOffset = LrcParser.Parse("[00:10.00]A\n[offset:-250]");
Check(
    "an offset tag after the lyric lines still applies",
    trailingOffset.Count == 1 && trailingOffset[0].Start == TimeSpan.FromMilliseconds(10250),
    Describe(trailingOffset));

// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("== 3. malformed input never throws ==");

var malformedThrew = false;
IReadOnlyList<LyricLine> malformed = Array.Empty<LyricLine>();
try
{
    malformed = LrcParser.Parse("not a lyric\n[99:99.99]bad\n[00:01.00]good");
}
catch (Exception ex)
{
    malformedThrew = true;
    Console.WriteLine(ex);
}

Check(
    "malformed LRC yields at least the 'good' line without throwing",
    !malformedThrew && malformed.Any(l => l.Text == "good" && l.Start == TimeSpan.FromSeconds(1)),
    Describe(malformed));

string[] junk =
[
    string.Empty,
    "   ",
    "[",
    "[]",
    "[00:",
    "[00:01.00",
    "[]]x",
    "[abc]",
    "[12:60.00]sixty",
    "[offset:abc]\n[00:01.00]ok",
    "\uFEFF",
    "[99999:00.00]huge",
];

var junkThrew = false;
foreach (var input in junk)
{
    try
    {
        LrcParser.Parse(input);
    }
    catch
    {
        junkThrew = true;
    }
}

Check("assorted junk input never throws", !junkThrew);

// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("== 4. LyricsCache.MakeKey ==");

var keyA = LyricsCache.MakeKey("  Radiohead ", "CREEP", "Pablo   Honey");
var keyB = LyricsCache.MakeKey("radiohead", "creep", "pablo honey");
var keyC = LyricsCache.MakeKey("RADIOHEAD", "Creep", "  pablo\thoney ");

Check("stable across case, padding and internal whitespace", keyA == keyB && keyB == keyC, keyB.Replace("\n", " | "));
Check(
    "null, empty and whitespace-only album produce the same key",
    LyricsCache.MakeKey("a", "t", null) == LyricsCache.MakeKey("A", "T", string.Empty)
        && LyricsCache.MakeKey("a", "t", null) == LyricsCache.MakeKey("a", "t", "   "));
Check("different albums produce different keys", LyricsCache.MakeKey("a", "t", "x") != LyricsCache.MakeKey("a", "t", "y"));

// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("== 6. LyricsCache ==");

var runRoot = Path.Combine(Path.GetTempPath(), "overlyric-lyrics-probe", Guid.NewGuid().ToString("N"));
var cacheDirectory = Path.Combine(runRoot, "cache");
var cache = new LyricsCache(cacheDirectory);
var key = LyricsCache.MakeKey("Radiohead", "Creep", "Pablo Honey");
var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

var sample = new LyricsResult
{
    Lines =
    [
        new LyricLine(TimeSpan.FromSeconds(12.345), "Hello"),
        new LyricLine(TimeSpan.FromSeconds(20), "World"),
    ],
    IsSynced = true,
    IsInstrumental = false,
    PlainLyrics = "Hello\nWorld",
    LrclibId = 496,
    TrackName = "Creep",
    ArtistName = "Radiohead",
    DurationSeconds = 239,
};

cache.Set(key, sample);
var hit = cache.TryGet(key, out var cached);
Check(
    "Set then TryGet round-trips every field",
    hit && cached is not null && Equivalent(cached, sample),
    cached is null ? "null" : Describe(cached.Lines));

Check(
    "entry file is named <lowercase sha256>.json and stores the key",
    File.Exists(Path.Combine(cacheDirectory, hash + ".json")),
    hash + ".json");

var otherKey = LyricsCache.MakeKey("Radiohead", "Creep", "Other Album");
var otherHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(otherKey)));
File.Copy(
    Path.Combine(cacheDirectory, hash + ".json"),
    Path.Combine(cacheDirectory, otherHash + ".json"),
    overwrite: true);
Check("an entry whose stored key does not match is rejected", !cache.TryGet(otherKey, out _));

cache.SetMiss(key);
Check("SetMiss then IsMiss, and TryGet still misses", cache.IsMiss(key) && !cache.TryGet(key, out _));

cache.Set(key, sample);
Check("Set clears the tombstone", !cache.IsMiss(key) && cache.TryGet(key, out _));

// Every entry and tombstone records the matcher version that produced it, so a file left behind by
// older matching rules reads as a miss and is resolved once more instead of hiding the newer answer.
var legacyKey = LyricsCache.MakeKey("Radiohead", "Creep", "Legacy Album");
var legacyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(legacyKey)));
File.WriteAllText(
    Path.Combine(cacheDirectory, legacyHash + ".json"),
    "{\"key\":" + JsonSerializer.Serialize(legacyKey)
        + ",\"result\":{\"isSynced\":false,\"isInstrumental\":false,\"plainLyrics\":\"old\","
        + "\"lrclibId\":900,\"trackName\":\"Creep\",\"artistName\":\"Radiohead\",\"durationSeconds\":239,"
        + "\"lines\":[{\"startTicks\":0,\"text\":\"old\"}]}}");
Check("an entry with no matchVersion is rejected as stale", !cache.TryGet(legacyKey, out _));
Check(
    "a freshly written entry carries the matchVersion stamp",
    File.ReadAllText(Path.Combine(cacheDirectory, hash + ".json")).Contains("\"matchVersion\"", StringComparison.Ordinal));

var legacyMissKey = LyricsCache.MakeKey("Radiohead", "Creep", "Legacy Tombstone");
var legacyMissHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(legacyMissKey)));
File.WriteAllText(
    Path.Combine(cacheDirectory, legacyMissHash + ".miss.json"),
    "{\"key\":" + JsonSerializer.Serialize(legacyMissKey) + ",\"result\":null}");
Check("a tombstone with no matchVersion is not respected", !cache.IsMiss(legacyMissKey));

var unusable = Path.Combine(Path.GetTempPath(), "overlyric|invalid*dir");
Check(
    "cache never throws on an unusable directory",
    !Throws(() =>
    {
        var broken = new LyricsCache(unusable);
        broken.Set("k", sample);
        broken.TryGet("k", out _);
        broken.IsMiss("k");
        broken.SetMiss("k");
    }));

// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("== 7. LyricsService (offline) ==");

var serviceCache = new LyricsCache(Path.Combine(runRoot, "service-cache"));
using var liveClient = new LrclibClient("Overlyric/0.1.0 (+https://github.com/overlyric)");
var service = new LyricsService(liveClient, serviceCache);
var serviceKey = LyricsCache.MakeKey("Radiohead", "Creep", "Pablo Honey");

serviceCache.Set(serviceKey, sample);
var fromCache = await service.GetLyricsAsync("Radiohead", "Creep", "Pablo Honey", 239, CancellationToken.None);
Check(
    "a cache hit is returned immediately",
    fromCache is not null && Equivalent(fromCache, sample));

serviceCache.SetMiss(serviceKey);
var tombstoned = await service.GetLyricsAsync("Radiohead", "Creep", "Pablo Honey", 239, CancellationToken.None);
Check(
    "a tombstone short-circuits the network even for a track lrclib does have",
    tombstoned is null);

// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("== 5. live lrclib.net round-trip ==");

LyricsResult? creep = null;
try
{
    creep = await FetchAsync(() => liveClient.GetExactAsync("Radiohead", "Creep", "Pablo Honey", 239, CancellationToken.None));
}
catch (Exception ex)
{
    Console.WriteLine(ex);
}

var firstStart = creep is { Lines.Count: > 0 } ? creep.Lines[0].Start.TotalSeconds : -1;
Check(
    "GetExactAsync(Radiohead, Creep, Pablo Honey, 239) returns synced lyrics starting ~19 s",
    creep is not null && creep.IsSynced && creep.Lines.Count > 0
        && firstStart is >= 18 and <= 20
        && creep.Lines[0].Text == "When you were here before",
    creep is null
        ? "null"
        : string.Format(
            CultureInfo.InvariantCulture,
            "id={0} lines={1} first={2:0.###}s text='{3}' plain={4} chars",
            creep.LrclibId,
            creep.Lines.Count,
            firstStart,
            creep.Lines[0].Text,
            creep.PlainLyrics?.Length ?? -1));

Check(
    "GetExactAsync also exposes plainLyrics and metadata",
    creep is { PlainLyrics.Length: > 0, TrackName: "Creep", ArtistName: "Radiohead" },
    creep is null ? "null" : $"track={creep.TrackName} artist={creep.ArtistName} duration={creep.DurationSeconds}");

if (creep is not null)
{
    // The real payload (37 lines, hundredth-of-a-second timestamps) is exactly the shape that
    // exposes lossy TimeSpan <-> double conversions in a JSON cache.
    var liveCache = new LyricsCache(Path.Combine(runRoot, "live-cache"));
    var liveKey = LyricsCache.MakeKey("Radiohead", "Creep", "live-round-trip");
    liveCache.Set(liveKey, creep);
    var reread = liveCache.TryGet(liveKey, out var rounded) ? rounded : null;
    Check(
        "the live 37-line result survives the JSON cache round-trip exactly",
        reread is not null && Equivalent(reread, creep),
        reread is null
            ? "nothing read back"
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0} lines, first={1:0.#######}s, last={2:0.#######}s",
                reread.Lines.Count,
                reread.Lines[0].Start.TotalSeconds,
                reread.Lines[^1].Start.TotalSeconds));
}

var nonsenseThrew = false;
LyricsResult? nonsense = null;
try
{
    nonsense = await liveClient.GetExactAsync("zzzqqq", "xxxyyy", null, null, CancellationToken.None);
}
catch (Exception ex)
{
    nonsenseThrew = true;
    Console.WriteLine(ex);
}

Check("a nonsense exact query returns null instead of throwing", !nonsenseThrew && nonsense is null, nonsenseThrew ? "threw" : "null");

LyricsResult? nonsenseSearch = null;
try
{
    nonsenseSearch = await liveClient.SearchAsync("zzzqqq", "xxxyyy", null, null, CancellationToken.None);
}
catch (Exception ex)
{
    Console.WriteLine(ex);
}

Check("a nonsense search returns null instead of throwing", nonsenseSearch is null, nonsenseSearch is null ? "null" : "unexpected result");

LyricsResult? searched = null;
try
{
    searched = await FetchAsync(() => liveClient.SearchAsync("Radiohead", "Creep", null, 239, CancellationToken.None));
}
catch (Exception ex)
{
    Console.WriteLine(ex);
}

Check(
    "SearchAsync picks a synced candidate from the array",
    searched is not null && searched.IsSynced && searched.Lines.Count > 0,
    searched is null ? "null" : $"id={searched.LrclibId} track={searched.TrackName} lines={searched.Lines.Count}");

LyricsResult? byId = null;
try
{
    byId = await FetchAsync(() => liveClient.GetByIdAsync(496, CancellationToken.None));
}
catch (Exception ex)
{
    Console.WriteLine(ex);
}

Check(
    "GetByIdAsync(496) returns the Creep record",
    byId is not null && byId.IsSynced && byId.LrclibId == 496 && byId.Lines.Count > 0,
    byId is null ? "null" : $"id={byId.LrclibId} track={byId.TrackName} lines={byId.Lines.Count}");

using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
var cancellationPropagated = false;
try
{
    await liveClient.GetExactAsync("Radiohead", "Creep", "Pablo Honey", 239, cancelled.Token);
}
catch (OperationCanceledException)
{
    cancellationPropagated = true;
}
catch (Exception ex)
{
    Console.WriteLine(ex);
}

Check("a caller-cancelled token still propagates OperationCanceledException", cancellationPropagated);

// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0} checks run, {1} failed", total, failures));

TryDelete(runRoot);

return failures == 0 ? 0 : 1;

void Check(string name, bool ok, string detail = "")
{
    total++;
    if (!ok)
    {
        failures++;
    }

    Console.WriteLine(string.Concat(ok ? "PASS" : "FAIL", " | ", name, detail.Length > 0 ? " | " + detail : string.Empty));
}

static string Describe(IReadOnlyList<LyricLine> lines)
{
    return string.Join(", ", lines.Select(l => string.Format(CultureInfo.InvariantCulture, "{0:0.###}s:'{1}'", l.Start.TotalSeconds, l.Text)));
}

/// <summary>
/// lrclib rate-limits bursts and answers 503 for minutes at a time, so a transient miss is retried
/// with a widening backoff before it counts as a failure. Without that these live checks go red for
/// reasons that have nothing to do with Overlyric, which trains people to ignore red output.
/// </summary>
static async Task<LyricsResult?> FetchAsync(Func<Task<LyricsResult?>> call, int attempts = 5)
{
    for (var attempt = 1; ; attempt++)
    {
        var result = await call();
        if (result is not null || attempt >= attempts)
        {
            return result;
        }

        var delay = TimeSpan.FromSeconds(Math.Min(2 * attempt, 6));
        Console.WriteLine(
            $"       lrclib busy - retrying in {delay.TotalSeconds:0}s (attempt {attempt} of {attempts})");

        await Task.Delay(delay);
    }
}

static bool Equivalent(LyricsResult a, LyricsResult b)
{
    if (a.IsSynced != b.IsSynced
        || a.IsInstrumental != b.IsInstrumental
        || a.LrclibId != b.LrclibId
        || a.DurationSeconds != b.DurationSeconds
        || !string.Equals(a.PlainLyrics, b.PlainLyrics, StringComparison.Ordinal)
        || !string.Equals(a.TrackName, b.TrackName, StringComparison.Ordinal)
        || !string.Equals(a.ArtistName, b.ArtistName, StringComparison.Ordinal)
        || a.Lines.Count != b.Lines.Count)
    {
        return false;
    }

    for (var i = 0; i < a.Lines.Count; i++)
    {
        if (a.Lines[i].Start != b.Lines[i].Start || !string.Equals(a.Lines[i].Text, b.Lines[i].Text, StringComparison.Ordinal))
        {
            return false;
        }
    }

    return true;
}

static bool Throws(Action action)
{
    try
    {
        action();
        return false;
    }
    catch
    {
        return true;
    }
}

static void TryDelete(string directory)
{
    try
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    catch
    {
    }
}
