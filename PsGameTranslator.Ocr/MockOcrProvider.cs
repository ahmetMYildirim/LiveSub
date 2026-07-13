using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Ocr;

namespace PsGameTranslator.Ocr;

/// <summary>
/// Deterministic in-process OCR provider for tests and pipeline dry-runs.
/// Always succeeds and returns the configured text regardless of the image.
/// </summary>
public sealed class MockOcrProvider : IOcrProvider
{
    private readonly string _text;
    private readonly double _confidence;

    public MockOcrProvider(string? text = null, double confidence = 0.99)
    {
        _text = text ?? "Haymish\nI thank you, friend. Truly.";
        _confidence = confidence;
    }

    public string Name => "MockOCR";
    public OcrProviderType ProviderType => OcrProviderType.MockOCR;
    public bool IsAvailable => true;

    public Task<OcrProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new OcrProviderHealth
        {
            ProviderName = Name,
            ProviderType = ProviderType,
            IsAvailable = true,
            State = OcrProviderState.Available,
            Message = "Mock provider always available (test/diagnostic use).",
        });

    public Task<OcrResult> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
    {
        var textLines = _text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<OcrLine>(textLines.Length);
        for (var i = 0; i < textLines.Length; i++)
        {
            lines.Add(new OcrLine
            {
                Text = textLines[i],
                Confidence = _confidence,
                X = 10,
                Y = 10 + i * 30,
                Right = 400,
                Bottom = 36 + i * 30,
            });
        }

        return Task.FromResult(new OcrResult
        {
            ProviderName = Name,
            Text = _text,
            Confidence = _confidence,
            Lines = lines,
            DurationMs = 1,
            Success = true,
        });
    }
}
