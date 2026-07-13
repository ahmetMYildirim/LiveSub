namespace PsGameTranslator.Core.Models;

public sealed class SubtitleOverlayStyleSettings
{
    public SubtitlePreset SubtitlePreset { get; set; } = SubtitlePreset.Cinematic;
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 30;
    public string FontWeight { get; set; } = "SemiBold";
    public bool BackgroundEnabled { get; set; } = true;
    public double BackgroundOpacity { get; set; } = 0.55;
    public double BackgroundCornerRadius { get; set; } = 10;
    public double PaddingHorizontal { get; set; } = 22;
    public double PaddingVertical { get; set; } = 12;
    public double MaxWidthPercent { get; set; } = 0.72;
    public double BottomMargin { get; set; } = 110;
    public string TextAlignment { get; set; } = "Center";
    public string TextColor { get; set; } = "#FFFFFF";
    public bool ShadowEnabled { get; set; } = true;
    public bool OutlineEnabled { get; set; } = true;
    public double OutlineThickness { get; set; } = 1;

    public static SubtitleOverlayStyleSettings CreatePreset(SubtitlePreset preset)
    {
        return preset switch
        {
            SubtitlePreset.LargeReadable => new SubtitleOverlayStyleSettings
            {
                SubtitlePreset = preset,
                FontSize = 36,
                BackgroundOpacity = 0.65,
                BackgroundCornerRadius = 10,
                PaddingHorizontal = 26,
                PaddingVertical = 14,
                MaxWidthPercent = 0.82,
                BottomMargin = 120,
                OutlineThickness = 2,
            },
            SubtitlePreset.Compact => new SubtitleOverlayStyleSettings
            {
                SubtitlePreset = preset,
                FontSize = 24,
                BackgroundOpacity = 0.45,
                BackgroundCornerRadius = 10,
                PaddingHorizontal = 16,
                PaddingVertical = 8,
                MaxWidthPercent = 0.60,
                BottomMargin = 90,
                OutlineThickness = 1,
            },
            SubtitlePreset.Accessibility => new SubtitleOverlayStyleSettings
            {
                SubtitlePreset = preset,
                FontSize = 40,
                FontWeight = "Bold",
                BackgroundOpacity = 0.80,
                BackgroundCornerRadius = 10,
                PaddingHorizontal = 30,
                PaddingVertical = 18,
                MaxWidthPercent = 0.90,
                BottomMargin = 130,
                ShadowEnabled = true,
                OutlineEnabled = true,
                OutlineThickness = 2,
            },
            _ => new SubtitleOverlayStyleSettings
            {
                SubtitlePreset = SubtitlePreset.Cinematic,
                FontSize = 30,
                FontWeight = "SemiBold",
                BackgroundEnabled = true,
                BackgroundOpacity = 0.55,
                BackgroundCornerRadius = 10,
                PaddingHorizontal = 22,
                PaddingVertical = 12,
                MaxWidthPercent = 0.72,
                BottomMargin = 110,
                TextAlignment = "Center",
                ShadowEnabled = true,
                OutlineEnabled = true,
                OutlineThickness = 1,
            },
        };
    }
}
