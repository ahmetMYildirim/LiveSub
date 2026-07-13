namespace PsGameTranslator.Core.Translation;

public enum TranslationRecordStatus
{
    Pending = 0,
    AutoSaved = 1,
    MemoryHit = 2,
    PostEditPending = 3,
    PostEditCompleted = 4,
    PostEditFailed = 5,
    AcceptedByUser = 6,
    EditedByUser = 7,
    RejectedByUser = 8,
}
