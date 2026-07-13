namespace PsGameTranslator.Core.Models;

public sealed class GameProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "tr";
    public List<CaptureRegion> CaptureRegions { get; set; } = [];

    // OCR subtitle line filtering (per-game overrides).
    public bool EnableSubtitleLineFiltering { get; set; } = true;
    public double SubtitleBandTopPercent { get; set; } = 0.00;
    public double SubtitleBandBottomPercent { get; set; } = 0.55;

    /// <summary>Lines containing any of these are always rejected (map labels, currency, etc.).</summary>
    public List<string> HudNoisePatterns { get; set; } = [];

    /// <summary>Proper names / game terms that should not be translated. A protected
    /// term alone never rescues a line that matches tutorial prompt rules.</summary>
    public List<string> ProtectedTerms { get; set; } = [];

    /// <summary>Game-specific tutorial/HUD prompt indicators (e.g. "Press", "hogtie").</summary>
    public List<string> TutorialPromptPatterns { get; set; } = [];
}
