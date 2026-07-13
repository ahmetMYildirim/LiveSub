namespace PsGameTranslator.Core.Models;

public sealed class OcrResult
{
    public string RequestId { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public CaptureRegion? Region { get; init; }
    public IReadOnlyList<OcrLine> Lines { get; init; } = [];
    public long DurationMs { get; init; }
    public bool Success { get; init; } = true;
    public string ErrorMessage { get; init; } = string.Empty;
    public string RawOutput { get; init; } = string.Empty;
}
