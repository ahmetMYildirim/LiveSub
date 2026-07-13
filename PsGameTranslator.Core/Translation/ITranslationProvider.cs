using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Core.Translation;

public interface ITranslationProvider
{
    string ProviderName { get; }
    string Name => ProviderName;
    TranslationProviderType ProviderType { get; }
    bool IsAvailable => true;

    Task<TranslationProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new TranslationProviderHealth
        {
            ProviderName = ProviderName,
            ProviderType = ProviderType,
            IsAvailable = IsAvailable,
            Message = IsAvailable ? "Available" : "Not available",
        });

    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);
}
