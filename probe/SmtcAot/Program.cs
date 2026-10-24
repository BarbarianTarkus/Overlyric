using Windows.Media.Control;

namespace SmtcAot;

/// <summary>
/// Minimal NativeAOT compatibility check: does the Windows SDK WinRT projection
/// (Windows.Media.Control) survive PublishAot without the CLR's dynamic WinRT support?
/// </summary>
internal static class Program
{
    private static async Task Main()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var session = manager.GetCurrentSession();
        if (session is null)
        {
            Console.WriteLine("AOT_OK no-current-session");
            return;
        }

        var props = await session.TryGetMediaPropertiesAsync();
        var timeline = session.GetTimelineProperties();

        Console.WriteLine($"AOT_OK {session.SourceAppUserModelId}");
        Console.WriteLine($"  title  : {props?.Title}");
        Console.WriteLine($"  artist : {props?.Artist}");
        Console.WriteLine($"  album  : {props?.AlbumTitle}");
        Console.WriteLine($"  end    : {timeline.EndTime}");
    }
}
