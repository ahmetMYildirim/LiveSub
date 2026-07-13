using System.Security.Cryptography;

namespace PsGameTranslator.Ocr;

public sealed class OcrResultCache
{
    public string? LastImageHash { get; private set; }

    public string? LastCleanedText { get; private set; }

    public DateTimeOffset? LastOcrTime { get; private set; }

    public static string ComputeImageHash(ReadOnlySpan<byte> imageBytes) =>
        Convert.ToHexString(SHA256.HashData(imageBytes));

    public bool IsSameImage(string imageHash) =>
        string.Equals(LastImageHash, imageHash, StringComparison.Ordinal);

    public bool IsSameText(string cleanedText) =>
        LastCleanedText is not null &&
        string.Equals(LastCleanedText, cleanedText, StringComparison.Ordinal);

    public bool IsInsideInterval(DateTimeOffset now, int intervalMilliseconds) =>
        LastOcrTime is { } lastTime &&
        intervalMilliseconds > 0 &&
        now - lastTime < TimeSpan.FromMilliseconds(intervalMilliseconds);

    public void StoreImage(string imageHash, DateTimeOffset ocrTime)
    {
        LastImageHash = imageHash;
        LastOcrTime = ocrTime;
    }

    public void StoreText(string cleanedText)
    {
        LastCleanedText = cleanedText;
    }
}
