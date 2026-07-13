namespace PsGameTranslator.Core.Translation;

public interface ITranslationLearningService
{
    // Pipeline integration. sourceText must always be dialogue-only — speaker names
    // are passed separately as metadata (Part C/G).
    Task<TranslationMemoryEntry?> LookupMemoryAsync(string gameName, string sourceText, string? speakerName = null);
    Task<long> SaveAutoRecordAsync(
        string sourceText, string gameName,
        string? opusTranslation, string? glossaryTranslation,
        string providerName, long durationMs,
        string sourceLanguage, string targetLanguage,
        string? speakerName = null);
    Task<long> SaveMemoryHitRecordAsync(
        string sourceText, string gameName, TranslationMemoryEntry memoryEntry,
        string sourceLanguage, string targetLanguage,
        string? speakerName = null);

    // User actions
    Task AcceptRecordAsync(long recordId);
    Task EditRecordAsync(long recordId, string correctedTranslation);
    Task RejectRecordAsync(long recordId);

    // Queries
    Task<IReadOnlyList<TranslationRecord>> GetRecentRecordsAsync(int count = 100);
    Task<int> GetMemoryEntryCountAsync();
    Task<int> GetCountByStatusAsync(TranslationRecordStatus status);

    // Export
    Task<(int Exported, int Skipped, string OutputPath)> ExportJsonlAsync(string outputPath, string? gameName = null);
    Task<(int Exported, int Skipped, string OutputPath)> ExportTsvAsync(string outputPath, string? gameName = null);

    event Action? RecordsChanged;
}
