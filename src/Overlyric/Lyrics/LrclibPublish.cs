using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlyric.Lyrics;

/// <summary>The proof-of-work challenge lrclib sends before a publish.</summary>
public sealed class PublishChallenge
{
    public string Prefix { get; init; } = "";

    /// <summary>64 uppercase hex characters: a 256-bit big-endian threshold.</summary>
    public string Target { get; init; } = "";
}

/// <summary>The publish payload, in the camelCase shape the server expects.</summary>
public sealed class PublishRequest
{
    public string TrackName { get; init; } = "";

    public string ArtistName { get; init; } = "";

    /// <summary>Empty string when the player reports no album; null is rejected by the server.</summary>
    public string AlbumName { get; init; } = "";

    public double Duration { get; init; }

    public string? PlainLyrics { get; init; }

    public string? SyncedLyrics { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, PublishJsonContext.Default.PublishRequest);
}

/// <summary>What one explicit publish attempt did.</summary>
internal enum PublishOutcome
{
    Published = 0,
    ValidationFailed,
    ChallengeUnavailable,
    TokenRejected,
    RateLimited,
    ServerBusy,
    ServerError,
    Cancelled,
    NetworkFailed
}

/// <summary>
/// The local checks before a publish, and the LRC body to send. The server validates almost nothing,
/// so the client is the last line of defence; every rule here is one a bad paste can break.
/// </summary>
internal static class PublishValidation
{
    public const double MaxDurationSeconds = 3600;

    /// <summary>How far the last timestamp may sit past the reported track length.</summary>
    public const double DurationGraceSeconds = 5;

    public static IReadOnlyList<string> Validate(
        string? title,
        string? artist,
        double? durationSeconds,
        IReadOnlyList<LyricLine> lines)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(title))
        {
            errors.Add("The track needs a title.");
        }

        if (string.IsNullOrWhiteSpace(artist))
        {
            errors.Add("The track needs an artist.");
        }

        if (durationSeconds is not > 0 || durationSeconds > MaxDurationSeconds)
        {
            errors.Add("The track length must be between 1 second and one hour.");
        }

        if (lines.Count == 0)
        {
            errors.Add("There are no lyric lines to publish.");
        }

        var previous = TimeSpan.MinValue;
        var anyText = false;

        foreach (var line in lines)
        {
            if (line.Start < TimeSpan.Zero)
            {
                errors.Add("A line starts before zero.");
                break;
            }

            if (line.Start < previous)
            {
                errors.Add("The timestamps are not in order.");
                break;
            }

            previous = line.Start;

            if (!string.IsNullOrWhiteSpace(line.Text))
            {
                anyText = true;
            }
        }

        if (lines.Count > 0 && !anyText)
        {
            errors.Add("Every line is empty.");
        }

        if (durationSeconds is { } duration && lines.Count > 0)
        {
            var last = lines[^1].Start.TotalSeconds;

            if (last > duration + DurationGraceSeconds)
            {
                errors.Add("The last timestamp runs past the end of the track.");
            }
        }

        return errors;
    }

    /// <summary>The timestamped LRC body a publish sends; metadata tags belong in the JSON fields.</summary>
    public static string BuildSyncedBody(IReadOnlyList<LyricLine> lines)
    {
        var builder = new StringBuilder(lines.Count * 40);

        foreach (var line in lines)
        {
            builder.Append('[')
                .Append(LyricTextCodec.FormatTimestamp(line.Start))
                .Append(']')
                .Append((line.Text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal))
                .Append("\r\n");
        }

        return builder.ToString();
    }

    public static PublishRequest BuildRequest(
        string title,
        string artist,
        string? album,
        double durationSeconds,
        IReadOnlyList<LyricLine> lines) => new()
    {
        TrackName = title.Trim(),
        ArtistName = artist.Trim(),
        AlbumName = (album ?? string.Empty).Trim(),
        Duration = Math.Round(durationSeconds, 2),
        SyncedLyrics = BuildSyncedBody(lines)
    };
}

/// <summary>
/// The challenge's proof of work: a nonce whose <c>SHA-256(prefix + nonce)</c> is at or below a
/// 256-bit big-endian threshold. The nonce is decimal text, as the reference clients send it.
/// </summary>
internal static class LrclibChallenge
{
    private const int TargetBytes = 32;

    /// <summary>Attempts per work item; small enough for a cancelling solve to stop promptly.</summary>
    private const int ChunkSize = 65_536;

    public static bool TryParse(string json, out PublishChallenge challenge, out string? error)
    {
        challenge = new PublishChallenge();
        error = null;

        try
        {
            var parsed = JsonSerializer.Deserialize(json, PublishJsonContext.Default.PublishChallenge);

            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Prefix) || string.IsNullOrWhiteSpace(parsed.Target))
            {
                error = "The challenge response is missing its prefix or target.";
                return false;
            }

            if (parsed.Prefix.Length > 128 || !parsed.Prefix.All(char.IsAsciiLetterOrDigit))
            {
                error = "The challenge prefix is not plausible.";
                return false;
            }

            if (!TryParseTarget(parsed.Target, out _))
            {
                error = "The challenge target is not a 256-bit hex value.";
                return false;
            }

            challenge = new PublishChallenge { Prefix = parsed.Prefix, Target = parsed.Target.ToUpperInvariant() };
            return true;
        }
        catch (JsonException)
        {
            error = "The challenge response is not JSON.";
            return false;
        }
    }

    public static bool TryParseTarget(string target, out byte[] threshold)
    {
        threshold = new byte[TargetBytes];

        if (string.IsNullOrWhiteSpace(target) || target.Length != TargetBytes * 2)
        {
            return false;
        }

        try
        {
            for (var i = 0; i < TargetBytes; i++)
            {
                threshold[i] = byte.Parse(target.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>True when the nonce's digest is at or below the threshold, bytewise.</summary>
    public static bool IsValidNonce(string prefix, string targetHex, string nonce)
    {
        if (!TryParseTarget(targetHex, out var threshold))
        {
            return false;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(prefix + nonce));

        for (var i = 0; i < TargetBytes; i++)
        {
            if (digest[i] < threshold[i])
            {
                return true;
            }

            if (digest[i] > threshold[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Solves the challenge on every available core, reporting the attempt count. The progress value
    /// is attempts, not a percentage: the threshold is server-side and can be harder under load, so
    /// the client cannot know how long it will take.
    /// </summary>
    public static string? Solve(PublishChallenge challenge, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (!TryParseTarget(challenge.Target, out var threshold))
        {
            return null;
        }

        var foundNonce = -1L;
        var attempts = 0L;

        // The search is unbounded in theory; in practice the base threshold is about 2^24. The
        // parallel loop covers a very large range and stops as soon as any worker finds a nonce.
        const long attemptLimit = 1L << 34;
        var chunks = (int)Math.Min(int.MaxValue / 2, (attemptLimit + ChunkSize - 1) / ChunkSize - 1);

        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
        };

        try
        {
            Parallel.For(
                0,
                chunks,
                options,
                (chunk, state) =>
                {
                    if (Volatile.Read(ref foundNonce) >= 0)
                    {
                        state.Break();
                        return;
                    }

                    var start = (long)chunk * ChunkSize;

                    for (var offset = 0; offset < ChunkSize; offset++)
                    {
                        var nonce = start + offset;
                        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(challenge.Prefix + nonce.ToString(CultureInfo.InvariantCulture)));

                        if (IsAtOrBelow(digest, threshold))
                        {
                            Interlocked.CompareExchange(ref foundNonce, nonce, -1);
                            state.Break();
                            return;
                        }
                    }

                    var done = Interlocked.Add(ref attempts, ChunkSize);

                    if (done % 1_048_576 == 0)
                    {
                        progress?.Report(done);
                    }
                });
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        return foundNonce >= 0 ? foundNonce.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static bool IsAtOrBelow(byte[] digest, byte[] threshold)
    {
        for (var i = 0; i < TargetBytes; i++)
        {
            if (digest[i] < threshold[i])
            {
                return true;
            }

            if (digest[i] > threshold[i])
            {
                return false;
            }
        }

        return true;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PublishChallenge))]
[JsonSerializable(typeof(PublishRequest))]
internal sealed partial class PublishJsonContext : JsonSerializerContext;
