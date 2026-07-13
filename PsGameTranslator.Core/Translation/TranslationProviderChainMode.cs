namespace PsGameTranslator.Core.Translation;

public enum TranslationProviderChainMode
{
    LocalOnly = 0,
    SelectedOnly = 1,
    ProviderChain = 2,
    HybridBalanced = 3,

    // Backward-compatible aliases for older UI/settings names.
    CloudFast = SelectedOnly,
    Quality = ProviderChain,
}
