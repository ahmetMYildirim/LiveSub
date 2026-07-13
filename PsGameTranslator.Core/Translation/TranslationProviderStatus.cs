namespace PsGameTranslator.Core.Translation;

public enum TranslationProviderStatus
{
    Available,
    NotConfigured,
    NotImplemented,
    MissingApiKey,
    ServerNotRunning,
    Running,
    Failed,
    Unreachable,
}
