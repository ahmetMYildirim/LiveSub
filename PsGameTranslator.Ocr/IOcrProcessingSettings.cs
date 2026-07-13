namespace PsGameTranslator.Ocr;

public interface IOcrProcessingSettings
{
    bool EnableOcrCache { get; }

    int OcrIntervalMilliseconds { get; }

    double MinimumConfidenceThreshold { get; }
}
