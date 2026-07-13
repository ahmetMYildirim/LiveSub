using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Ocr;

namespace PsGameTranslator.Ocr;

public interface IOcrProvider
{
    string Name { get; }
    OcrProviderType ProviderType { get; }
    bool IsAvailable { get; }

    Task<OcrProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default);

    Task<OcrResult> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default);
}
