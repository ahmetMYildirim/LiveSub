namespace PsGameTranslator.Core.Translation;

public enum CapturedSubtitleStatus
{
    Captured,
    MemoryHit,
    CacheHit,
    QueuedForTranslation,
    Translating,
    Translated,
    Displayed,
    Expired,
    Rejected,
}
