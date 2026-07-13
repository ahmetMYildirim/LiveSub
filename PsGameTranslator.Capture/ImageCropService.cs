using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Capture;

public sealed class ImageCropService : IImageCropService
{
    private readonly ILogger<ImageCropService> _logger;

    public ImageCropService(ILogger<ImageCropService> logger)
    {
        _logger = logger;
    }

    public Task<byte[]> CropAsync(
        string imagePath,
        CaptureRegion region,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var source = new Bitmap(imagePath);
            return CropBitmap(source, region, imagePath);
        }, cancellationToken);

    public Task<byte[]> CropAsync(
        byte[] imageBytes,
        CaptureRegion region,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var ms = new MemoryStream(imageBytes);
            using var source = new Bitmap(ms);
            return CropBitmap(source, region, "<memory>");
        }, cancellationToken);

    private byte[] CropBitmap(Bitmap source, CaptureRegion region, string sourceName)
    {
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(region),
                $"Region has invalid dimensions: {region.Width}×{region.Height}.");

        // Clamp the crop rectangle to the image bounds instead of failing.
        var x = Math.Clamp(region.X, 0, source.Width - 1);
        var y = Math.Clamp(region.Y, 0, source.Height - 1);
        var w = Math.Clamp(region.Width,  1, source.Width  - x);
        var h = Math.Clamp(region.Height, 1, source.Height - y);
        var rect = new Rectangle(x, y, w, h);

        if (x != region.X || y != region.Y || w != region.Width || h != region.Height)
            _logger.LogWarning(
                "Crop region ({RX},{RY}) {RW}×{RH} clamped to ({X},{Y}) {W}×{H} for image {IW}×{IH}",
                region.X, region.Y, region.Width, region.Height,
                x, y, w, h, source.Width, source.Height);

        using var cropped = source.Clone(rect, source.PixelFormat);
        using var stream = new MemoryStream();
        cropped.Save(stream, ImageFormat.Png);

        _logger.LogInformation(
            "Cropped region ({X},{Y}) {Width}×{Height} from {Source}",
            rect.X, rect.Y, rect.Width, rect.Height, sourceName);

        return stream.ToArray();
    }
}
