namespace PsGameTranslator.Core.Models;

public sealed class FormattedSubtitle
{
    public string RawText { get; init; } = string.Empty;
    public string CleanedText { get; init; } = string.Empty;
    public string SpeakerName { get; init; } = string.Empty;
    public string MainText { get; init; } = string.Empty;
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
    public string DisplayText { get; init; } = string.Empty;
    public bool IsEmpty { get; init; }
    public double Confidence { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}
