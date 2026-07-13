using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Capture;

public interface IImageCropService
{
    Task<byte[]> CropAsync(
        string imagePath,
        CaptureRegion region,
        CancellationToken cancellationToken = default);

    Task<byte[]> CropAsync(
        byte[] imageBytes,
        CaptureRegion region,
        CancellationToken cancellationToken = default);
}
