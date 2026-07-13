namespace PsGameTranslator.Core.Translation;

/// <summary>
/// Optional deep health check: verifies the provider end-to-end (model loaded,
/// tokenizer loaded, a real tiny inference succeeds). Slower than
/// <see cref="ITranslationProvider.CheckHealthAsync"/> — only used by explicit
/// "Check Providers" style UI actions, never on the hot translation path.
/// </summary>
public interface ITranslationProviderDeepHealth
{
    Task<TranslationProviderHealth> CheckDeepHealthAsync(CancellationToken cancellationToken = default);
}
