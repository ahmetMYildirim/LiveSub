namespace PsGameTranslator.Core.Ocr;

public sealed class OcrProviderHealth
{
    public string ProviderName { get; init; } = string.Empty;
    public OcrProviderType ProviderType { get; init; }
    public bool IsAvailable { get; init; }
    public OcrProviderState State { get; init; } = OcrProviderState.Failed;
    public string Message { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public string? ServerStatus { get; init; }
    public string? RawHealthResult { get; init; }
}
