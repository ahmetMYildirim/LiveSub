namespace PsGameTranslator.Core.Translation;

public interface ITranslationRecordRepository
{
    Task InitializeAsync();
    Task<long> SaveRecordAsync(TranslationRecord record);
    Task UpdateStatusAsync(long recordId, TranslationRecordStatus status);
    Task UpdateUserCorrectionAsync(long recordId, string correctedTranslation, TranslationRecordStatus status);
    Task UpdatePosteditAsync(long recordId, string postedit, TranslationRecordStatus status);
    Task<IReadOnlyList<TranslationRecord>> GetRecentRecordsAsync(int count);
    Task<IReadOnlyList<TranslationRecord>> GetExportableRecordsAsync(string? gameName);
    Task<int> GetCountByStatusAsync(TranslationRecordStatus status);
}
