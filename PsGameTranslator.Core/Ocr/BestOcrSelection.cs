using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Core.Ocr;

public sealed class BestOcrSelection
{
    public OcrResult BestResult { get; init; } = new();
    public string CandidateText { get; init; } = string.Empty;
    public string SpeakerName { get; init; } = string.Empty;
    public string DialogueText { get; init; } = string.Empty;
    public double Score { get; init; }
    public IReadOnlyList<RejectedOcrResult> RejectedResults { get; init; } = [];
    public IReadOnlyList<string> Reasons { get; init; } = [];
}
