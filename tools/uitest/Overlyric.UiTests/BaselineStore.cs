namespace Overlyric.UiTests;

/// <summary>A rectangle in DIP client coordinates where a capture is allowed to differ.</summary>
internal readonly record struct MaskRect(float X, float Y, float Width, float Height);

internal readonly record struct CompareResult(bool Ok, string Message, int Differing, int Allowed);

/// <summary>
/// The pixel comparison between a capture and its committed baseline.
/// </summary>
/// <remarks>
/// The rule is deliberately blunt: a pixel counts as different when any colour channel moves by
/// more than 8, and the step fails when more than max(32, 0.02 %) of the unmasked pixels differ.
/// Eight leaves room for the subpixel jitter of text rasterisation without hiding a real change,
/// and the floor of 32 stops an 8-bit rounding in one corner from failing an otherwise identical
/// frame. Masked rectangles are excluded entirely and are documented at the scenario that owns them.
/// </remarks>
internal static class BaselineStore
{
    private const int ChannelTolerance = 8;
    private const int MinimumAllowedDifferences = 32;
    private const double AllowedFraction = 0.0002;

    public static void Save(Frame frame, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        frame.Save(path);
    }

    public static CompareResult Compare(
        string baselinePath,
        Frame frame,
        IReadOnlyList<MaskRect> masks,
        float dipWidth,
        string diffPath)
    {
        if (!File.Exists(baselinePath))
        {
            return new CompareResult(false, $"baseline '{baselinePath}' is missing; run with --update to create it.", 0, 0);
        }

        var baseline = Frame.Load(baselinePath);

        if (baseline.Width != frame.Width || baseline.Height != frame.Height)
        {
            return new CompareResult(
                false,
                $"baseline is {baseline.Width}x{baseline.Height} but the capture is {frame.Width}x{frame.Height} px; the layout or the DPI seam changed.",
                0,
                0);
        }

        // Mask coordinates are DIP, like every coordinate in the scenarios; the capture scale is
        // derived from the frame width rather than assumed, so a future DPI change fails loudly in
        // the size check above instead of silently misplacing a mask.
        var scale = dipWidth > 0 ? frame.Width / dipWidth : 1f;
        var masked = new bool[frame.Width * frame.Height];

        foreach (var mask in masks)
        {
            var left = Math.Clamp((int)MathF.Round(mask.X * scale), 0, frame.Width);
            var top = Math.Clamp((int)MathF.Round(mask.Y * scale), 0, frame.Height);
            var right = Math.Clamp((int)MathF.Round((mask.X + mask.Width) * scale), 0, frame.Width);
            var bottom = Math.Clamp((int)MathF.Round((mask.Y + mask.Height) * scale), 0, frame.Height);

            for (var y = top; y < bottom; y++)
            {
                var row = y * frame.Width;

                for (var x = left; x < right; x++)
                {
                    masked[row + x] = true;
                }
            }
        }

        var unmasked = 0;
        var differing = 0;

        for (var pixel = 0; pixel < masked.Length; pixel++)
        {
            if (masked[pixel])
            {
                continue;
            }

            unmasked++;

            var offset = pixel * 4;

            if (Math.Abs(frame.Bgra[offset] - baseline.Bgra[offset]) > ChannelTolerance
                || Math.Abs(frame.Bgra[offset + 1] - baseline.Bgra[offset + 1]) > ChannelTolerance
                || Math.Abs(frame.Bgra[offset + 2] - baseline.Bgra[offset + 2]) > ChannelTolerance)
            {
                differing++;
            }
        }

        var allowed = Math.Max(MinimumAllowedDifferences, (int)(unmasked * AllowedFraction));

        if (differing <= allowed)
        {
            return new CompareResult(
                true,
                $"matches the baseline ({differing} of {unmasked} unmasked pixels differ, {allowed} allowed)",
                0,
                0);
        }

        WriteDiff(frame, baseline, masked, differing, diffPath);

        return new CompareResult(
            false,
            $"{differing} of {unmasked} unmasked pixels differ ({allowed} allowed); diff written to '{diffPath}'",
            differing,
            allowed);
    }

    /// <summary>
    /// A reviewable diff: the capture itself with every failing pixel painted red. Painting on the
    /// capture rather than subtracting the images keeps the screenshot recognisable, so a reviewer
    /// can see which control changed instead of staring at a black rectangle.
    /// </summary>
    private static void WriteDiff(Frame frame, Frame baseline, bool[] masked, int differing, string path)
    {
        var pixels = (byte[])frame.Bgra.Clone();

        for (var pixel = 0; pixel < masked.Length; pixel++)
        {
            if (masked[pixel])
            {
                continue;
            }

            var offset = pixel * 4;

            if (Math.Abs(pixels[offset] - baseline.Bgra[offset]) > ChannelTolerance
                || Math.Abs(pixels[offset + 1] - baseline.Bgra[offset + 1]) > ChannelTolerance
                || Math.Abs(pixels[offset + 2] - baseline.Bgra[offset + 2]) > ChannelTolerance)
            {
                pixels[offset] = 0;
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 255;
                pixels[offset + 3] = 255;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var diff = FrameFactory.FromPixels(frame.Width, frame.Height, pixels);
        diff.Save(path);
    }
}

/// <summary>Builds a frame from a pixel buffer that did not come from PrintWindow.</summary>
internal static class FrameFactory
{
    public static Frame FromPixels(int width, int height, byte[] bgra)
    {
        // Frame's constructor is private because a capture and a diff frame are not the same thing;
        // the comparison needs one constructor entry point and this is it.
        return Frame.Create(width, height, bgra);
    }
}
