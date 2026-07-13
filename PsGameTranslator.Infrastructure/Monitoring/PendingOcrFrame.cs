namespace PsGameTranslator.Infrastructure.Monitoring;

/// <summary>A single OCR-ready crop submitted by the capture loop to the OcrWorker.</summary>
public sealed class PendingOcrFrame
{
    public required byte[] ImageBytes { get; init; }
    /// <summary>Optional small crop from the same captured frame containing only
    /// the speaker name plate. It is kept separate from subtitle OCR so the name
    /// can never leak into the translation input.</summary>
    public byte[]? SecondarySpeakerImageBytes { get; init; }
    public string SecondarySpeakerRegionLabel { get; init; } = string.Empty;
    public long FrameNumber { get; init; }
    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public bool IsForced { get; init; }
    public string CropHash { get; init; } = string.Empty;
    public int WindowLeft { get; init; }
    public int WindowTop { get; init; }
    public int WindowWidth { get; init; }
    public int WindowHeight { get; init; }
    public int SavedRegionX { get; init; }
    public int SavedRegionY { get; init; }
    public int SavedRegionWidth { get; init; }
    public int SavedRegionHeight { get; init; }
    public int FinalCropOffsetX { get; init; }
    public int FinalCropOffsetY { get; init; }
    public int FinalCropWidth { get; init; }
    public int FinalCropHeight { get; init; }
    public int OcrImageWidth { get; init; }
    public int OcrImageHeight { get; init; }
}
