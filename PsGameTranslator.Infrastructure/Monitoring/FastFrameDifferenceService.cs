using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PsGameTranslator.Infrastructure.Monitoring;

/// <summary>
/// Cheap change detector for the subtitle crop. Downscales both images to a
/// tiny grayscale grid and compares average pixel difference — this must be
/// far faster than OCR so the capture loop can run every ~120 ms without
/// blocking on the OCR server. Average brightness difference naturally also
/// catches fade-in/fade-out transitions, since those ramp pixel values
/// gradually rather than changing them instantly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FastFrameDifferenceService
{
    public const int DefaultTargetWidth = 160;
    public const int DefaultTargetHeight = 40;

    /// <summary>Returns a 0-100 difference percentage; 100 when the images
    /// cannot be compared (decode failure, so callers should treat as changed).</summary>
    public double ComputeDifferencePercent(
        byte[] previousPngBytes,
        byte[] currentPngBytes,
        int targetWidth = DefaultTargetWidth,
        int targetHeight = DefaultTargetHeight)
    {
        var previous = DownscaleToGrayscale(previousPngBytes, targetWidth, targetHeight);
        var current = DownscaleToGrayscale(currentPngBytes, targetWidth, targetHeight);
        if (previous is null || current is null || previous.Length != current.Length)
            return 100;

        long totalDifference = 0;
        for (var i = 0; i < previous.Length; i++)
            totalDifference += Math.Abs(previous[i] - current[i]);

        var averageDifference = totalDifference / (double)previous.Length; // 0-255 scale
        return averageDifference / 255.0 * 100.0;
    }

    private static byte[]? DownscaleToGrayscale(byte[] pngBytes, int width, int height)
    {
        try
        {
            using var inputStream = new MemoryStream(pngBytes);
            using var source = new Bitmap(inputStream);
            using var small = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            using (var graphics = Graphics.FromImage(small))
            {
                // Speed over quality — this is only a change detector, not OCR input.
                graphics.InterpolationMode = InterpolationMode.Low;
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                graphics.DrawImage(source, 0, 0, width, height);
            }

            var pixels = new byte[width * height];
            var rect = new Rectangle(0, 0, width, height);
            var data = small.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[Math.Abs(data.Stride)];
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    for (var x = 0; x < width; x++)
                    {
                        var index = x * 3;
                        var blue = row[index];
                        var green = row[index + 1];
                        var red = row[index + 2];
                        pixels[y * width + x] =
                            (byte)Math.Clamp(red * 0.299 + green * 0.587 + blue * 0.114, 0, 255);
                    }
                }
            }
            finally
            {
                small.UnlockBits(data);
            }

            return pixels;
        }
        catch
        {
            return null;
        }
    }
}
