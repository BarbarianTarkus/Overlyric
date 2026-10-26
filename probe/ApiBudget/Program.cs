using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Overlyric.Lyrics;

namespace Overlyric.ApiBudget;

/// <summary>
/// Measures how many HTTP requests one lyrics resolution really makes, and how many bytes it moves,
/// against a local stub that answers the way lrclib does. Nothing here touches the real service.
/// </summary>
/// <remarks>
/// The scenarios mirror the code paths in <see cref="LyricsService"/> and the two callers in
/// <c>AppHost</c>: the per-track lookup and the version list behind the picker. Each scenario gets a
/// fresh cache directory, so a cached answer is only reused when the scenario asks for it.
/// </remarks>
internal static class Program
{
    private const string Artist = "Gentle Giant";
    private const string Track = "For Nobody";
    private const string Album = "The Missing Piece";
    private const double Duration = 246.4;

    private const string SyncedBody = "[00:46.88] Running away, I'll leave you my address\n[00:50.00] I don't want you to know\n";
    private const string PlainBody = "Running away, I'll leave you my address\nI don't want you to know\n";

    private static readonly List<string> Summary = [];

    private static async Task<int> Main()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "budget-cache-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);

        Console.WriteLine("Overlyric lyric-resolution request budget");
        Console.WriteLine("local stub only; no traffic leaves this machine");
        Console.WriteLine();

        await Scenario(root, "1  cold track, /api/get answers with synced lyrics",
            Reply.Object(Fixtures.Record(101, Duration - 1.656, synced: SyncedBody)),
            Fixtures.SearchArray(20, syncedIndex: -1),
            albumSearchBody: null,
            run: (service, probe) => service.LookupDetailedAsync(Artist, Track, Album, Duration, CancellationToken.None));

        await Scenario(root, "2  cold track, /api/get misses, one search finds the timed sheet",
            Reply.Fault(404),
            Fixtures.SearchArray(20, syncedIndex: 7),
            albumSearchBody: null,
            run: (service, probe) => service.LookupDetailedAsync(Artist, Track, Album, Duration, CancellationToken.None));

        await Scenario(root, "3  cold track, /api/get is untimed, album-scoped search finds timings",
            Reply.Object(Fixtures.Record(301, Duration - 1.656, plain: PlainBody)),
            Fixtures.SearchArray(20, syncedIndex: -1),
            albumSearchBody: Fixtures.SearchArray(1, syncedIndex: 0),
            run: (service, probe) => service.LookupDetailedAsync(Artist, Track, Album, Duration, CancellationToken.None));

        await Scenario(root, "4  cold track, lrclib has nothing at all",
            Reply.Fault(404),
            "[]",
            albumSearchBody: "[]",
            run: async (service, probe) =>
            {
                await service.LookupDetailedAsync(Artist, Track, Album, Duration, CancellationToken.None);
                probe.Mark("lookup");
                await service.GetVersionsAsync(Artist, Track, Album, Duration, CancellationToken.None);
                probe.Mark("picker for a track with no lyrics");
            });

        await Scenario(root, "5  outage, one resolution (503, Retry-After: 1)",
            Reply.Fault(503, retryAfter: 1),
            "[]",
            albumSearchBody: "[]",
            fault: Reply.Fault(503, retryAfter: 1),
            run: async (service, probe) =>
            {
                await service.LookupDetailedAsync(Artist, Track, Album, Duration, CancellationToken.None);
                probe.Mark("outcome");
            });

        await Scenario(root, "6  outage, as AppHost drives it (initial + 2 retries)",
            Reply.Fault(503, retryAfter: 1),
            "[]",
            albumSearchBody: "[]",
            fault: Reply.Fault(503, retryAfter: 1),
            run: async (service, probe) =>
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    await service.LookupDetailedAsync(Artist, Track, Album, Duration, CancellationToken.None);
                    probe.Mark("attempt " + (attempt + 1).ToString(CultureInfo.InvariantCulture));
                }
            });

        await Scenario(root, "7  outage, lookup then the version list",
            Reply.Fault(503, retryAfter: 1),
            "[]",
            albumSearchBody: "[]",
            fault: Reply.Fault(503, retryAfter: 1),
            run: async (service, probe) =>
            {
                await service.LookupDetailedAsync(Artist, Track, Album, Duration, CancellationToken.None);
                probe.Mark("after the lookup");
                await service.GetVersionsAsync(Artist, Track, Album, Duration, CancellationToken.None);
                probe.Mark("after the version list");
            });

        await Scenario(root, "8  replay of a cached track, three lookups",
            Reply.Object(Fixtures.Record(101, Duration - 1.656, synced: SyncedBody)),
            Fixtures.SearchArray(20, syncedIndex: -1),
            albumSearchBody: null,
            run: async (service, probe) =>
            {
                await service.LookupDetailedAsync(Artist, Track, Album, Duration, CancellationToken.None);
                probe.Mark("first lookup");
                await service.LookupDetailedAsync(Artist, Track, Album, Duration, CancellationToken.None);
                await service.LookupDetailedAsync(Artist, Track, Album, Duration, CancellationToken.None);
                probe.Mark("two replays");
            });

        await Scenario(root, "9  version list for a track that already resolved well",
            Reply.Object(Fixtures.Record(101, Duration - 1.656, synced: SyncedBody)),
            Fixtures.SearchArray(20, syncedIndex: -1),
            albumSearchBody: Fixtures.SearchArray(20, syncedIndex: -1),
            run: async (service, probe) =>
            {
                await service.LookupDetailedAsync(Artist, Track, Album, Duration, CancellationToken.None);
                probe.Mark("after the lookup");
                await service.GetVersionsAsync(Artist, Track, Album, Duration, CancellationToken.None);
                probe.Mark("after the version list");
            });

        await Scenario(root, "10 version list opened twice",
            Reply.Object(Fixtures.Record(101, Duration - 1.656, synced: SyncedBody)),
            Fixtures.SearchArray(20, syncedIndex: -1),
            albumSearchBody: Fixtures.SearchArray(20, syncedIndex: -1),
            run: async (service, probe) =>
            {
                await service.LookupDetailedAsync(Artist, Track, Album, Duration, CancellationToken.None);
                probe.Mark("after the lookup");
                await service.GetVersionsAsync(Artist, Track, Album, Duration, CancellationToken.None);
                probe.Mark("after the first picker");
                await service.GetVersionsAsync(Artist, Track, Album, Duration, CancellationToken.None);
                probe.Mark("after the second picker");
            });

        await Scenario(root, "11 pinned version, cold cache",
            Reply.Object(Fixtures.Record(101, Duration - 1.656, synced: SyncedBody)),
            Fixtures.SearchArray(20, syncedIndex: -1),
            albumSearchBody: null,
            run: (service, probe) =>
            {
                service.PinVersion(Artist, Track, Album, 101);
                return service.LookupDetailedAsync(Artist, Track, Album, Duration, CancellationToken.None);
            });

        await Scenario(root, "12 rate limited, 429 with Retry-After: 5",
            Reply.Fault(429, retryAfter: 5),
            "[]",
            albumSearchBody: "[]",
            fault: Reply.Fault(429, retryAfter: 5),
            run: async (service, probe) =>
            {
                await service.LookupDetailedAsync(Artist, Track, Album, Duration, CancellationToken.None);
                probe.Mark("outcome");
            });

        // The good case for the stored list: the track was resolved by searching, so the search
        // answer is already on disk when the next session opens the selector.
        await Scenario(root, "13 version list in the next session, same track",
            Reply.Object(Fixtures.Record(301, Duration - 1.656, plain: PlainBody)),
            Fixtures.SearchArray(20, syncedIndex: -1),
            albumSearchBody: Fixtures.SearchArray(1, syncedIndex: 0),
            run: async (service, probe) =>
            {
                await service.LookupDetailedAsync(Artist, Track, Album, Duration, CancellationToken.None);
                probe.Mark("first session");
                var next = probe.Restart();
                await next.GetVersionsAsync(Artist, Track, Album, Duration, CancellationToken.None);
                probe.Mark("picker in the next session");
            });

        // An edition suffix on the album: the verbatim name misses, the cleaned name answers. The
        // second exact request is what buys the fix, so the budget records it as its own path.
        await Scenario(root, "14 cold track, album carries an edition suffix",
            Reply.Object(Fixtures.Record(101, Duration - 1.656, synced: SyncedBody)),
            "[]",
            albumSearchBody: null,
            run: (service, probe) => service.LookupDetailedAsync(
                Artist,
                Track,
                Album + " (2007 Stereo Mix)",
                Duration,
                CancellationToken.None),
            getOverride: request => request.Contains("%282007%20Stereo%20Mix%29", StringComparison.OrdinalIgnoreCase)
                ? Reply.Fault(404)
                : null);

        Console.WriteLine("Summary");
        foreach (var line in Summary)
        {
            Console.WriteLine("  " + line);
        }

        Console.WriteLine();
        Console.WriteLine("Bytes are decoded JSON sizes; lrclib serves gzip, so the wire cost is smaller.");

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception)
        {
        }

        return 0;
    }

    private static async Task Scenario(
        string root,
        string name,
        Reply getReply,
        string searchBody,
        string? albumSearchBody,
        Func<LyricsService, Probe, Task> run,
        Reply? fault = null,
        Func<string, Reply?>? getOverride = null)
    {
        var directory = Path.Combine(
            root,
            name[..2].Trim() + "-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);

        await using var stub = new StubServer
        {
            Handler = request =>
            {
                if (fault is { } failure)
                {
                    return failure;
                }

                if (getOverride?.Invoke(request) is { } replaced)
                {
                    return replaced;
                }

                if (request.Contains("/api/search", StringComparison.OrdinalIgnoreCase))
                {
                    var albumScoped = request.Contains("album_name=", StringComparison.OrdinalIgnoreCase);
                    return new Reply(200, albumScoped ? albumSearchBody ?? searchBody : searchBody);
                }

                return getReply;
            }
        };

        using var client = new LrclibClient("Overlyric/0.1.0 (https://github.com/overlyric/overlyric)", stub.BaseUrl);
        var service = new LyricsService(client, new LyricsCache(directory));
        var probe = new Probe(stub, directory);

        Console.WriteLine(name);

        var stopwatch = Stopwatch.StartNew();
        await run(service, probe);
        stopwatch.Stop();

        var requests = stub.Requests;
        var breakdown = requests
            .GroupBy(r => r.Endpoint, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key + " x" + g.Count().ToString(CultureInfo.InvariantCulture));

        Console.WriteLine(
            "      total " + requests.Count.ToString(CultureInfo.InvariantCulture) + " request(s), "
            + stub.Bytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes, "
            + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms"
            + (requests.Count > 0 ? "  [" + string.Join(", ", breakdown) + "]" : ""));

        if (requests.Count > 1)
        {
            var gaps = new List<string>();
            for (var i = 1; i < requests.Count; i++)
            {
                gaps.Add((requests[i].At - requests[i - 1].At).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms");
            }

            Console.WriteLine("      gaps: " + string.Join(", ", gaps));
        }

        Console.WriteLine();
        Summary.Add(
            name.PadRight(58) + requests.Count.ToString(CultureInfo.InvariantCulture).PadLeft(3) + " req  "
            + stub.Bytes.ToString("N0", CultureInfo.InvariantCulture).PadLeft(9) + " B");
    }
}

/// <summary>Marks moments inside a scenario, and reports how many requests have happened by then.</summary>
internal sealed class Probe(StubServer stub, string directory)
{
    public void Mark(string label)
    {
        Console.WriteLine(
            "      ~ " + label + ": " + stub.Requests.Count.ToString(CultureInfo.InvariantCulture) + " request(s) so far");
    }

    /// <summary>
    /// A brand-new service over the same cache directory and stub: the next session, with everything
    /// the process remembered forgotten and everything the disk remembered intact.
    /// </summary>
    public LyricsService Restart()
    {
        var client = new LrclibClient("Overlyric/0.1.0 (https://github.com/overlyric/overlyric)", stub.BaseUrl);
        return new LyricsService(client, new LyricsCache(directory));
    }
}

/// <summary>One answer the stub gives.</summary>
internal readonly record struct Reply(int Status, string Body, int RetryAfterSeconds = 0)
{
    public static Reply Object(string json) => new(200, json);

    public static Reply Fault(int status, int retryAfter = 0) => new(status, "{}", retryAfter);
}

/// <summary>One request the stub served.</summary>
internal sealed record RecordedRequest(string Line, string Endpoint, DateTime At, int Bytes);

/// <summary>A bare TCP HTTP responder with a request log.</summary>
internal sealed class StubServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly List<RecordedRequest> _requests = [];
    private readonly Lock _gate = new();

    public StubServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BaseUrl = string.Create(
            CultureInfo.InvariantCulture,
            $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public Func<string, Reply> Handler { get; init; } = _ => Reply.Fault(404);

    public string BaseUrl { get; }

    public int Bytes { get; private set; }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
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
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient connection;

            try
            {
                connection = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = RespondAsync(connection);
        }
    }

    private async Task RespondAsync(TcpClient connection)
    {
        try
        {
            using (connection)
            using (var stream = connection.GetStream())
            {
                var buffer = new byte[16384];
                var read = await stream.ReadAsync(buffer, _cts.Token);
                var text = Encoding.ASCII.GetString(buffer, 0, read);
                var line = text.Split("\r\n", 2)[0];

                var reply = Handler(text);
                var body = Encoding.UTF8.GetBytes(reply.Body);

                lock (_gate)
                {
                    _requests.Add(new RecordedRequest(line, EndpointOf(line), DateTime.UtcNow, body.Length));
                    Bytes += body.Length;
                }

                var header =
                    string.Create(CultureInfo.InvariantCulture, $"HTTP/1.1 {reply.Status} {Reason(reply.Status)}\r\n")
                    + "Content-Type: application/json\r\n"
                    + string.Create(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n")
                    + (reply.RetryAfterSeconds > 0
                        ? string.Create(CultureInfo.InvariantCulture, $"Retry-After: {reply.RetryAfterSeconds}\r\n")
                        : string.Empty)
                    + "Connection: close\r\n\r\n";

                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _cts.Token);
                await stream.WriteAsync(body, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>The question a request asked, which is what a budget is counted in.</summary>
    private static string EndpointOf(string line)
    {
        if (line.Contains("/api/search", StringComparison.OrdinalIgnoreCase))
        {
            return line.Contains("album_name=", StringComparison.OrdinalIgnoreCase)
                ? "search+album"
                : "search";
        }

        if (line.Contains("/api/get/", StringComparison.OrdinalIgnoreCase))
        {
            return "get/{id}";
        }

        if (line.Contains("/api/get", StringComparison.OrdinalIgnoreCase))
        {
            return line.Contains("album_name=", StringComparison.OrdinalIgnoreCase) ? "get+album" : "get-noalbum";
        }

        return "other";
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        404 => "Not Found",
        429 => "Too Many Requests",
        503 => "Service Unavailable",
        _ => "Status"
    };
}

internal static class Fixtures
{
    public static string Record(
        long id,
        double duration,
        string? plain = null,
        string? synced = null,
        string album = "The Missing Piece")
    {
        return new StringBuilder()
            .Append("{\"id\":").Append(id.ToString(CultureInfo.InvariantCulture))
            .Append(",\"trackName\":").Append(JsonSerializer.Serialize("For Nobody"))
            .Append(",\"artistName\":").Append(JsonSerializer.Serialize("Gentle Giant"))
            .Append(",\"albumName\":").Append(JsonSerializer.Serialize(album))
            .Append(",\"duration\":").Append(duration.ToString("0.######", CultureInfo.InvariantCulture))
            .Append(",\"instrumental\":false,\"hasWordSync\":false")
            .Append(",\"plainLyrics\":").Append(plain is null ? "null" : JsonSerializer.Serialize(plain))
            .Append(",\"syncedLyrics\":").Append(synced is null ? "null" : JsonSerializer.Serialize(synced))
            .Append('}')
            .ToString();
    }

    /// <summary>A twenty-record search answer, lrclib's hard cap.</summary>
    public static string SearchArray(int count, int syncedIndex)
    {
        var records = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            var duration = 244.0 + (i * 0.2);
            var synced = i == syncedIndex
                ? "[00:46.88] Running away\n[00:50.00] I don't want you to know\n"
                : null;

            records.Add(Record(500 + i, duration, plain: "plain words", synced: synced));
        }

        return "[" + string.Join(",", records) + "]";
    }
}