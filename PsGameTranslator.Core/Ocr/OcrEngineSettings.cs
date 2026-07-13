namespace PsGameTranslator.Core.Ocr;

public sealed class OcrEngineSettings
{
    public OcrProfile Profile { get; set; } = OcrProfile.Balanced;
    public OcrExecutionMode ExecutionMode { get; set; } = OcrExecutionMode.SingleProvider;
    public OcrProviderType PreferredProvider { get; set; } = OcrProviderType.PaddleOCR;
    public bool EnableParallelOcr { get; set; }
    public bool EnableOcrProviderFallback { get; set; }
    public bool AutoStartOcrServer { get; set; } = true;
    public OcrDeviceMode Device { get; set; } = OcrDeviceMode.Auto;
    public bool IgnoreOcrConfidenceThresholdForDebug { get; set; }
    public bool EnableSlowOcrProviders { get; set; }
    public int OcrTimeoutMs { get; set; } = 1500;

    /// <summary>
    /// Timeout for subprocess-transport OCR (cold model load per call).
    /// Not touched by profiles; always used as a floor for subprocess providers.
    /// </summary>
    public int SubprocessOcrTimeoutMs { get; set; } = 15000;
    public int CaptureIntervalMs { get; set; } = 100;
    public int MaxOcrFrameBufferSize { get; set; } = 10;
    public bool AggressiveDuplicateFiltering { get; set; } = true;
    public bool HudFiltering { get; set; } = true;
    public PreprocessingPreset PreprocessingPreset { get; set; } = PreprocessingPreset.FastSubtitle;

    public OcrProviderType[] FastProviderPriority { get; set; } =
    [
        OcrProviderType.WindowsOCR,
        OcrProviderType.PaddleOCR,
        OcrProviderType.RapidOCR,
        OcrProviderType.EasyOCR,
    ];

    public OcrProviderType[] BalancedProviderPriority { get; set; } =
    [
        OcrProviderType.PaddleOCR,
        OcrProviderType.WindowsOCR,
        OcrProviderType.RapidOCR,
    ];

    public OcrProviderType[] AccurateProviderPriority { get; set; } =
    [
        OcrProviderType.PaddleOCR,
        OcrProviderType.WindowsOCR,
        OcrProviderType.RapidOCR,
    ];

    public void ApplyProfileDefaults(bool preserveSelectedProvider = true)
    {
        if (Profile == OcrProfile.Custom)
            return;

        var selectedProvider = PreferredProvider;

        switch (Profile)
        {
            case OcrProfile.Fast:
                ExecutionMode = OcrExecutionMode.SingleProvider;
                PreferredProvider = OcrProviderType.WindowsOCR;
                CaptureIntervalMs = 80;
                MaxOcrFrameBufferSize = 8;
                OcrTimeoutMs = 700;
                AggressiveDuplicateFiltering = true;
                HudFiltering = true;
                PreprocessingPreset = PreprocessingPreset.FastSubtitle;
                EnableParallelOcr = false;
                break;
            case OcrProfile.Accurate:
                ExecutionMode = OcrExecutionMode.ParallelBestResult;
                PreferredProvider = OcrProviderType.PaddleOCR;
                CaptureIntervalMs = 100;
                MaxOcrFrameBufferSize = 10;
                // Real-world PaddleOCR-Server calls on a typical subtitle crop have been
                // observed taking ~1.5-1.6s — 2500ms left almost no margin, and ParallelBestResult
                // runs multiple providers per tick, making it even tighter.
                OcrTimeoutMs = 5000;
                AggressiveDuplicateFiltering = true;
                HudFiltering = true;
                PreprocessingPreset = PreprocessingPreset.HighContrastWhiteText;
                EnableParallelOcr = true;
                break;
            default:
                ExecutionMode = OcrExecutionMode.SingleProvider;
                // WindowsOCR is fast (native, no server round-trip) but reads stylized
                // game fonts poorly — observed producing garbled text like "thuddering
                // ga.? 'Tis%eyclops." on real gameplay. PaddleOCR is slower but far more
                // reliable on non-standard fonts, so it's the right single-engine default.
                PreferredProvider = OcrProviderType.PaddleOCR;
                CaptureIntervalMs = 100;
                MaxOcrFrameBufferSize = 10;
                // Was 1500ms — too close to the ~1.5-1.6s a real PaddleOCR-Server call takes on
                // a normal-sized subtitle crop, so nearly every request timed out and produced no
                // text at all (nothing to translate, even though translation itself was fine).
                OcrTimeoutMs = 4000;
                AggressiveDuplicateFiltering = true;
                HudFiltering = true;
                PreprocessingPreset = PreprocessingPreset.FastSubtitle;
                EnableParallelOcr = false;
                break;
        }

        if (preserveSelectedProvider)
            PreferredProvider = selectedProvider;
    }
}
