using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Core.Translation;

/// <summary>A Turkish subtitle ready for ordered, timed display on the overlay.</summary>
public sealed class TranslatedSubtitleDisplayItem
{
    public string SourceText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
    public string NormalizedSourceKey { get; init; } = string.Empty;
    public string SpeakerName { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset ReadyAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? DisplayedAt { get; set; }
    public int MinDisplayMs { get; init; }
    public int MaxDisplayMs { get; init; }
    public bool FromMemory { get; init; }
    public bool FromCache { get; init; }
    public long? TranslationRecordId { get; init; }
    public SubtitleReplacementContext? ReplacementContext { get; init; }
    public int DisplayDurationMs { get; init; }

    /// <summary>Origin tag for LastOverlayUpdateSource diagnostics (Part G).</summary>
    public string Source { get; init; } = "PLAYBACK_QUEUE";
}
