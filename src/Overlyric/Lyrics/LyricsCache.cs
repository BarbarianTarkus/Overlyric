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

    /// <summary>
    /// Version of the matching rules that decide what an entry means. Bump it when they change, so
    /// that entries produced by the previous rules are re-resolved once instead of being trusted.
    /// </summary>
    private const int CurrentMatchVersion = 1;

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

            // A tombstone is only respected while the matcher that wrote it is the current one: a
            // search that has since learned a better question may well find what that one missed.
            return entry is not null
                   && string.Equals(entry.Key, key, StringComparison.Ordinal)
                   && entry.MatchVersion >= CurrentMatchVersion;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string EntryPath(string key)
    {
        return Path.Combine(_directory, Hash(key) + EntryExtension);
    }

    private string MissPath(string key)
    {
        return Path.Combine(_directory, Hash(key) + MissExtension);
    }

    private void WriteAtomic(string path, string json)
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
            lines.Add(new CachedLine
            {
                StartTicks = line.Start.Ticks,
                Text = line.Text,
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
                lines.Add(new LyricLine(TimeSpan.FromTicks(line.StartTicks), line.Text ?? string.Empty));
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
}

/// <summary>Source-generated serialisation metadata for the cache file format (NativeAOT safe).</summary>
[JsonSerializable(typeof(LyricsCacheEntry))]
internal sealed partial class LyricsCacheJsonContext : JsonSerializerContext;
