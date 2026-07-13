namespace PsGameTranslator.Core.Translation;

public interface ITranslationMemoryService
{
    string NormalizeSourceKey(string sourceText);
    Task<TranslationMemoryEntry?> LookupAsync(string gameName, string sourceText, bool useGlobalFallback);
    Task UpsertAsync(string gameName, TranslationRecord record);
    Task IncrementUsageAsync(long memoryEntryId);
    Task<int> GetEntryCountAsync();
}
