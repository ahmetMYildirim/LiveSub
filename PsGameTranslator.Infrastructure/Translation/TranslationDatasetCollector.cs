using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

/// <summary>
/// Silently appends successful DeepL / Google Translate results to separate
/// JSONL datasets (data/training/dataset_deepl.jsonl, dataset_google.jsonl) for
/// later OPUS-MT fine-tuning. Every other provider (OPUS-MT itself, Ollama,
/// LM Studio, Groq, Gemini) is intentionally ignored — only these two "teacher"
/// providers are being used as fine-tuning ground truth.
/// </summary>
public sealed class TranslationDatasetCollector
{
    private static readonly string DatasetDir =
        Path.Combine(AppContext.BaseDirectory, "data", "training");

    private readonly ILogger<TranslationDatasetCollector> _logger;
    private readonly object _gate = new();

    public TranslationDatasetCollector(ILogger<TranslationDatasetCollector> logger)
    {
        _logger = logger;
    }

    public void Record(string sourceText, string translatedText, TranslationProviderType providerType)
    {
        if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(translatedText))
            return;

        var fileName = providerType switch
        {
            TranslationProviderType.GoogleTranslate => "dataset_google.jsonl",
            TranslationProviderType.DeepL => "dataset_deepl.jsonl",
            _ => null,
        };
        if (fileName is null)
            return;

        try
        {
            var entry = new DatasetEntry(sourceText.Trim(), translatedText.Trim(), providerType.ToString(), DateTimeOffset.Now);
            var json = JsonSerializer.Serialize(entry);

            lock (_gate)
            {
                Directory.CreateDirectory(DatasetDir);
                File.AppendAllText(Path.Combine(DatasetDir, fileName), json + Environment.NewLine);
            }
        }
        catch (Exception exception)
        {
            // Never let dataset logging break live translation.
            _logger.LogWarning(exception, "dataset_collector_write_failed - provider={Provider}", providerType);
        }
    }

    /// <summary>Line counts per provider, for the hidden training page's dataset summary.</summary>
    public (int DeepLCount, int GoogleCount) GetCounts()
    {
        lock (_gate)
        {
            return (CountLines("dataset_deepl.jsonl"), CountLines("dataset_google.jsonl"));
        }
    }

    private static int CountLines(string fileName)
    {
        var path = Path.Combine(DatasetDir, fileName);
        if (!File.Exists(path)) return 0;
        try { return File.ReadLines(path).Count(line => !string.IsNullOrWhiteSpace(line)); }
        catch { return 0; }
    }

    private sealed record DatasetEntry(string Source, string Target, string Provider, DateTimeOffset Timestamp);
}
