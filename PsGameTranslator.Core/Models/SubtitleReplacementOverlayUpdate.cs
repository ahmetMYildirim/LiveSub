namespace PsGameTranslator.Core.Models;

public sealed class SubtitleReplacementOverlayUpdate
{
    public string Text { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;

    /// <summary>Rendered on its own line above <see cref="Text"/> — never merged
    /// into the translated sentence (Part E). Empty = no speaker line.</summary>
    public string SpeakerName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public bool ShowMaskOnly { get; set; }
    public long DisplayDurationMs { get; set; }
    public SubtitleReplacementContext Context { get; set; } = new();
}
