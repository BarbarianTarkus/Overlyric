using System.Text.Json;
using Overlyric.App;

namespace Overlyric.DiagnosticsTests;

/// <summary>
/// Checks the rolling log (bounded memory, bounded file, rotation, format) and the diagnostics
/// snapshot's JSON: the fields the window promises, and that a title with quotes/newlines cannot
/// produce invalid JSON.
/// </summary>
internal static class Program
{
    private static int _checks;
    private static int _failures;
    private static string _root = "";

    private static int Main()
    {
        _root = Path.Combine(Path.GetTempPath(), "OverlyricDiagnosticsTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        try
        {
            LogChecks();
            FileChecks();
            SnapshotChecks();
        }
        finally
        {
            try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ALL {_checks} CHECKS PASSED"
            : $"{_failures} OF {_checks} CHECK(S) FAILED");

        return _failures == 0 ? 0 : 1;
    }

    private static void LogChecks()
    {
        var log = new DiagnosticLog();
        log.Add("track", "first");
        log.Add("lookup", "second");

        Check("the log keeps what was added", log.Count == 2, log.Count);
        Check("the newest line is last", log.Recent(1).Single().EndsWith("second", StringComparison.Ordinal), log.Recent(1).Single());
        Check("recent lines are oldest first", log.Recent(2).Count == 2 && log.Recent(2)[0].Contains("first", StringComparison.Ordinal), string.Join("|", log.Recent(2)));
        Check("a dump is one line per entry", log.Dump().Split(Environment.NewLine).Length == 2, log.Dump());
        Check("blank categories and messages are dropped", Ignore(log), log.Count);

        for (var i = 0; i < 300; i++)
        {
            log.Add("ring", "line " + i);
        }

        Check("the in-memory ring is bounded", log.Count == DiagnosticLog.Capacity, log.Count);
        Check("the ring keeps the newest lines", log.Recent(1).Single().EndsWith("line 299", StringComparison.Ordinal), log.Recent(1).Single());
        Check("the ring never returns more than it holds", log.Recent(1000).Count == DiagnosticLog.Capacity, log.Recent(1000).Count);
        Check("the log lines carry a time and a category",
            log.Recent(1).Single().Contains("ring:", StringComparison.Ordinal)
            && log.Recent(1).Single().Length > 12,
            log.Recent(1).Single());
    }

    private static bool Ignore(DiagnosticLog log)
    {
        var before = log.Count;
        log.Add("", "x");
        log.Add(" ", "x");
        log.Add("x", "");
        log.Add("x", "  ");
        return log.Count == before;
    }

    private static void FileChecks()
    {
        var path = Path.Combine(_root, "diagnostics.log");
        var log = new DiagnosticLog(path);
        log.Add("file", "one");

        Check("the file is written", File.Exists(path) && File.ReadAllText(path).Contains("one", StringComparison.Ordinal), "");

        var big = new string('x', 1024);

        for (var i = 0; i < 400; i++)
        {
            log.Add("rotate", big);
        }

        var size = new FileInfo(path).Length;
        Check("the log file rotates instead of growing without bound", size <= DiagnosticLog.MaxBytes * 2, size);
        Check("the file still has readable content", File.ReadAllLines(path).Length > 0, size);
    }

    private static void SnapshotChecks()
    {
        var snapshot = new DiagnosticsSnapshot
        {
            CapturedAt = DateTimeOffset.Parse("2026-10-27T00:00:00+01:00"),
            Version = "0.3.3",
            HasSession = true,
            SourceApp = "TIDAL",
            Title = "A \"quoted\" title\nwith a newline",
            Artist = "Artist",
            Album = "Album",
            IsPlaying = true,
            DurationSeconds = 300,
            Position = "1:23.45",
            ClockIsLive = false,
            PositionIsUsable = true,
            SeekCount = 2,
            LyricSource = "Local",
            RecordId = null,
            LineCount = 42,
            Synced = true,
            WordSynced = false,
            Instrumental = false,
            LyricStatus = null,
            CacheEntries = 3,
            CacheBytes = 1024,
            LocalFiles = 1,
            LocalBytes = 2048,
            OffsetMs = 250,
            OffsetLabel = "+0.25 s",
            OffsetSource = "this track",
            OutputDevice = "Speakers",
            OutputBluetooth = false,
            ForegroundApp = "game.exe",
            HiddenForApp = false,
            FullscreenNotice = true,
            Log = ["00:00:00.000 track: one"]
        };

        var json = snapshot.ToJson();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Check("the snapshot is valid JSON", root.ValueKind == JsonValueKind.Object, json.Length);
        Check("the session fields are present",
            root.GetProperty("SourceApp").GetString() == "TIDAL"
            && root.GetProperty("IsPlaying").GetBoolean()
            && root.GetProperty("Position").GetString() == "1:23.45", "");
        Check("a title with quotes and a newline round-trips",
            root.GetProperty("Title").GetString() == snapshot.Title, root.GetProperty("Title").GetString());
        Check("the lyric source and offset are named",
            root.GetProperty("LyricSource").GetString() == "Local"
            && root.GetProperty("OffsetLabel").GetString() == "+0.25 s"
            && root.GetProperty("OffsetSource").GetString() == "this track", "");
        Check("presence facts are included",
            root.GetProperty("ForegroundApp").GetString() == "game.exe"
            && root.GetProperty("FullscreenNotice").GetBoolean(), "");
        Check("the log is an array", root.GetProperty("Log").GetArrayLength() == 1, root.GetProperty("Log").GetArrayLength());
        Check("the snapshot is pretty-printed for a bug report", json.Contains(Environment.NewLine, StringComparison.Ordinal), "");
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
