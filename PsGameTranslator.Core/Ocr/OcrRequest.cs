namespace PsGameTranslator.Core.Ocr;

public sealed class OcrRequest
{
    public ReadOnlyMemory<byte> ImageBytes { get; init; }
    public string? ImagePath { get; init; }
    public string Language { get; init; } = "en";
    public string RegionId { get; init; } = string.Empty;
    public PreprocessingSettings PreprocessingSettings { get; init; } = new();
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public bool ForceOcr { get; init; }
    public bool DebugMode { get; init; }
}
