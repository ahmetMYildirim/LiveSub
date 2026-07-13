using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Core.Models;

public sealed class PipelineDiagnostics
{
    public event Action? Changed;

    public long LastFrameNumber { get; set; }
    public bool LastCaptureSucceeded { get; set; }
    public bool LastCropSucceeded { get; set; }
    public DateTimeOffset? LastOcrStartedAt { get; set; }
    public DateTimeOffset? LastOcrFinishedAt { get; set; }
    public long LastOcrDurationMs { get; set; }
    public string LastOcrRawText { get; set; } = string.Empty;
    public string LastOcrCleanedText { get; set; } = string.Empty;
    public double LastOcrConfidence { get; set; }
    public bool LastOcrWasEmpty { get; set; }
    public string LastOcrError { get; set; } = string.Empty;

    public bool TranslationEnabled { get; set; }
    public TranslationDisplayMode TranslationDisplayMode { get; set; }
    public DateTimeOffset? LastTranslationEnqueuedAt { get; set; }
    public string LastTranslationSourceText { get; set; } = string.Empty;
    public string LastTranslationQueueStatus { get; set; } = "disabled";
    public DateTimeOffset? LastTranslationStartedAt { get; set; }
    public DateTimeOffset? LastTranslationFinishedAt { get; set; }
    public long LastTranslationDurationMs { get; set; }
    public string LastTranslationRawResponse { get; set; } = string.Empty;
    public string LastTranslationParsedText { get; set; } = string.Empty;
    public string LastTranslationPostProcessedText { get; set; } = string.Empty;
    public string LastTranslationProviderName { get; set; } = string.Empty;
    public DateTimeOffset? LastTranslationTime { get; set; }
    public string LastTranslationError { get; set; } = string.Empty;
    public bool LastTranslationWasFromCache { get; set; }
    public bool LastTranslationWasDroppedAsStale { get; set; }
    public string LastTranslationDropReason { get; set; } = string.Empty;
    public string ActualProviderUsed { get; set; } = string.Empty;
    public bool LastTranslationWasFallbackUsed { get; set; }
    public string LastTranslationFallbackReason { get; set; } = string.Empty;

    // Subtitle line filtering + fast-dialogue queue diagnostics.
    public string CurrentSubtitleSourceText { get; set; } = string.Empty;
    public string CurrentSubtitleSelectedLines { get; set; } = string.Empty;
    public string RejectedHudLines { get; set; } = string.Empty;
    public int TranslationQueueCount { get; set; }
    public string TranslationQueueItems { get; set; } = string.Empty;
    public long TranslationLateCompletedCount { get; set; }
    public long TranslationExpiredCount { get; set; }
    public long TranslationCacheSavedCount { get; set; }
    public string LastQueueDropReason { get; set; } = string.Empty;
    public long LastSubtitleAgeMsWhenTranslationCompleted { get; set; }
    public string LastOverlayReplaceReason { get; set; } = string.Empty;
    public string CurrentNormalizedSourceKey { get; set; } = string.Empty;
    public string CurrentOverlayDisplayText { get; set; } = string.Empty;
    public string CurrentOverlayDisplayLanguage { get; set; } = string.Empty;
    public string CurrentOverlayDisplayState { get; set; } = string.Empty;
    public string CurrentOverlayTranslationText { get; set; } = string.Empty;
    public bool LastOverlaySourceIgnoredBecauseTranslationExists { get; set; }
    public bool LastOverlayTranslationWasLate { get; set; }
    public bool LastOverlayCacheHit { get; set; }

    public string OllamaBaseUrl { get; set; } = string.Empty;
    public string OllamaModel { get; set; } = string.Empty;
    public bool OllamaReachable { get; set; }
    public int? LastOllamaStatusCode { get; set; }
    public string LastOllamaRequestBodyPreview { get; set; } = string.Empty;
    public string LastOllamaResponsePreview { get; set; } = string.Empty;

    public long TranslationEnqueueCount { get; set; }
    public long TranslationStartedCount { get; set; }
    public long TranslationCompletedCount { get; set; }
    public long TranslationFailedCount { get; set; }
    public long TranslationDroppedCount { get; set; }
    public long TranslationCacheHitCount { get; set; }

    // OCR noise filtering diagnostics.
    public string LastRejectedOcrNoiseText { get; set; } = string.Empty;
    public string LastRejectedOcrNoiseReason { get; set; } = string.Empty;
    public string LastAcceptedShortSubtitleReason { get; set; } = string.Empty;

    // Ollama refinement diagnostics.
    public bool RefinementEnabled { get; set; }
    public string RefinementMode { get; set; } = string.Empty;
    public long LastRefinementDurationMs { get; set; }
    public string LastRefinedText { get; set; } = string.Empty;
    public string LastRefinementError { get; set; } = string.Empty;
    public bool LastRefinementOverlayReplaced { get; set; }
    public string LastGlossaryTermsUsed { get; set; } = string.Empty;

    // ── Ordered subtitle pipeline diagnostics (Part B/D/G/K) ────────────────────

    // Part B — candidate validation.
    public string LastRejectedSubtitleCandidate { get; set; } = string.Empty;
    public string LastRejectedReason { get; set; } = string.Empty;
    public long RejectedBeforeQueueCount { get; set; }
    public long AcceptedSubtitleCandidateCount { get; set; }

    // Part D — memory/cache/in-flight lookup before OPUS-MT.
    public long MemoryHitCount { get; set; }
    public long CacheHitCount { get; set; }
    public long InFlightHitCount { get; set; }
    public long ActualOpusCallCount { get; set; }
    public long DuplicateSubtitleIgnoredCount { get; set; }
    public string LastDedupReason { get; set; } = string.Empty;

    // Part G — overlay ownership.
    public string LastOverlayUpdateSource { get; set; } = string.Empty;

    // Part K — realtime queue diagnostics panel.
    public int CapturedQueueCount { get; set; }
    public int OrderedTranslationQueueCount { get; set; }
    public int PlaybackQueueCount { get; set; }
    public string LastCapturedSourceText { get; set; } = string.Empty;
    public string LastTranslatedTurkishText { get; set; } = string.Empty;
    public string LastDisplayedTurkishText { get; set; } = string.Empty;
    public long LastSubtitleDisplayDurationMs { get; set; }
    public string LastReplacementRect { get; set; } = string.Empty;
    public string LastReplacementSourceRect { get; set; } = string.Empty;
    public bool WasEnglishShownInReplacementMode { get; set; }
    public bool WasEnglishBlockedInReplacementMode { get; set; }
    public string LastAcceptedOcrCandidate { get; set; } = string.Empty;
    public string LastReplacementStatus { get; set; } = string.Empty;
    public string LastReplacementSourceKey { get; set; } = string.Empty;
    public long ExpiredSkippedCount { get; set; }
    public double AverageOpusDurationMs { get; set; }
    public double AverageCaptureToDisplayLatencyMs { get; set; }

    public void NotifyChanged() => Changed?.Invoke();
}
