namespace PsGameTranslator.Core.Models;

public sealed class SubtitleReplacementOverlaySnapshot
{
    public string Text { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public bool ShowMaskOnly { get; set; }
    public long DisplayDurationMs { get; set; }
    public SubtitleReplacementContext Context { get; set; } = new();

    public SubtitleReplacementOverlaySnapshot Clone() => new()
    {
        Text = Text,
        SourceText = SourceText,
        Reason = Reason,
        ShowMaskOnly = ShowMaskOnly,
        DisplayDurationMs = DisplayDurationMs,
        Context = Context.Clone(),
    };
}
