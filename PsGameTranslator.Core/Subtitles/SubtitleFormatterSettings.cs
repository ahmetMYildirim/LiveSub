namespace PsGameTranslator.Core.Subtitles;

public sealed class SubtitleFormatterSettings
{
    public bool EnableSubtitleFormatter { get; set; } = true;
    public int MaxSubtitleLines { get; set; } = 2;
    public int MaxCharactersPerLine { get; set; } = 42;
    public bool ShowSpeakerName { get; set; } = true;
    public bool RemoveHudNoise { get; set; } = true;
    public List<string> HudNoiseWords { get; set; } = ["Switch", "Menu", "Map", "Inventory"];
}
