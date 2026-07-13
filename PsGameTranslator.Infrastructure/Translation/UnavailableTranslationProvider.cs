using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

public sealed class UnavailableTranslationProvider : ITranslationProvider
{
    private readonly string _message;
    private readonly TranslationProviderStatus _status;

    public UnavailableTranslationProvider(
        string providerName,
        TranslationProviderType providerType,
        string message,
        TranslationProviderStatus status = TranslationProviderStatus.NotImplemented)
    {
        ProviderName = providerName;
        ProviderType = providerType;
        _message = message;
        _status = status;
    }

    public string ProviderName { get; }
    public TranslationProviderType ProviderType { get; }
    public bool IsAvailable => false;

    public Task<TranslationProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new TranslationProviderHealth
        {
            ProviderName = ProviderName,
            ProviderType = ProviderType,
            IsAvailable = false,
            Status = _status,
            Message = _message,
            ConfigurationStatus = _status.ToString(),
        });

    public Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TranslationResult
        {
            SourceText = request.SourceText,
            SourceLanguage = request.SourceLanguage,
            TargetLanguage = request.TargetLanguage,
            ProviderName = ProviderName,
            Success = false,
            ErrorMessage = _message,
        });
}
