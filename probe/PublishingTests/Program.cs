using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Overlyric.Lyrics;

namespace Overlyric.PublishingTests;

/// <summary>
/// Checks the publish contract client-side: challenge parsing, the proof-of-work comparison and
/// solver, the camelCase payload, the timestamped LRC body, and the validation that stands between a
/// bad sheet and a public record.
/// </summary>
internal static class Program
{
    private static int _checks;
    private static int _failures;
    private const string Prefix = "abc123";
    private static readonly string AllOnes = new('F', 64);
    private static readonly string NoTarget = new('0', 64);

    private static int Main()
    {
        ChallengeChecks();
        SolverChecks();
        PayloadChecks();
        ValidationChecks();

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ALL {_checks} CHECKS PASSED"
            : $"{_failures} OF {_checks} CHECK(S) FAILED");

        return _failures == 0 ? 0 : 1;
    }

    private static void ChallengeChecks()
    {
        var valid = JsonSerializer.Serialize(new { prefix = Prefix, target = AllOnes });
        Check("a valid challenge parses", LrclibChallenge.TryParse(valid, out var challenge, out _)
            && challenge.Prefix == Prefix && challenge.Target == AllOnes, challenge?.Prefix);
        Check("the target is normalised to uppercase",
            LrclibChallenge.TryParse(JsonSerializer.Serialize(new { prefix = Prefix, target = AllOnes.ToLowerInvariant() }), out var lowered, out _)
            && lowered.Target == AllOnes, lowered?.Target);

        Check("a challenge with no prefix is refused",
            !LrclibChallenge.TryParse(JsonSerializer.Serialize(new { target = AllOnes }), out _, out var missingPrefix), missingPrefix);
        Check("a challenge with a short target is refused",
            !LrclibChallenge.TryParse(JsonSerializer.Serialize(new { prefix = Prefix, target = "ABCD" }), out _, out var shortTarget), shortTarget);
        Check("a challenge with a non-hex target is refused",
            !LrclibChallenge.TryParse(JsonSerializer.Serialize(new { prefix = Prefix, target = new string('Z', 64) }), out _, out var hexTarget), hexTarget);
        Check("a challenge with a punctuation prefix is refused",
            !LrclibChallenge.TryParse(JsonSerializer.Serialize(new { prefix = "abc-123", target = AllOnes }), out _, out var punctuation), punctuation);
        Check("malformed JSON is refused, not thrown",
            !LrclibChallenge.TryParse("not json", out _, out var malformed), malformed);
        Check("a 32-byte target parses", LrclibChallenge.TryParseTarget(AllOnes, out var threshold) && threshold.Length == 32, "");
        Check("a target of the wrong length is refused", !LrclibChallenge.TryParseTarget("F0", out _), "");
    }

    private static void SolverChecks()
    {
        Check("any nonce satisfies an all-ones target",
            LrclibChallenge.IsValidNonce(Prefix, AllOnes, "12345"), "");
        Check("no nonce satisfies an all-zero target",
            !LrclibChallenge.IsValidNonce(Prefix, NoTarget, "12345"), "");

        // A target equal to one specific digest: exactly that nonce is accepted unless an earlier
        // collision exists, which IsValidNonce can verify deterministically.
        var target = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Prefix + "123")));
        Check("a nonce whose digest equals the target is accepted",
            LrclibChallenge.IsValidNonce(Prefix, target, "123"), target);
        Check("a digest above the target is rejected",
            !LrclibChallenge.IsValidNonce(Prefix, Decrement(target, 1), "123"), Decrement(target, 1));

        var easy = new PublishChallenge { Prefix = Prefix, Target = AllOnes };
        var nonce = LrclibChallenge.Solve(easy, progress: null, CancellationToken.None);
        Check("the solver finds a nonce for an easy target",
            nonce is not null && LrclibChallenge.IsValidNonce(Prefix, AllOnes, nonce), nonce);

        var specific = new PublishChallenge { Prefix = Prefix, Target = target };
        var found = LrclibChallenge.Solve(specific, progress: null, CancellationToken.None);
        Check("the solver's nonce satisfies the real comparison",
            found is not null && LrclibChallenge.IsValidNonce(Prefix, target, found), found);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Check("a cancelled solve stops instead of hanging",
            LrclibChallenge.Solve(easy, progress: null, cancelled.Token) is null, "");
    }

    private static void PayloadChecks()
    {
        var lines = new List<LyricLine>
        {
            new(TimeSpan.FromSeconds(1), "One"),
            new(TimeSpan.FromSeconds(4.25), "Two\r\nwith a break")
        };

        var request = PublishValidation.BuildRequest("Song", "Artist", null, 300, lines);
        var json = request.ToJson();

        Check("the payload uses the camelCase field names",
            json.Contains("\"trackName\"", StringComparison.Ordinal)
            && json.Contains("\"artistName\"", StringComparison.Ordinal)
            && json.Contains("\"albumName\"", StringComparison.Ordinal)
            && json.Contains("\"duration\"", StringComparison.Ordinal)
            && json.Contains("\"syncedLyrics\"", StringComparison.Ordinal), json);
        Check("a missing album becomes an empty string, not null",
            request.AlbumName.Length == 0 && json.Contains("\"albumName\":\"\"", StringComparison.Ordinal), request.AlbumName);
        Check("the payload carries the reported duration", Math.Abs(request.Duration - 300) < 0.01, request.Duration);

        using var document = JsonDocument.Parse(json);
        var synced = document.RootElement.GetProperty("syncedLyrics").GetString() ?? "";
        Check("the synced body has one millisecond timestamp per line",
            synced.Contains("[00:01.000]One", StringComparison.Ordinal)
            && synced.Contains("[00:04.250]Two", StringComparison.Ordinal), synced);
        Check("the synced body uses CRLF and carries no metadata tags",
            synced.Contains("\r\n", StringComparison.Ordinal) && !synced.Contains("[ar:", StringComparison.Ordinal), synced);
        Check("a newline inside a line cannot break the body",
            !synced.Contains("Two\r\nwith", StringComparison.Ordinal) && synced.Contains("Twowith a break", StringComparison.Ordinal), synced);
    }

    private static void ValidationChecks()
    {
        var lines = new List<LyricLine>
        {
            new(TimeSpan.FromSeconds(1), "One"),
            new(TimeSpan.FromSeconds(4), "Two")
        };

        Check("a plausible sheet validates", PublishValidation.Validate("Song", "Artist", 300, lines).Count == 0, "");
        Check("an empty title is refused", PublishValidation.Validate(" ", "Artist", 300, lines).Count > 0, "");
        Check("an empty artist is refused", PublishValidation.Validate("Song", "", 300, lines).Count > 0, "");
        Check("a missing duration is refused", PublishValidation.Validate("Song", "Artist", null, lines).Count > 0, "");
        Check("an absurd duration is refused", PublishValidation.Validate("Song", "Artist", 4000, lines).Count > 0, "");
        Check("a sheet with no lines is refused", PublishValidation.Validate("Song", "Artist", 300, []).Count > 0, "");
        Check("a negative timestamp is refused",
            PublishValidation.Validate("Song", "Artist", 300, [new LyricLine(TimeSpan.FromSeconds(-1), "x")]).Count > 0, "");
        Check("out-of-order timestamps are refused",
            PublishValidation.Validate("Song", "Artist", 300, [new LyricLine(TimeSpan.FromSeconds(5), "a"), new LyricLine(TimeSpan.FromSeconds(2), "b")]).Count > 0, "");
        Check("an all-empty sheet is refused",
            PublishValidation.Validate("Song", "Artist", 300, [new LyricLine(TimeSpan.FromSeconds(1), " ")]).Count > 0, "");
        Check("a last timestamp far past the track is refused",
            PublishValidation.Validate("Song", "Artist", 10, [new LyricLine(TimeSpan.FromSeconds(30), "late")]).Count > 0, "");
        Check("a last timestamp inside the grace is accepted",
            PublishValidation.Validate("Song", "Artist", 10, [new LyricLine(TimeSpan.FromSeconds(14), "late")]).Count == 0, "");
    }

    /// <summary>Subtracts a small amount from a 64-hex number, for a strictly lower threshold.</summary>
    private static string Decrement(string hex, int amount)
    {
        var bytes = Convert.FromHexString(hex);

        for (var i = bytes.Length - 1; i >= 0 && amount > 0; i--)
        {
            if (bytes[i] >= amount)
            {
                bytes[i] = (byte)(bytes[i] - amount);
                amount = 0;
            }
            else
            {
                amount -= bytes[i];
                bytes[i] = 0xFF;
            }
        }

        return Convert.ToHexString(bytes);
    }

    private static void Check(string what, bool ok, object? detail)
    {
        _checks++;
        if (ok)
        {
            Console.WriteLine($"PASS | {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"FAIL | {what}   (got: {detail})");
    }
}
