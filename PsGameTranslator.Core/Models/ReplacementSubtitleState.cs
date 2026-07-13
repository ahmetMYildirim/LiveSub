namespace PsGameTranslator.Core.Models;

public enum ReplacementSubtitleStatus
{
    Empty,
    MaskingPendingTranslation,
    ShowingTurkish,
    HoldingTurkish,
    Expired,
}

public sealed class ReplacementSubtitleState
{
    public string CurrentSourceKey { get; set; } = string.Empty;
    public string CurrentSourceText { get; set; } = string.Empty;
    public OverlayRectangle CurrentOriginalSubtitleRect { get; set; } = new();
    public string CurrentTurkishText { get; set; } = string.Empty;
    public ReplacementSubtitleStatus CurrentStatus { get; set; } = ReplacementSubtitleStatus.Empty;
    public DateTimeOffset? FirstSeenAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? TranslationStartedAt { get; set; }
    public DateTimeOffset? TranslationCompletedAt { get; set; }
    public DateTimeOffset? DisplayStartedAt { get; set; }
    public DateTimeOffset? MinDisplayUntil { get; set; }
    public long? TranslationRecordId { get; set; }

    public ReplacementSubtitleState Clone() => new()
    {
        CurrentSourceKey = CurrentSourceKey,
        CurrentSourceText = CurrentSourceText,
        CurrentOriginalSubtitleRect = CurrentOriginalSubtitleRect.Clone(),
        CurrentTurkishText = CurrentTurkishText,
        CurrentStatus = CurrentStatus,
        FirstSeenAt = FirstSeenAt,
        LastSeenAt = LastSeenAt,
        TranslationStartedAt = TranslationStartedAt,
        TranslationCompletedAt = TranslationCompletedAt,
        DisplayStartedAt = DisplayStartedAt,
        MinDisplayUntil = MinDisplayUntil,
        TranslationRecordId = TranslationRecordId,
    };
}
