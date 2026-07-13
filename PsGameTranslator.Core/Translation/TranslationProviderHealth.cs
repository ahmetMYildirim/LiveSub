namespace PsGameTranslator.Core.Translation;

public sealed class TranslationProviderHealth
{
    public string ProviderName { get; init; } = string.Empty;
    public TranslationProviderType ProviderType { get; init; }
    public bool IsAvailable { get; init; }
    public TranslationProviderStatus Status { get; init; } = TranslationProviderStatus.Failed;
    public string Message { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public string ConfigurationStatus { get; init; } = string.Empty;
}
