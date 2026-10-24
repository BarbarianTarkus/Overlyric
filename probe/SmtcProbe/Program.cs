using System.Diagnostics;
using Windows.Media.Control;

namespace SmtcProbe;

/// <summary>
/// Measures how (and whether) a player actually updates the SMTC timeline.
/// This determines if synced lyrics can trust SMTC's position or must self-clock.
/// </summary>
internal static class Program
{
    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 90;

        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var session = manager.GetCurrentSession();
        if (session is null)
        {
            Console.WriteLine("NO_SESSION");
            return;
        }

        Console.WriteLine($"WATCH {session.SourceAppUserModelId} for {seconds}s");

        var sw = Stopwatch.StartNew();
        string lastTitle = "", lastArtist = "";
        TimeSpan lastPos = TimeSpan.MinValue;
        DateTimeOffset lastUpd = default;
        var lastStatus = (GlobalSystemMediaTransportControlsSessionPlaybackStatus)(-1);
        var lastHeartbeat = TimeSpan.Zero;

        int posChanges = 0, updChanges = 0, statusChanges = 0, trackChanges = 0;
        int tlEvents = 0, mpEvents = 0, pbEvents = 0;
        session.TimelinePropertiesChanged += (_, _) => Interlocked.Increment(ref tlEvents);
        session.MediaPropertiesChanged += (_, _) => Interlocked.Increment(ref mpEvents);
        session.PlaybackInfoChanged += (_, _) => Interlocked.Increment(ref pbEvents);

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var pb = session.GetPlaybackInfo();
            var tl = session.GetTimelineProperties();
            var props = await session.TryGetMediaPropertiesAsync();
            var title = props?.Title ?? "";
            var artist = props?.Artist ?? "";

            if (tl.Position != lastPos)
            {
                posChanges++;
                Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,7:0.00}s] POSITION {lastPos} -> {tl.Position}");
                lastPos = tl.Position;
            }

            if (tl.LastUpdatedTime != lastUpd)
            {
                updChanges++;
                Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,7:0.00}s] UPDATED  {lastUpd:HH:mm:ss.fff} -> {tl.LastUpdatedTime:HH:mm:ss.fff}");
                lastUpd = tl.LastUpdatedTime;
            }

            if (pb.PlaybackStatus != lastStatus)
            {
                statusChanges++;
                Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,7:0.00}s] STATUS   {lastStatus} -> {pb.PlaybackStatus}");
                lastStatus = pb.PlaybackStatus;
            }

            if (title != lastTitle || artist != lastArtist)
            {
                trackChanges++;
                Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,7:0.00}s] TRACK    '{lastArtist} - {lastTitle}' -> '{artist} - {title}'");
                lastTitle = title;
                lastArtist = artist;
            }

            if (sw.Elapsed - lastHeartbeat >= TimeSpan.FromSeconds(20))
            {
                lastHeartbeat = sw.Elapsed;
                Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,7:0.00}s] heartbeat pos={tl.Position} upd={tl.LastUpdatedTime:HH:mm:ss} status={pb.PlaybackStatus} events(tl={tlEvents},mp={mpEvents},pb={pbEvents})");
            }

            await Task.Delay(200);
        }

        Console.WriteLine($"DONE posChanges={posChanges} updChanges={updChanges} statusChanges={statusChanges} trackChanges={trackChanges} events tl={tlEvents} mp={mpEvents} pb={pbEvents}");
    }
}
