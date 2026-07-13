namespace PsGameTranslator.Core.Models;

public sealed class TranslationResult
{
    public string SourceText { get; init; } = string.Empty;
    public string TranslatedText { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;
    public bool Success { get; init; }
    public bool FromCache { get; init; }
    public string? ErrorMessage { get; init; }
    public string? RawOutput { get; init; }
    public long DurationMs { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    // Existing Ollama diagnostics and compatibility aliases.
    public string SourceLanguage { get; init; } = string.Empty;
    public string TargetLanguage { get; init; } = string.Empty;
    public string Provider
    {
        get => ProviderName;
        init => ProviderName = value;
    }
    public string RawResponse
    {
        get => RawOutput ?? string.Empty;
        init => RawOutput = value;
    }
    public string ParsedTranslation { get; init; } = string.Empty;
    public string PostProcessedTranslation { get; init; } = string.Empty;
    public bool JsonParseSucceeded { get; init; }
}
