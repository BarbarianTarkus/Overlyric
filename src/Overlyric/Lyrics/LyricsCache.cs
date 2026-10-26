using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlyric.Lyrics;

/// <summary>
/// Tiny one-file-per-entry JSON cache for lyrics lookups, plus negative ("no lyrics, ever")
/// tombstones so that hopeless tracks are not re-queried against lrclib on every play.
/// </summary>
/// <remarks>
/// File name is the lowercase hex SHA-256 of the key plus <c>.json</c>; the key itself is stored
/// inside the file and verified on read, so a hash collision or a renamed file is rejected instead
/// of returning someone else's lyrics. Every disk operation is best-effort: a failure degrades to
/// a cache miss and never propagates to the caller. Writes go to a temporary file in the same
/// directory and are then moved over the target, which is atomic on Windows.
/// <para>
/// Every entry also records the <see cref="CurrentMatchVersion"/> of the matching rules that produced
/// it. When those rules change - the release-suffix fix and the synced-same-duration rule both did -
/// an entry written by the older rules is not wrong on its face, but it is the answer the old rules
/// gave, and serving it would hide the fix from tracks that were already played. So an entry from an
/// older matcher reads as a miss: the track is resolved once more against lrclib and the entry is
/// rewritten. A tombstone is bookkeeping of the same kind and is aged out the same way, because a
/// search that has since learned to ask a better question may well find what the old one missed.
/// </para>
/// </remarks>
public sealed class LyricsCache
{
    private const string EntryExtension = ".json";
    private const string MissExtension = ".miss.json";
    private const string CandidatesExtension = ".candidates.json";

    /// <summary>
    /// How long "lrclib has nothing for this track" is trusted before the track is asked about again.
    /// </summary>
    /// <remarks>
    /// lrclib's own documentation says missing tracks "may become available in a later request",
    /// because a background service picks them up - so a verdict that never expires would hide
    /// lyrics that exist by now, which is the one failure this cache must not cause. Twenty-one days
    /// is long enough that a hopeless track is not re-queried on every replay and short enough that
    /// a backfilled one arrives within a month of listening.
    /// </remarks>
    private static readonly TimeSpan TombstoneLifetime = TimeSpan.FromDays(21);

    /// <summary>
    /// Version of the matching rules that decide what an entry means. Bump it when they change, so
    /// that entries produced by the previous rules are re-resolved once instead of being trusted.
    /// </summary>
    internal const int CurrentMatchVersion = 2;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;

    public LyricsCache(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;

        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception)
        {
            // Unusable directory (permissions, bad path): every operation below just reports a miss.
        }
    }

    /// <summary>
    /// Deterministic, case-insensitive cache key for a track: trim, lowercase and collapse internal
    /// whitespace in each component. Diacritics are deliberately left alone.
    /// </summary>
    public static string MakeKey(string artist, string track, string? album)
    {
        // '\n' can never survive Normalize, so it is an injection-proof separator.
        return string.Concat(Normalize(artist), "\n", Normalize(track), "\n", Normalize(album));
    }

    /// <summary>Returns true when a cached result was found and read successfully.</summary>
    public bool TryGet(string key, out LyricsResult? result)
    {
        result = null;

        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        try
        {
            var path = EntryPath(key);
            if (!File.Exists(path))
            {
                return false;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            var entry = JsonSerializer.Deserialize(json, LyricsCacheJsonContext.Default.LyricsCacheEntry);
            if (entry is null || entry.Result is null || !string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                // Stale, corrupt or colliding entry: treat as a miss.
                return false;
            }

            if (entry.MatchVersion < CurrentMatchVersion)
            {
                // Written by an older matcher: the answer it holds is the one those rules gave.
                return false;
            }

            result = entry.Result.ToLyricsResult();
            return true;
        }
        catch (Exception)
        {
            result = null;
            return false;
        }
    }

    /// <summary>Stores a result (including a valid "instrumental" answer) and clears any tombstone.</summary>
    public void Set(string key, LyricsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        try
        {
            var entry = new LyricsCacheEntry
            {
                Key = key,
                MatchVersion = CurrentMatchVersion,
                WrittenAt = Now(),
                Result = CachedLyrics.FromLyricsResult(result),
            };

            var json = JsonSerializer.Serialize(entry, LyricsCacheJsonContext.Default.LyricsCacheEntry);
            WriteAtomic(EntryPath(key), json);
            TryDelete(MissPath(key));
        }
        catch (Exception)
        {
            // Caching is an optimisation; dropping the write is always acceptable.
        }
    }

    /// <summary>Records "lrclib will never have this track" so future lookups skip the network.</summary>
    public void SetMiss(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        try
        {
            var entry = new LyricsCacheEntry
            {
                Key = key,
                MatchVersion = CurrentMatchVersion,
                WrittenAt = Now(),
                Result = null,
            };

            var json = JsonSerializer.Serialize(entry, LyricsCacheJsonContext.Default.LyricsCacheEntry);
            WriteAtomic(MissPath(key), json);
            TryDelete(EntryPath(key));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>True when a negative tombstone for <paramref name="key"/> is still current.</summary>
    public bool IsMiss(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        try
        {
            var path = MissPath(key);
            if (!File.Exists(path))
            {
                return false;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            var entry = JsonSerializer.Deserialize(json, LyricsCacheJsonContext.Default.LyricsCacheEntry);

            // A tombstone is respected while the matcher that wrote it is still current and while
            // it is younger than the lifetime: a search that has since learned a better question,
            // or a record lrclib has since picked up, may well find what the old verdict missed.
            return entry is not null
                   && string.Equals(entry.Key, key, StringComparison.Ordinal)
                   && entry.MatchVersion >= CurrentMatchVersion
                   && !IsStale(entry.WrittenAt);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Stores the ranked list the version selector shows, so opening it in a later session costs no
    /// request at all.
    /// </summary>
    /// <remarks>
    /// The list is a by-product of the search that resolved the track, and it is the expensive part
    /// of a resolution: one search answer is twenty records, roughly twenty times the bytes of an
    /// exact lookup. Throwing it away when the result was written meant re-downloading all of it the
    /// next time somebody opened the selector.
    /// </remarks>
    public void SetCandidates(string key, IReadOnlyList<LyricsCandidate> candidates)
    {
        if (string.IsNullOrEmpty(key) || candidates.Count == 0)
        {
            return;
        }

        try
        {
            var cached = new List<CachedCandidate>(candidates.Count);

            foreach (var candidate in candidates)
            {
                cached.Add(new CachedCandidate
                {
                    Id = candidate.Id,
                    TrackName = candidate.TrackName,
                    ArtistName = candidate.ArtistName,
                    AlbumName = candidate.AlbumName,
                    DurationSeconds = candidate.DurationSeconds,
                    HasSynced = candidate.HasSynced,
                    HasPlain = candidate.HasPlain,
                    HasWordSync = candidate.HasWordSync,
                    IsInstrumental = candidate.IsInstrumental,
                    SyncedLineCount = candidate.SyncedLineCount,
                    PlainLineCount = candidate.PlainLineCount,
                });
            }

            var entry = new LyricsCandidatesEntry
            {
                Key = key,
                MatchVersion = CurrentMatchVersion,
                WrittenAt = Now(),
                Candidates = cached,
            };

            var json = JsonSerializer.Serialize(entry, LyricsCacheJsonContext.Default.LyricsCandidatesEntry);
            WriteAtomic(CandidatesPath(key), json);
        }
        catch (Exception)
        {
            // The selector can always ask lrclib again; losing this write is only a lost shortcut.
        }
    }

    /// <summary>
    /// Reads the stored candidate list. Returns false when there is none, when it was written by an
    /// older matcher, or when it cannot be read - every one of which means "ask lrclib instead".
    /// </summary>
    public bool TryGetCandidates(string key, out IReadOnlyList<LyricsCandidate> candidates)
    {
        candidates = Array.Empty<LyricsCandidate>();

        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        try
        {
            var path = CandidatesPath(key);
            if (!File.Exists(path))
            {
                return false;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            var entry = JsonSerializer.Deserialize(json, LyricsCacheJsonContext.Default.LyricsCandidatesEntry);

            if (entry?.Candidates is not { Count: > 0 }
                || !string.Equals(entry.Key, key, StringComparison.Ordinal)
                || entry.MatchVersion < CurrentMatchVersion)
            {
                return false;
            }

            var list = new List<LyricsCandidate>(entry.Candidates.Count);

            foreach (var candidate in entry.Candidates)
            {
                if (candidate.Id <= 0)
                {
                    continue;
                }

                list.Add(new LyricsCandidate
                {
                    Id = candidate.Id,
                    TrackName = candidate.TrackName,
                    ArtistName = candidate.ArtistName,
                    AlbumName = candidate.AlbumName,
                    DurationSeconds = candidate.DurationSeconds,
                    HasSynced = candidate.HasSynced,
                    HasPlain = candidate.HasPlain,
                    HasWordSync = candidate.HasWordSync,
                    IsInstrumental = candidate.IsInstrumental,
                    SyncedLineCount = candidate.SyncedLineCount,
                    PlainLineCount = candidate.PlainLineCount,
                });
            }

            candidates = list;
            return list.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes the positive entry, any tombstone and the stored candidate list for a track. Refresh
    /// uses this so a wrong record or a stale "no lyrics" answer can be asked again without touching
    /// the rest of the cache.
    /// </summary>
    public bool Forget(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var removed = false;

        try
        {
            foreach (var path in new[] { EntryPath(key), MissPath(key), CandidatesPath(key) })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed = true;
                }
            }
        }
        catch (Exception)
        {
            // A failed delete degrades to "nothing forgotten"; the next lookup still tries.
        }

        return removed;
    }

    /// <summary>Counts cache files and their size for the settings window. Best effort.</summary>
    public (int Entries, long Bytes) GetStats()
    {
        var entries = 0;
        long bytes = 0;

        try
        {
            if (!Directory.Exists(_directory))
            {
                return (0, 0);
            }

            foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                if (!path.EndsWith(CandidatesExtension, StringComparison.Ordinal))
                {
                    entries++;
                }

                try
                {
                    bytes += new FileInfo(path).Length;
                }
                catch (Exception)
                {
                    // The count is still useful even if one file cannot be sized.
                }
            }
        }
        catch (Exception)
        {
            return (0, 0);
        }

        return (entries, bytes);
    }

    /// <summary>Deletes every cache file and returns how many were removed.</summary>
    public int Clear()
    {
        var removed = 0;

        try
        {
            if (!Directory.Exists(_directory))
            {
                return 0;
            }

            foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (Exception)
                {
                    // Keep going: clearing most of the cache is better than clearing none.
                }
            }
        }
        catch (Exception)
        {
            return removed;
        }

        return removed;
    }

    private string EntryPath(string key)
    {
        return Path.Combine(_directory, Hash(key) + EntryExtension);
    }

    private string MissPath(string key)
    {
        return Path.Combine(_directory, Hash(key) + MissExtension);
    }

    private string CandidatesPath(string key)
    {
        return Path.Combine(_directory, Hash(key) + CandidatesExtension);
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static bool IsStale(long writtenAt)
    {
        // No timestamp means the file predates the field, so its age is unknown and unknowable.
        // Ageing it out is the safe reading: it re-queries once instead of trusting forever.
        if (writtenAt <= 0)
        {
            return true;
        }

        return Now() - writtenAt >= (long)TombstoneLifetime.TotalSeconds;
    }

    private static void WriteAtomic(string path, string json)
    {
        var temp = string.Concat(path, ".", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), ".tmp");

        try
        {
            File.WriteAllText(temp, json, Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            TryDelete(temp);
            throw; // Swallowed by Set/SetMiss, which own the "never throw" contract.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }

    private static string Hash(string key)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(digest);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        var builder = new StringBuilder(trimmed.Length);
        var pendingSpace = false;

        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }
}

/// <summary>On-disk envelope; <see cref="Key"/> is re-verified on read.</summary>
internal sealed class LyricsCacheEntry
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>
    /// The version of the matching rules that produced this entry. Absent in files written before
    /// the field existed, which is exactly what makes them read as stale.
    /// </summary>
    [JsonPropertyName("matchVersion")]
    public int MatchVersion { get; init; }

    /// <summary>
    /// When the entry was written, in Unix seconds. A tombstone older than
    /// <see cref="TombstoneLifetime"/> stops being trusted; an entry with no timestamp at all was
    /// written before the field existed and is aged out on first sight.
    /// </summary>
    [JsonPropertyName("writtenAt")]
    public long WrittenAt { get; init; }

    /// <summary>Null marks a negative-cache tombstone.</summary>
    [JsonPropertyName("result")]
    public CachedLyrics? Result { get; init; }
}

/// <summary>
/// Serialisable mirror of <see cref="LyricsResult"/>. Kept deliberately primitive (no
/// <see cref="TimeSpan"/>, no interface typed collections) so the source generator stays simple.
/// </summary>
internal sealed class CachedLyrics
{
    [JsonPropertyName("isSynced")]
    public bool IsSynced { get; init; }

    [JsonPropertyName("isInstrumental")]
    public bool IsInstrumental { get; init; }

    [JsonPropertyName("plainLyrics")]
    public string? PlainLyrics { get; init; }

    [JsonPropertyName("lrclibId")]
    public long? LrclibId { get; init; }

    [JsonPropertyName("trackName")]
    public string? TrackName { get; init; }

    [JsonPropertyName("artistName")]
    public string? ArtistName { get; init; }

    [JsonPropertyName("durationSeconds")]
    public double? DurationSeconds { get; init; }

    [JsonPropertyName("lines")]
    public List<CachedLine>? Lines { get; init; }

    public static CachedLyrics FromLyricsResult(LyricsResult result)
    {
        var lines = new List<CachedLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            List<CachedWord>? words = null;

            if (line.Words is { Count: > 0 })
            {
                words = new List<CachedWord>(line.Words.Count);

                foreach (var word in line.Words)
                {
                    words.Add(new CachedWord
                    {
                        StartTicks = word.Start.Ticks,
                        EndTicks = word.End?.Ticks,
                        Text = word.Text,
                    });
                }
            }

            lines.Add(new CachedLine
            {
                StartTicks = line.Start.Ticks,
                Text = line.Text,
                EndTicks = line.End?.Ticks,
                Words = words,
            });
        }

        return new CachedLyrics
        {
            IsSynced = result.IsSynced,
            IsInstrumental = result.IsInstrumental,
            PlainLyrics = result.PlainLyrics,
            LrclibId = result.LrclibId,
            TrackName = result.TrackName,
            ArtistName = result.ArtistName,
            DurationSeconds = result.DurationSeconds,
            Lines = lines,
        };
    }

    public LyricsResult ToLyricsResult()
    {
        var source = Lines;
        var lines = new List<LyricLine>(source?.Count ?? 0);

        if (source is not null)
        {
            foreach (var line in source)
            {
                List<LyricWord>? words = null;

                if (line.Words is { Count: > 0 })
                {
                    words = new List<LyricWord>(line.Words.Count);

                    foreach (var word in line.Words)
                    {
                        words.Add(new LyricWord(
                            TimeSpan.FromTicks(word.StartTicks),
                            word.EndTicks is null ? null : TimeSpan.FromTicks(word.EndTicks.Value),
                            word.Text ?? string.Empty));
                    }
                }

                lines.Add(new LyricLine(
                    TimeSpan.FromTicks(line.StartTicks),
                    line.Text ?? string.Empty,
                    line.EndTicks is null ? null : TimeSpan.FromTicks(line.EndTicks.Value),
                    words));
            }
        }

        return new LyricsResult
        {
            Lines = lines,
            IsSynced = IsSynced,
            IsInstrumental = IsInstrumental,
            PlainLyrics = PlainLyrics,
            LrclibId = LrclibId,
            TrackName = TrackName,
            ArtistName = ArtistName,
            DurationSeconds = DurationSeconds,
        };
    }
}

/// <summary>One cached lyric line.</summary>
internal sealed class CachedLine
{
    /// <summary>
    /// Ticks rather than seconds on purpose: <see cref="TimeSpan.FromSeconds"/> truncates to the
    /// tick, so a double second value is not an exact round-trip for every millisecond.
    /// </summary>
    [JsonPropertyName("startTicks")]
    public long StartTicks { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>When the line ends, in ticks, when the sheet said so.</summary>
    [JsonPropertyName("endTicks")]
    public long? EndTicks { get; init; }

    /// <summary>Word-level timings, when the sheet carried them.</summary>
    [JsonPropertyName("words")]
    public List<CachedWord>? Words { get; init; }
}

/// <summary>One cached word timing.</summary>
internal sealed class CachedWord
{
    [JsonPropertyName("startTicks")]
    public long StartTicks { get; init; }

    [JsonPropertyName("endTicks")]
    public long? EndTicks { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>On-disk envelope for the version selector's candidate list.</summary>
internal sealed class LyricsCandidatesEntry
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>The matcher that produced the badges, so a change to them invalidates the list.</summary>
    [JsonPropertyName("matchVersion")]
    public int MatchVersion { get; init; }

    [JsonPropertyName("writtenAt")]
    public long WrittenAt { get; init; }

    [JsonPropertyName("candidates")]
    public List<CachedCandidate>? Candidates { get; init; }
}

/// <summary>Serialisable mirror of <see cref="LyricsCandidate"/>.</summary>
internal sealed class CachedCandidate
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("trackName")]
    public string? TrackName { get; init; }

    [JsonPropertyName("artistName")]
    public string? ArtistName { get; init; }

    [JsonPropertyName("albumName")]
    public string? AlbumName { get; init; }

    [JsonPropertyName("durationSeconds")]
    public double? DurationSeconds { get; init; }

    [JsonPropertyName("hasSynced")]
    public bool HasSynced { get; init; }

    [JsonPropertyName("hasPlain")]
    public bool HasPlain { get; init; }

    [JsonPropertyName("hasWordSync")]
    public bool HasWordSync { get; init; }

    [JsonPropertyName("isInstrumental")]
    public bool IsInstrumental { get; init; }

    [JsonPropertyName("syncedLineCount")]
    public int SyncedLineCount { get; init; }

    [JsonPropertyName("plainLineCount")]
    public int PlainLineCount { get; init; }
}

/// <summary>Source-generated serialisation metadata for the cache file format (NativeAOT safe).</summary>
[JsonSerializable(typeof(LyricsCacheEntry))]
[JsonSerializable(typeof(LyricsCandidatesEntry))]
internal sealed partial class LyricsCacheJsonContext : JsonSerializerContext;
