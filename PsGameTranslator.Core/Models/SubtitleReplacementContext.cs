namespace PsGameTranslator.Core.Models;

public sealed class SubtitleReplacementContext
{
    public OverlayRectangle OcrLineRect { get; set; } = new();
    public OverlayRectangle CropRect { get; set; } = new();
    public OverlayRectangle WindowRect { get; set; } = new();
    public OverlayRectangle ScreenRect { get; set; } = new();
    public OverlayRectangle OverlayRect { get; set; } = new();
    public double DpiScaleX { get; set; } = 1.0;
    public double DpiScaleY { get; set; } = 1.0;
    public bool UsedFallbackRegion { get; set; }
    public string SelectedLinesText { get; set; } = string.Empty;
    public IReadOnlyList<OverlayRectangle> OcrLineBoxes { get; set; } = [];
    public IReadOnlyList<OverlayRectangle> SelectedLineBoxes { get; set; } = [];
    public OverlayRectangle UnionSubtitleRectInCrop { get; set; } = new();
    public OverlayRectangle CropRectInWindow { get; set; } = new();
    public string MonitorDeviceName { get; set; } = string.Empty;
    public OverlayRectangle MonitorBounds { get; set; } = new();

    public SubtitleReplacementContext Clone() => new()
    {
        OcrLineRect = OcrLineRect.Clone(),
        CropRect = CropRect.Clone(),
        WindowRect = WindowRect.Clone(),
        ScreenRect = ScreenRect.Clone(),
        OverlayRect = OverlayRect.Clone(),
        DpiScaleX = DpiScaleX,
        DpiScaleY = DpiScaleY,
        UsedFallbackRegion = UsedFallbackRegion,
        SelectedLinesText = SelectedLinesText,
        OcrLineBoxes = OcrLineBoxes.Select(rect => rect.Clone()).ToArray(),
        SelectedLineBoxes = SelectedLineBoxes.Select(rect => rect.Clone()).ToArray(),
        UnionSubtitleRectInCrop = UnionSubtitleRectInCrop.Clone(),
        CropRectInWindow = CropRectInWindow.Clone(),
        MonitorDeviceName = MonitorDeviceName,
        MonitorBounds = MonitorBounds.Clone(),
    };
}
