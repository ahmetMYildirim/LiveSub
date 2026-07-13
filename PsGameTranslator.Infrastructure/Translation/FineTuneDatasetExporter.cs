using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

public sealed class FineTuneDatasetExporter : IFineTuneDatasetExporter
{
    private readonly ITranslationRecordRepository _repo;
    private readonly TranslationSettings _settings;
    private readonly ILogger<FineTuneDatasetExporter> _logger;

    public FineTuneDatasetExporter(
        ITranslationRecordRepository repo,
        TranslationSettings settings,
        ILogger<FineTuneDatasetExporter> logger)
    {
        _repo = repo;
        _settings = settings;
        _logger = logger;
    }

    public async Task<(int Exported, int Skipped, string OutputPath)> ExportJsonlAsync(
        string outputPath, string? gameName = null)
    {
        var records = await _repo.GetExportableRecordsAsync(gameName);
        var exported = 0;
        var skipped = 0;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await using var writer = new StreamWriter(outputPath, append: false, Encoding.UTF8);

        foreach (var r in records)
        {
            if (!PassesQualityFilter(r)) { skipped++; continue; }

            var entry = new
            {
                translation = new
                {
                    en = r.SourceText,
                    tr = r.FinalTranslation,
                }
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(entry));
            exported++;
        }

        _logger.LogInformation(
            "dataset_export_jsonl - exported={Exported}, skipped={Skipped}, path={Path}",
            exported, skipped, outputPath);
        await WriteDatasetCardAsync(outputPath, "JSONL (HuggingFace translation format)", gameName, exported, skipped);
        return (exported, skipped, outputPath);
    }

    public async Task<(int Exported, int Skipped, string OutputPath)> ExportTsvAsync(
        string outputPath, string? gameName = null)
    {
        var records = await _repo.GetExportableRecordsAsync(gameName);
        var exported = 0;
        var skipped = 0;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await using var writer = new StreamWriter(outputPath, append: false, Encoding.UTF8);
        await writer.WriteLineAsync("source_text\tfinal_translation");

        foreach (var r in records)
        {
            if (!PassesQualityFilter(r)) { skipped++; continue; }

            var src = r.SourceText.Replace("\t", " ").Replace("\n", " ");
            var tgt = r.FinalTranslation.Replace("\t", " ").Replace("\n", " ");
            await writer.WriteLineAsync($"{src}\t{tgt}");
            exported++;
        }

        _logger.LogInformation(
            "dataset_export_tsv - exported={Exported}, skipped={Skipped}, path={Path}",
            exported, skipped, outputPath);
        await WriteDatasetCardAsync(outputPath, "TSV (source_text \\t final_translation)", gameName, exported, skipped);
        return (exported, skipped, outputPath);
    }

    /// <summary>Writes a dataset_card.md next to the export so the dataset stays
    /// self-describing when moved to a training environment later.</summary>
    private async Task WriteDatasetCardAsync(
        string outputPath, string format, string? gameName, int exported, int skipped)
    {
        try
        {
            var cardPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "dataset_card.md");
            var card = $$"""
                # PsGameTranslator Fine-Tune Dataset

                - **Exported:** {{DateTimeOffset.Now:yyyy-MM-dd HH:mm}}
                - **File:** {{Path.GetFileName(outputPath)}}
                - **Format:** {{format}}
                - **Language pair:** {{_settings.SourceLanguage}} → {{_settings.TargetLanguage}}
                - **Game:** {{(string.IsNullOrWhiteSpace(gameName) ? "all games" : gameName)}}
                - **Pairs exported:** {{exported}}
                - **Pairs skipped by quality filter:** {{skipped}}
                - **Quality filter enabled:** {{_settings.EnableDatasetQualityFilter}}

                Content: user-accepted or user-edited game subtitle translations
                (statuses AcceptedByUser / EditedByUser). Speaker names are excluded
                from source texts.

                Suggested use: LoRA/QLoRA adaptation of a MarianMT (OPUS-MT) or small
                LLM once ≥2,000 pairs are available. The JSONL rows follow the
                HuggingFace `{"translation": {"en": …, "tr": …} }` convention.
                """;
            await File.WriteAllTextAsync(cardPath, card, new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not write dataset card");
        }
    }

    private bool PassesQualityFilter(TranslationRecord r)
    {
        if (!_settings.EnableDatasetQualityFilter) return true;

        // Skip too-short entries
        if (r.SourceText.Length < 3) return false;
        if (r.FinalTranslation.Length < 2) return false;

        // Skip single-char OCR noise
        if (r.SourceText.Trim().Length <= 2 &&
            r.SourceText.All(c => char.IsLetter(c))) return false;

        // Skip if translation equals source (likely untranslated)
        if (string.Equals(r.SourceText.Trim(), r.FinalTranslation.Trim(),
                StringComparison.OrdinalIgnoreCase)) return false;

        // Skip empty final
        if (string.IsNullOrWhiteSpace(r.FinalTranslation)) return false;

        return true;
    }
}
