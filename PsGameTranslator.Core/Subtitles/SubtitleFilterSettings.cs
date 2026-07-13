namespace PsGameTranslator.Core.Subtitles;

/// <summary>
/// Runtime-mutable settings for OCR subtitle line filtering
/// (dialogue vs tutorial/HUD prompt classification).
/// </summary>
public sealed class SubtitleFilterSettings
{
    public bool EnableSubtitleLineFiltering { get; set; } = true;
    public SubtitleBandMode SubtitleBandMode { get; set; } = SubtitleBandMode.UpperBand;

    // Inside the selected OCR crop, only consider subtitle candidates whose
    // vertical center falls within [Top, Bottom]. Defaults keep the upper 55%
    // because real subtitles usually sit above tutorial/action prompts.
    public double SubtitleBandTopPercent { get; set; } = 0.00;
    public double SubtitleBandBottomPercent { get; set; } = 0.55;

    public bool ShowRejectedHudLines { get; set; } = true;
    public bool ShowSelectedSubtitleLines { get; set; } = true;

    public string ActiveGameProfileName { get; set; } = "Default English Game";
}
