namespace PsGameTranslator.Core.Models;

public sealed class SubtitleLineSelectionResult
{
    public IReadOnlyList<OcrLine> SelectedSubtitleLines { get; init; } = [];
    public IReadOnlyList<OcrLine> RejectedHudLines { get; init; } = [];
    public string RejectionReasons { get; init; } = string.Empty;
    public string SelectedText { get; init; } = string.Empty;
    public bool HasSubtitleCandidate { get; init; }

    /// <summary>False when filtering was disabled or line geometry was unavailable
    /// and the raw OCR text should be used unchanged.</summary>
    public bool FilteringApplied { get; init; }
}
