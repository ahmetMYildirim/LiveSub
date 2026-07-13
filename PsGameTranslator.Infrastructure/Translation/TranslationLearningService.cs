using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

/// <summary>
/// High-level coordinator for the translation learning pipeline.
/// Saves auto-records, manages TM lookup/upsert, and routes user actions.
/// </summary>
public sealed class TranslationLearningService : ITranslationLearningService
{
    private static readonly string DebugDir = Path.Combine(AppContext.BaseDirectory, "debug");
    private static readonly string ExportDir = Path.Combine(AppContext.BaseDirectory, "exports");

    private readonly ITranslationRecordRepository _repo;
    private readonly ITranslationMemoryService _memory;
    private readonly IFineTuneDatasetExporter _exporter;
    private readonly TranslationSettings _settings;
    private readonly ILogger<TranslationLearningService> _logger;

    public event Action? RecordsChanged;

    public TranslationLearningService(
        ITranslationRecordRepository repo,
        ITranslationMemoryService memory,
        IFineTuneDatasetExporter exporter,
        TranslationSettings settings,
        ILogger<TranslationLearningService> logger)
    {
        _repo = repo;
        _memory = memory;
        _exporter = exporter;
        _settings = settings;
        _logger = logger;
    }

    // ── Pipeline integration ──────────────────────────────────────────────────

    public async Task<TranslationMemoryEntry?> LookupMemoryAsync(
        string gameName, string sourceText, string? speakerName = null)
    {
        if (!_settings.EnableTranslationMemory) return null;

        var entry = await _memory.LookupAsync(
            gameName, BuildMemoryKeyText(sourceText, speakerName),
            _settings.UseGlobalTranslationMemoryFallback);

        if (entry is not null)
            await SaveMemoryLookupDiagnosticAsync(sourceText, entry);

        return entry;
    }

    public async Task<long> SaveAutoRecordAsync(
        string sourceText, string gameName,
        string? opusTranslation, string? glossaryTranslation,
        string providerName, long durationMs,
        string sourceLanguage, string targetLanguage,
        string? speakerName = null)
    {
        if (!_settings.EnableLearningRecords) return -1;

        var finalTranslation = glossaryTranslation ?? opusTranslation ?? string.Empty;
        var record = new TranslationRecord
        {
            GameName = gameName,
            SourceText = sourceText,
            NormalizedSourceKey = _memory.NormalizeSourceKey(BuildMemoryKeyText(sourceText, speakerName)),
            OpusTranslation = opusTranslation,
            GlossaryTranslation = glossaryTranslation,
            FinalTranslation = finalTranslation,
            Status = TranslationRecordStatus.AutoSaved,
            Timestamp = DateTimeOffset.Now,
            ProviderName = providerName,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            DurationMs = durationMs,
            SpeakerName = string.IsNullOrWhiteSpace(speakerName) ? null : speakerName,
        };

        var id = await _repo.SaveRecordAsync(record);
        if (id > 0)
        {
            record.Id = id;
            await SaveLearningDiagnosticAsync(record, memoryHit: false);
        }
        return id;
    }

    public async Task<long> SaveMemoryHitRecordAsync(
        string sourceText, string gameName, TranslationMemoryEntry memoryEntry,
        string sourceLanguage, string targetLanguage,
        string? speakerName = null)
    {
        if (!_settings.EnableLearningRecords) return -1;

        var record = new TranslationRecord
        {
            GameName = gameName,
            SourceText = sourceText,
            NormalizedSourceKey = _memory.NormalizeSourceKey(BuildMemoryKeyText(sourceText, speakerName)),
            FinalTranslation = memoryEntry.FinalTranslation,
            Status = TranslationRecordStatus.MemoryHit,
            Timestamp = DateTimeOffset.Now,
            ProviderName = "TranslationMemory",
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            SpeakerName = string.IsNullOrWhiteSpace(speakerName) ? null : speakerName,
        };

        var id = await _repo.SaveRecordAsync(record);
        if (id > 0)
        {
            record.Id = id;
            await _memory.IncrementUsageAsync(memoryEntry.Id);
            await SaveLearningDiagnosticAsync(record, memoryHit: true);
        }
        return id;
    }

    /// <summary>Part D: speaker names stay out of the memory key unless
    /// IncludeSpeakerInMemoryKey is explicitly enabled.</summary>
    private string BuildMemoryKeyText(string dialogueText, string? speakerName) =>
        _settings.IncludeSpeakerInMemoryKey && !string.IsNullOrWhiteSpace(speakerName)
            ? speakerName + " | " + dialogueText
            : dialogueText;

    // ── User actions ──────────────────────────────────────────────────────────

    public async Task AcceptRecordAsync(long recordId)
    {
        var records = await _repo.GetRecentRecordsAsync(500);
        var record = records.FirstOrDefault(r => r.Id == recordId);
        if (record is null) return;

        await _repo.UpdateStatusAsync(recordId, TranslationRecordStatus.AcceptedByUser);
        record.Status = TranslationRecordStatus.AcceptedByUser;
        await _memory.UpsertAsync(record.GameName, record);

        _logger.LogInformation("translation_record_accepted - id={Id}", recordId);
        RecordsChanged?.Invoke();
    }

    public async Task EditRecordAsync(long recordId, string correctedTranslation)
    {
        if (string.IsNullOrWhiteSpace(correctedTranslation)) return;

        var records = await _repo.GetRecentRecordsAsync(500);
        var record = records.FirstOrDefault(r => r.Id == recordId);
        if (record is null) return;

        await _repo.UpdateUserCorrectionAsync(
            recordId, correctedTranslation.Trim(), TranslationRecordStatus.EditedByUser);

        record.UserCorrection = correctedTranslation.Trim();
        record.FinalTranslation = correctedTranslation.Trim();
        record.Status = TranslationRecordStatus.EditedByUser;
        await _memory.UpsertAsync(record.GameName, record);

        _logger.LogInformation("translation_record_edited - id={Id}", recordId);
        RecordsChanged?.Invoke();
    }

    public async Task RejectRecordAsync(long recordId)
    {
        await _repo.UpdateStatusAsync(recordId, TranslationRecordStatus.RejectedByUser);
        _logger.LogInformation("translation_record_rejected - id={Id}", recordId);
        RecordsChanged?.Invoke();
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<TranslationRecord>> GetRecentRecordsAsync(int count = 100) =>
        _repo.GetRecentRecordsAsync(count);

    public Task<int> GetMemoryEntryCountAsync() => _memory.GetEntryCountAsync();

    public Task<int> GetCountByStatusAsync(TranslationRecordStatus status) =>
        _repo.GetCountByStatusAsync(status);

    // ── Export ────────────────────────────────────────────────────────────────

    public async Task<(int Exported, int Skipped, string OutputPath)> ExportJsonlAsync(
        string outputPath, string? gameName = null)
    {
        var result = await _exporter.ExportJsonlAsync(outputPath, gameName);
        await SaveExportDiagnosticAsync(outputPath, result.Exported, result.Skipped);
        return result;
    }

    public async Task<(int Exported, int Skipped, string OutputPath)> ExportTsvAsync(
        string outputPath, string? gameName = null)
    {
        var result = await _exporter.ExportTsvAsync(outputPath, gameName);
        await SaveExportDiagnosticAsync(outputPath, result.Exported, result.Skipped);
        return result;
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    private async Task SaveLearningDiagnosticAsync(TranslationRecord record, bool memoryHit)
    {
        try
        {
            Directory.CreateDirectory(DebugDir);
            var snapshot = new
            {
                Timestamp = DateTimeOffset.Now,
                record.Id,
                record.GameName,
                record.SourceText,
                record.NormalizedSourceKey,
                MemoryHit = memoryHit,
                record.Status,
                record.FinalTranslation,
                record.ProviderName,
                record.DurationMs,
            };
            await File.WriteAllTextAsync(
                Path.Combine(DebugDir, "last_translation_learning_record.json"),
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }
        catch { /* diagnostics are best-effort */ }
    }

    private async Task SaveMemoryLookupDiagnosticAsync(string sourceText, TranslationMemoryEntry? entry)
    {
        try
        {
            Directory.CreateDirectory(DebugDir);
            var snapshot = new
            {
                Timestamp = DateTimeOffset.Now,
                SourceText = sourceText,
                Hit = entry is not null,
                entry?.Id,
                entry?.GameName,
                entry?.FinalTranslation,
                entry?.Status,
                entry?.UsageCount,
            };
            await File.WriteAllTextAsync(
                Path.Combine(DebugDir, "last_translation_memory_lookup.json"),
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }
        catch { /* diagnostics are best-effort */ }
    }

    private static async Task SaveExportDiagnosticAsync(string outputPath, int exported, int skipped)
    {
        try
        {
            Directory.CreateDirectory(DebugDir);
            var snapshot = new
            {
                Timestamp = DateTimeOffset.Now,
                OutputPath = outputPath,
                Exported = exported,
                Skipped = skipped,
            };
            await File.WriteAllTextAsync(
                Path.Combine(DebugDir, "last_dataset_export_result.json"),
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }
        catch { /* diagnostics are best-effort */ }
    }
}
