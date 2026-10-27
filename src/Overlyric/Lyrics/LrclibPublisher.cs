using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Overlyric.Lyrics;

/// <summary>
/// Publishes one sheet to LRCLIB, with the proof-of-work challenge the API asks for. There is no
/// automatic publish anywhere: the caller has already shown the exact payload and the listener has
/// confirmed it, and this class sends exactly one request per confirmed action.
/// </summary>
/// <remarks>
/// The contract was verified against the live documentation and the server source: a challenge is
/// <c>POST /api/request-challenge</c> → <c>{prefix,target}</c>; the token is
/// <c>X-Publish-Token: prefix:decimalNonce</c> where SHA-256(prefix + nonce) is at or below the
/// target; the token is single-use and expires after five minutes; <c>POST /api/publish</c> answers
/// 201 on success. A failed send is never retried with the same token - the token is consumed before
/// the write, so a retry would at best burn a new solve and at worst create a duplicate.
/// </remarks>
internal sealed class LrclibPublisher : IDisposable
{
    private static readonly Uri RequestChallengeUri = new("https://lrclib.net/api/request-challenge");
    private static readonly Uri PublishUri = new("https://lrclib.net/api/publish");

    private readonly HttpClient _http;

    public LrclibPublisher(HttpClient? http = null)
    {
        _http = http ?? CreateClient();
    }

    public async Task<(PublishOutcome Outcome, string Message)> PublishAsync(
        PublishRequest request,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        PublishChallenge challenge;

        try
        {
            using var challengeResponse = await _http
                .PostAsync(RequestChallengeUri, content: null, cancellationToken)
                .ConfigureAwait(false);

            var challengeProblem = Describe(challengeResponse);

            if (challengeProblem is not null)
            {
                return challengeProblem.Value;
            }

            var json = await challengeResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!LrclibChallenge.TryParse(json, out var parsed, out var parseError))
            {
                return (PublishOutcome.ServerError, parseError ?? "LRCLIB sent an unreadable challenge.");
            }

            challenge = parsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (PublishOutcome.Cancelled, "Publishing was cancelled.");
        }
        catch (Exception)
        {
            return (PublishOutcome.NetworkFailed, "Could not reach LRCLIB.");
        }

        var nonce = await Task.Run(
            () => LrclibChallenge.Solve(challenge, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (nonce is null)
        {
            return cancellationToken.IsCancellationRequested
                ? (PublishOutcome.Cancelled, "Publishing was cancelled.")
                : (PublishOutcome.ServerError, "The challenge could not be solved.");
        }

        try
        {
            using var content = new StringContent(request.ToJson(), Encoding.UTF8, "application/json");
            using var publish = new HttpRequestMessage(HttpMethod.Post, PublishUri) { Content = content };
            publish.Headers.Add("X-Publish-Token", string.Concat(challenge.Prefix, ":", nonce));

            using var response = await _http.SendAsync(publish, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Created)
            {
                return (PublishOutcome.Published, "Published to LRCLIB.");
            }

            return Describe(response) ?? (PublishOutcome.ServerError, "LRCLIB refused the publish.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (PublishOutcome.Cancelled, "Publishing was cancelled.");
        }
        catch (Exception)
        {
            return (PublishOutcome.NetworkFailed, "The publish request could not be completed.");
        }
    }

    private static (PublishOutcome Outcome, string Message)? Describe(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return null;
        }

        var status = response.StatusCode;

        if (status == HttpStatusCode.TooManyRequests)
        {
            return (PublishOutcome.RateLimited, "LRCLIB is rate-limiting this client; wait a minute and try again.");
        }

        if (status == HttpStatusCode.ServiceUnavailable)
        {
            return (PublishOutcome.ServerBusy, "LRCLIB is overloaded right now; try again later.");
        }

        if (status == HttpStatusCode.BadRequest)
        {
            var message = ReadErrorMessage(response);

            return (PublishOutcome.TokenRejected,
                message is null
                    ? "LRCLIB rejected the publish request."
                    : string.Concat("LRCLIB rejected the publish request: ", message));
        }

        return (PublishOutcome.ServerError, string.Concat("LRCLIB answered ", (int)status, " ", response.ReasonPhrase, "."));
    }

    private static string? ReadErrorMessage(HttpResponseMessage response)
    {
        try
        {
            var text = response.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();

            if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{'))
            {
                return null;
            }

            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("message", out var message) ? message.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            // The assembly version is the single copy of the product identity; the fallback only
            // exists so a metadata-less host cannot throw, and must never become a second version.
            string.Create(CultureInfo.InvariantCulture, $"Overlyric/{typeof(LrclibPublisher).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"} (+https://github.com/overlyric/overlyric)"));
        return client;
    }
}
