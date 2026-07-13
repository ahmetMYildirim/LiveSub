using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Core.Ocr;

public sealed class RejectedOcrResult
{
    public OcrResult Result { get; init; } = new();
    public double Score { get; init; }
    public string Reason { get; init; } = string.Empty;
}
