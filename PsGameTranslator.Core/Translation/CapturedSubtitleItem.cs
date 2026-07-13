using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Core.Translation;

/// <summary>An ordered, deduplicated OCR subtitle candidate awaiting or undergoing translation.</summary>
public sealed class CapturedSubtitleItem
{
    public long Id { get; init; }
    public string SourceText { get; set; } = string.Empty;
    public string NormalizedSourceKey { get; init; } = string.Empty;
    public string SpeakerName { get; init; } = string.Empty;
    public DateTimeOffset FirstSeenAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.Now;
    public long FrameNumber { get; init; }
    public CapturedSubtitleStatus Status { get; set; } = CapturedSubtitleStatus.Captured;
    public long? TranslationRecordId { get; set; }
    public bool FromMemory { get; set; }
    public bool FromCache { get; set; }
    public SubtitleReplacementContext? ReplacementContext { get; set; }

    public long AgeMs => (long)(DateTimeOffset.Now - FirstSeenAt).TotalMilliseconds;
}
