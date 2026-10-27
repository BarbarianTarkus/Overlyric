using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SmtcAot;

/// <summary>
/// Minimal NativeAOT compatibility check: does the Windows SDK WinRT projection
/// (Windows.Media.Control) survive PublishAot without the CLR's dynamic WinRT support?
/// </summary>
internal static class Program
{
    private static async Task Main()
    {
        await ArtworkProbeAsync();

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

    /// <summary>
    /// Proves that Windows.Graphics.Imaging survives PublishAot, which is what album art needs.
    /// The decoder is fed a real (1x1) PNG in memory, so the result does not depend on a player.
    /// </summary>
    private static async Task ArtworkProbeAsync()
    {
        try
        {
            // A 1x1 PNG: the smallest real image that exercises the whole decode path.
            var png = Convert.FromHexString(
                "89504E470D0A1A0A0000000D49484452000000010000000108060000001F15C489"
                + "0000000D4944415478DA63F8FFFF3F0005FE02FE0DEF4B6E0000000049454E44AE426082");

            using var stream = new InMemoryRandomAccessStream();

            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = 1, ScaledHeight = 1 },
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            Console.WriteLine($"AOT_ART_OK {decoder.PixelWidth}x{decoder.PixelHeight} bytes={pixels.DetachPixelData().Length}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"AOT_ART_FAIL {exception.GetType().Name}: {exception.Message}");
        }
    }
}
