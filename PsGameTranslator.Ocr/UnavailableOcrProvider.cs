using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Ocr;

namespace PsGameTranslator.Ocr;

public sealed class UnavailableOcrProvider : IOcrProvider
{
    private readonly OcrProviderState _state;

    public UnavailableOcrProvider(
        string name,
        OcrProviderType providerType,
        string message,
        OcrProviderState state = OcrProviderState.NotImplemented)
    {
        Name = name;
        ProviderType = providerType;
        Message = message;
        _state = state;
    }

    public string Name { get; }
    public OcrProviderType ProviderType { get; }
    public string Message { get; }
    public bool IsAvailable => false;

    public Task<OcrProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new OcrProviderHealth
        {
            ProviderName = Name,
            ProviderType = ProviderType,
            IsAvailable = false,
            State = _state,
            Message = Message,
        });

    public Task<OcrResult> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new OcrResult
        {
            ProviderName = Name,
            Success = false,
            ErrorMessage = Message,
        });
}
