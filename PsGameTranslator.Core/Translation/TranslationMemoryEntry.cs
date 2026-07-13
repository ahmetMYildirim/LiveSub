namespace PsGameTranslator.Core.Translation;

public sealed class TranslationMemoryEntry
{
    public long Id { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string NormalizedSourceKey { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public string FinalTranslation { get; set; } = string.Empty;
    public TranslationRecordStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int UsageCount { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
