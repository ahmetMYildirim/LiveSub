using System.Diagnostics;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Ocr;

namespace PsGameTranslator.Ocr;

public sealed class PaddleOcrProvider : IOcrProvider
{
    private readonly PaddleOcrService _service;

    public PaddleOcrProvider(PaddleOcrService service)
    {
        _service = service;
    }

    public string Name => "PaddleOCR";
    public OcrProviderType ProviderType => OcrProviderType.PaddleOCR;
    public bool IsAvailable => true;

    public Task<OcrProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new OcrProviderHealth
        {
            ProviderName = Name,
            ProviderType = ProviderType,
            IsAvailable = true,
            State = OcrProviderState.Available,
            Message = "PaddleOCR subprocess provider is configured.",
        });

    public async Task<OcrResult> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await _service.RecognizeAsync(
                request.ImageBytes,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return new OcrResult
            {
                ProviderName = Name,
                Text = result.Text,
                Confidence = result.Confidence,
                Region = result.Region,
                Lines = result.Lines,
                DurationMs = result.DurationMs > 0 ? result.DurationMs : stopwatch.ElapsedMilliseconds,
                Success = true,
                RawOutput = result.RawOutput,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new OcrResult
            {
                ProviderName = Name,
                Success = false,
                ErrorMessage = exception.Message,
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }
    }
}
