using System.Diagnostics;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

public sealed class FakeTranslationProvider : ITranslationProvider
{
    public string ProviderName => "FakeTranslation";
    public TranslationProviderType ProviderType => TranslationProviderType.MachineTranslation;

    public Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var translatedText = "[TR] " + request.SourceText;
        stopwatch.Stop();

        return Task.FromResult(new TranslationResult
        {
            SourceText = request.SourceText,
            TranslatedText = translatedText,
            ProviderName = ProviderName,
            Success = true,
            FromCache = false,
            RawOutput = translatedText,
            DurationMs = stopwatch.ElapsedMilliseconds,
        });
    }
}
