namespace PsGameTranslator.Core.Models;

/// <summary>
/// A normalized window-relative OCR region. Regions are stored as percentages so
/// they continue to work when the captured game window changes resolution.
/// </summary>
public sealed class SecondaryOcrRegionSettings
{
    public string Label { get; set; } = "Bölge";
    public bool IsEnabled { get; set; } = true;
    public bool UseForSpeakerName { get; set; }
    public double XPercent { get; set; }
    public double YPercent { get; set; } = 0.8;
    public double WidthPercent { get; set; } = 1.0;
    public double HeightPercent { get; set; } = 0.15;
}
