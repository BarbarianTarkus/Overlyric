using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Overlyric.UiTests;

/// <summary>
/// One captured frame as tightly packed BGRA bytes. Keeping the pixels in a byte array rather than
/// a Bitmap makes the comparison and the diff image independent of GDI+ state.
/// </summary>
internal sealed class Frame
{
    private Frame(int width, int height, byte[] bgra, bool printWindowReported)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
        PrintWindowReported = printWindowReported;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Bgra { get; }

    /// <summary>What PrintWindow returned; informational, because the pixels are the real answer.</summary>
    public bool PrintWindowReported { get; }

    /// <summary>Builds a frame from an existing pixel buffer, for the diff image.</summary>
    internal static Frame Create(int width, int height, byte[] bgra) => new(width, height, bgra, true);

    /// <summary>True when every pixel is the same colour, which means nothing was drawn.</summary>
    public bool IsBlank
    {
        get
        {
            if (Bgra.Length < 4)
            {
                return true;
            }

            var blue = Bgra[0];
            var green = Bgra[1];
            var red = Bgra[2];

            for (var i = 4; i < Bgra.Length; i += 4)
            {
                if (Bgra[i] != blue || Bgra[i + 1] != green || Bgra[i + 2] != red)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Captures the window's client area with PrintWindow(PW_RENDERFULLCONTENT). The retry loop
    /// exists because a DirectComposition surface that has just been shown can answer the first
    /// PrintWindow with nothing while the compositor is still committing the frame.
    /// </summary>
    public static Frame Capture(nint window, int attempts = 3)
    {
        if (!NativeMethods.GetClientRect(window, out var client) || client.Width <= 0 || client.Height <= 0)
        {
            throw new ScenarioFailureException("GetClientRect failed: the window has no client area.");
        }

        Frame? frame = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            frame = CaptureOnce(window, client.Width, client.Height);

            if (!frame.IsBlank)
            {
                return frame;
            }

            if (attempt < attempts)
            {
                Thread.Sleep(150);
            }
        }

        return frame!;
    }

    private static Frame CaptureOnce(nint window, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        bool printed;

        using (var graphics = Graphics.FromImage(bitmap))
        {
            // PrintWindow is not obliged to cover every pixel; a cleared background makes an
            // unrendered corner visible as black instead of as leftover memory.
            graphics.Clear(Color.Black);
            var hdc = graphics.GetHdc();

            try
            {
                printed = NativeMethods.PrintWindow(window, hdc, NativeMethods.PW_RENDERFULLCONTENT);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        return new Frame(width, height, ReadPixels(bitmap), printed);
    }

    private static byte[] ReadPixels(Bitmap bitmap)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            var rowBytes = bitmap.Width * 4;
            var pixels = new byte[rowBytes * bitmap.Height];

            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), pixels, y * rowBytes, rowBytes);
            }

            // A PNG written from a DirectComposition capture can otherwise carry a zero alpha and
            // render as a transparent rectangle in an image viewer.
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }

            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var bounds = new Rectangle(0, 0, Width, Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            var rowBytes = Width * 4;

            for (var y = 0; y < Height; y++)
            {
                Marshal.Copy(Bgra, y * rowBytes, data.Scan0 + (y * data.Stride), rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    public static Frame Load(string path)
    {
        using var source = new Bitmap(path);

        // Normalising through a 32bpp surface keeps a 24bpp baseline and a 32bpp capture comparable
        // byte for byte, without depending on the encoder's choice.
        using var normalised = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(normalised))
        {
            graphics.DrawImageUnscaled(source, 0, 0);
        }

        return new Frame(source.Width, source.Height, ReadPixels(normalised), true);
    }
}
