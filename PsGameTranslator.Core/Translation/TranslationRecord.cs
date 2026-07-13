namespace PsGameTranslator.Core.Translation;

public sealed class TranslationRecord
{
    public long Id { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public string NormalizedSourceKey { get; set; } = string.Empty;
    public string? OpusTranslation { get; set; }
    public string? GlossaryTranslation { get; set; }
    public string? OllamaPosteditTranslation { get; set; }
    public string? UserCorrection { get; set; }
    public string FinalTranslation { get; set; } = string.Empty;
    public TranslationRecordStatus Status { get; set; } = TranslationRecordStatus.AutoSaved;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string? UsedGlossaryTermsJson { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "tr";
    public long DurationMs { get; set; }
    public string? Notes { get; set; }

    /// <summary>Detected speaker name (metadata only — never part of SourceText,
    /// never exported in the fine-tune dataset).</summary>
    public string? SpeakerName { get; set; }
}
