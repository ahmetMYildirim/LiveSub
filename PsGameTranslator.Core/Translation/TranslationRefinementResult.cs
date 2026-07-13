namespace PsGameTranslator.Core.Translation;

public sealed class TranslationRefinementResult
{
    public string SourceText { get; init; } = string.Empty;
    public string MachineTranslatedText { get; init; } = string.Empty;
    public string RefinedText { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public bool Success { get; init; }
    public bool TimedOut { get; init; }
    public string? ErrorMessage { get; init; }
    public string? RawOutput { get; init; }
    public long DurationMs { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}
