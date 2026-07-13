using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Ocr;

namespace PsGameTranslator.Infrastructure.Translation;

public enum ModelInstallState
{
    Unknown,
    NotInstalled,
    Installed,
    Downloading,
    Failed,
}

/// <summary>
/// Installs, verifies and removes translation models without a terminal:
/// HuggingFace models via the project's Python environment (huggingface_hub),
/// Ollama models via the Ollama HTTP API (/api/pull, /api/delete).
/// </summary>
public sealed class ModelInstallService
{
    private readonly PythonEnvironmentService _python;
    private readonly TranslationSettings _settings;
    private readonly ILogger<ModelInstallService> _logger;
    private readonly HttpClient _http;

    public ModelInstallService(
        PythonEnvironmentService python,
        TranslationSettings settings,
        ILogger<ModelInstallService> logger)
    {
        _python = python;
        _settings = settings;
        _logger = logger;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    // ── HuggingFace models (machine translation server) ─────────────────────────

    /// <summary>
    /// The fine-tuned entry isn't a HuggingFace Hub repo — it's a local LoRA
    /// adapter directory produced by the Training page. Treating it like a
    /// downloadable repo (snapshot_download by repo id) always reports
    /// NotInstalled and makes Install fail, which also keeps Use permanently
    /// disabled. Resolve/inspect it on disk instead.
    /// </summary>
    private static bool IsFineTunedSentinel(string modelId) =>
        string.Equals(modelId, TranslationModelCatalog.FineTunedModelSentinel, StringComparison.OrdinalIgnoreCase);

    private static string ResolveFineTunedPath() =>
        Path.Combine(AppContext.BaseDirectory, "models", TranslationModelCatalog.FineTunedModelSentinel);

    /// <summary>
    /// True once training output exists, whether it's still a raw LoRA adapter
    /// (adapter_config.json) or has been merged into a standalone model
    /// (config.json + model.safetensors) for faster inference — see
    /// MergeAdapterIntoBaseModel below.
    /// </summary>
    private static bool FineTunedAdapterExists(string path) =>
        File.Exists(Path.Combine(path, "adapter_config.json")) ||
        (File.Exists(Path.Combine(path, "config.json")) && File.Exists(Path.Combine(path, "model.safetensors")));

    public async Task<ModelInstallState> GetHuggingFaceStateAsync(
        string modelId, CancellationToken ct = default)
    {
        if (IsFineTunedSentinel(modelId))
            return FineTunedAdapterExists(ResolveFineTunedPath())
                ? ModelInstallState.Installed
                : ModelInstallState.NotInstalled;

        var (success, _) = await _python.RunSnippetAsync(
            $"from huggingface_hub import snapshot_download; snapshot_download('{modelId}', local_files_only=True)",
            timeout: TimeSpan.FromSeconds(30), ct: ct).ConfigureAwait(false);
        return success ? ModelInstallState.Installed : ModelInstallState.NotInstalled;
    }

    public async Task<(bool Success, string Message)> InstallHuggingFaceAsync(
        string modelId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (IsFineTunedSentinel(modelId))
        {
            var path = ResolveFineTunedPath();
            return FineTunedAdapterExists(path)
                ? (true, $"Zaten eğitilmiş, yerel olarak mevcut: {path}")
                : (false, "Bu model henüz eğitilmedi. Önce Eğitim sayfasından fine-tune başlatın.");
        }

        _logger.LogInformation("hf_model_install_started - {Model}", modelId);
        progress?.Report($"Downloading {modelId} from HuggingFace…");
        var (success, output) = await _python.RunSnippetAsync(
            $"from huggingface_hub import snapshot_download; " +
            $"print(snapshot_download('{modelId}'))",
            progress, TimeSpan.FromHours(2), ct).ConfigureAwait(false);

        if (success)
        {
            _logger.LogInformation("hf_model_install_completed - {Model}", modelId);
            return (true, $"Downloaded to {output.Split('\n').LastOrDefault()?.Trim()}");
        }
        _logger.LogWarning("hf_model_install_failed - {Model}: {Output}", modelId, Truncate(output, 400));
        return (false, $"Download failed: {Truncate(output, 400)}");
    }

    public async Task<(bool Success, string Message)> RemoveHuggingFaceAsync(
        string modelId, CancellationToken ct = default)
    {
        if (IsFineTunedSentinel(modelId))
        {
            var path = ResolveFineTunedPath();
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return (true, "Yerel fine-tune adaptörü kaldırıldı.");
            }
            catch (Exception exception)
            {
                return (false, $"Kaldırma başarısız: {exception.Message}");
            }
        }

        var code =
            "from huggingface_hub import scan_cache_dir\n" +
            "cache = scan_cache_dir()\n" +
            $"repos = [r for r in cache.repos if r.repo_id == '{modelId}']\n" +
            "if not repos:\n" +
            "    print('not installed'); raise SystemExit(1)\n" +
            "revisions = [rev.commit_hash for r in repos for rev in r.revisions]\n" +
            "strategy = cache.delete_revisions(*revisions)\n" +
            "strategy.execute()\n" +
            "print(f'freed {strategy.expected_freed_size_str}')";
        var (success, output) = await _python.RunSnippetAsync(
            code, timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false);
        return success ? (true, $"Removed ({output})") : (false, $"Remove failed: {Truncate(output, 300)}");
    }

    /// <summary>Verify = the model can be fully loaded by transformers (tokenizer + weights).</summary>
    public async Task<(bool Success, string Message)> VerifyHuggingFaceAsync(
        string modelId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (IsFineTunedSentinel(modelId))
        {
            var path = ResolveFineTunedPath();
            var pathForPython = path.Replace('\\', '/');
            var isMerged = File.Exists(Path.Combine(path, "config.json")) && !File.Exists(Path.Combine(path, "adapter_config.json"));
            progress?.Report(isMerged
                ? "Birleştirilmiş fine-tune modeli doğrulanıyor…"
                : "Fine-tune adaptörü doğrulanıyor (base model + LoRA yükleniyor)…");
            var code = isMerged
                ? "from transformers import AutoTokenizer, AutoModelForSeq2SeqLM\n" +
                  $"tok = AutoTokenizer.from_pretrained('{pathForPython}', local_files_only=True)\n" +
                  $"model = AutoModelForSeq2SeqLM.from_pretrained('{pathForPython}', local_files_only=True)\n" +
                  "print('ok: merged model load')"
                : "import json\n" +
                  "from transformers import AutoTokenizer, AutoModelForSeq2SeqLM\n" +
                  "from peft import PeftModel\n" +
                  $"with open('{pathForPython}/adapter_config.json') as f: cfg = json.load(f)\n" +
                  "base_id = cfg.get('base_model_name_or_path', 'Helsinki-NLP/opus-mt-tc-big-en-tr')\n" +
                  "tok = AutoTokenizer.from_pretrained(base_id, local_files_only=True)\n" +
                  "base = AutoModelForSeq2SeqLM.from_pretrained(base_id, local_files_only=True)\n" +
                  $"model = PeftModel.from_pretrained(base, '{pathForPython}')\n" +
                  "print('ok: base + LoRA adapter load')";
            var (ok, output) = await _python.RunSnippetAsync(
                code, progress, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            return ok
                ? (true, "✓ Doğrulandı: model yükleniyor.")
                : (false, $"✗ Doğrulama başarısız: {Truncate(output, 300)}");
        }

        progress?.Report($"Verifying {modelId} (loading tokenizer + weights)…");
        var (success, output2) = await _python.RunSnippetAsync(
            "from transformers import AutoTokenizer, AutoModelForSeq2SeqLM\n" +
            $"tok = AutoTokenizer.from_pretrained('{modelId}', local_files_only=True)\n" +
            $"model = AutoModelForSeq2SeqLM.from_pretrained('{modelId}', local_files_only=True)\n" +
            "print('ok: tokenizer + model load')",
            progress, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        return success
            ? (true, "✓ Verified: tokenizer and weights load.")
            : (false, $"✗ Verify failed: {Truncate(output2, 300)}");
    }

    // ── Ollama models ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetOllamaInstalledModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            var json = await _http.GetStringAsync(
                _settings.OllamaBaseUrl.TrimEnd('/') + "/api/tags", cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var list))
                foreach (var model in list.EnumerateArray())
                    if (model.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } n)
                        models.Add(n);
            return models;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Streams `ollama pull` progress via the HTTP API.</summary>
    public async Task<(bool Success, string Message)> PullOllamaModelAsync(
        string modelName, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var url = _settings.OllamaBaseUrl.TrimEnd('/') + "/api/pull";
            using var content = new StringContent(
                JsonSerializer.Serialize(new { model = modelName, stream = true }),
                Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return (false, $"Ollama HTTP {(int)response.StatusCode} döndürdü.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            string? lastStatus = null;
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("error", out var error))
                        return (false, error.GetString() ?? "Ollama indirme başarısız oldu.");
                    if (doc.RootElement.TryGetProperty("status", out var status))
                    {
                        lastStatus = status.GetString();
                        var detail = lastStatus;
                        if (doc.RootElement.TryGetProperty("completed", out var completed) &&
                            doc.RootElement.TryGetProperty("total", out var total) &&
                            total.GetInt64() > 0)
                        {
                            var percent = 100.0 * completed.GetInt64() / total.GetInt64();
                            detail = $"{lastStatus} {percent:F0}%";
                        }
                        progress?.Report(detail ?? string.Empty);
                    }
                }
                catch (JsonException) { /* partial line */ }
            }

            var success = string.Equals(lastStatus, "success", StringComparison.OrdinalIgnoreCase);
            return success ? (true, $"{modelName} çekildi.") : (false, $"Çekme işlemi şu durumla bitti: {lastStatus}");
        }
        catch (HttpRequestException)
        {
            return (false, "Ollama'ya ulaşılamıyor. Önce Ollama'yı başlatın.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Çekme işlemi iptal edildi veya zaman aşımına uğradı.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "ollama_pull_failed - {Model}", modelName);
            return (false, exception.Message);
        }
    }

    public async Task<(bool Success, string Message)> RemoveOllamaModelAsync(
        string modelName, CancellationToken ct = default)
    {
        try
        {
            var url = _settings.OllamaBaseUrl.TrimEnd('/') + "/api/delete";
            using var request = new HttpRequestMessage(HttpMethod.Delete, url)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { model = modelName }),
                    Encoding.UTF8, "application/json"),
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (true, $"Removed {modelName}.")
                : (false, $"Ollama returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
