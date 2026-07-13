using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

public sealed class OllamaTranslationService : ITranslationService, ITranslationProvider, ITranslationProviderDeepHealth
{
    public string ProviderName => $"ollama/{_settings.OllamaModel}";
    public TranslationProviderType ProviderType => TranslationProviderType.Ollama;

    private const string PromptTemplate = """
        You are a professional English-to-Turkish game subtitle translator.

        Return only valid JSON:
        {"translation":"..."}

        Translate the English subtitle into fluent Turkish.

        Rules:
        - Do not explain.
        - Do not reason.
        - Do not invent words.
        - Use correct Turkish spelling and grammar.
        - Translate common fantasy words into Turkish.
        - Keep only real proper names, character names, place names, item names, skill names, and known game-specific terms unchanged.

        Glossary:
        dragon = ejderha
        dragon's = ejderhanın
        dragon's fury = ejderhanın öfkesi
        fury = öfke
        marks = izler
        more = daha fazla
        beast = canavar
        Arisen = Arisen
        Pawn = Pawn
        Sovran = Sovran
        Vermund = Vermund
        Battahl = Battahl

        Good examples:
        English: More marks of the dragon's fury.
        Turkish: Ejderhanın öfkesinden kalan daha fazla iz.

        English: I have never seen such a beast before.
        Turkish: Böyle bir canavarı daha önce hiç görmemiştim.

        English: The Arisen must return to Vermund and speak with the Pawn.
        Turkish: Arisen, Vermund'a dönüp Pawn ile konuşmalı.

        Subtitle:
        {TEXT}
        """;

    private readonly TranslationSettings _settings;
    private readonly TranslationPostProcessor _postProcessor;
    private readonly PipelineDiagnostics _diagnostics;
    private readonly PipelineDiagnosticsStore _diagnosticsStore;
    private readonly ILogger<OllamaTranslationService> _logger;
    private readonly HttpClient _http;

    public OllamaTranslationService(
        TranslationSettings settings,
        TranslationPostProcessor postProcessor,
        PipelineDiagnostics diagnostics,
        PipelineDiagnosticsStore diagnosticsStore,
        ILogger<OllamaTranslationService> logger)
    {
        _settings = settings;
        _postProcessor = postProcessor;
        _diagnostics = diagnostics;
        _diagnosticsStore = diagnosticsStore;
        _logger = logger;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var url = _settings.OllamaBaseUrl.TrimEnd('/') + "/api/generate";
        var stopwatch = Stopwatch.StartNew();
        var payload = JsonSerializer.Serialize(new
        {
            model = _settings.OllamaModel,
            stream = false,
            think = false,
            format = "json",
            prompt = BuildPrompt(request.SourceText),
            options = new
            {
                temperature = 0.0,
                top_p = 0.5,
                num_predict = 80,
                repeat_penalty = 1.1,
            },
        });

        _diagnostics.OllamaBaseUrl = _settings.OllamaBaseUrl;
        _diagnostics.OllamaModel = _settings.OllamaModel;
        _diagnostics.LastOllamaRequestBodyPreview = Truncate(payload, 2000);
        _diagnosticsStore.Save();

        _logger.LogInformation(
            "Ollama request started - model={Model}, url={Url}, text={Text}",
            _settings.OllamaModel, url, Truncate(request.SourceText, 60));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(500, _settings.TranslationTimeoutMs)));

        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, timeoutCts.Token).ConfigureAwait(false);
            var responseBytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
            var rawResponse = Encoding.UTF8.GetString(responseBytes);
            stopwatch.Stop();

            _diagnostics.OllamaReachable = true;
            _diagnostics.LastOllamaStatusCode = (int)response.StatusCode;
            _diagnostics.LastOllamaResponsePreview = Truncate(rawResponse, 2000);
            _diagnosticsStore.Save();

            _logger.LogDebug("Raw Ollama response: {RawResponse}", Truncate(rawResponse, 2000));

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Ollama request failed - HTTP {Status}: {Body}",
                    (int)response.StatusCode, Truncate(rawResponse, 300));
                return Failure(request, stopwatch.ElapsedMilliseconds,
                    $"Ollama returned HTTP {(int)response.StatusCode}.", rawResponse);
            }

            var responseText = ExtractResponseProperty(rawResponse);
            if (TryExtractOllamaError(responseText, out var ollamaError))
            {
                _logger.LogWarning(
                    "Ollama returned a JSON generation error: {Error}. Raw response: {RawResponse}",
                    ollamaError, Truncate(rawResponse, 2000));
                return Failure(
                    request,
                    stopwatch.ElapsedMilliseconds,
                    $"Ollama JSON generation failed: {ollamaError}",
                    rawResponse);
            }

            var jsonParseSucceeded = TryExtractJsonTranslation(responseText, out var parsedTranslation);
            if (!jsonParseSucceeded)
            {
                _logger.LogWarning(
                    "Ollama JSON parse failed; using fallback cleaner. Raw response: {RawResponse}",
                    Truncate(rawResponse, 2000));
                parsedTranslation = CleanFallback(responseText);
            }

            var postProcessed = _postProcessor.Process(request.SourceText, parsedTranslation);

            _logger.LogInformation(
                "Ollama translation diagnostics - parsed={Parsed}, postProcessed={PostProcessed}, " +
                "durationMs={DurationMs}, fromCache={FromCache}, jsonParseSuccess={JsonParseSuccess}",
                Truncate(parsedTranslation, 200), Truncate(postProcessed, 200),
                stopwatch.ElapsedMilliseconds, false, jsonParseSucceeded);

            if (string.IsNullOrWhiteSpace(postProcessed))
                return Failure(request, stopwatch.ElapsedMilliseconds,
                    "Ollama returned an empty translation.", rawResponse, jsonParseSucceeded);

            return new TranslationResult
            {
                SourceText = request.SourceText,
                TranslatedText = postProcessed,
                SourceLanguage = request.SourceLanguage,
                TargetLanguage = request.TargetLanguage,
                ProviderName = ProviderName,
                DurationMs = stopwatch.ElapsedMilliseconds,
                FromCache = false,
                Success = true,
                RawResponse = rawResponse,
                ParsedTranslation = parsedTranslation,
                PostProcessedTranslation = postProcessed,
                JsonParseSucceeded = jsonParseSucceeded,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _diagnostics.OllamaReachable = false;
            _diagnostics.LastOllamaStatusCode = null;
            _diagnosticsStore.Save();
            _logger.LogWarning("Ollama request timed out after {TimeoutMs} ms", _settings.TranslationTimeoutMs);
            return Failure(request, stopwatch.ElapsedMilliseconds,
                $"Translation timed out ({_settings.TranslationTimeoutMs} ms).");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _diagnostics.OllamaReachable = false;
            _diagnostics.LastOllamaStatusCode = null;
            _diagnosticsStore.Save();
            _logger.LogWarning("Ollama request failed - server unreachable: {Message}", ex.Message);
            return Failure(request, stopwatch.ElapsedMilliseconds,
                "Ollama is not reachable. Start Ollama or disable translation.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _diagnosticsStore.Save();
            _logger.LogError(ex, "Ollama request failed unexpectedly");
            return Failure(request, stopwatch.ElapsedMilliseconds, $"Translation error: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var url = _settings.OllamaBaseUrl.TrimEnd('/') + "/api/tags";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
            _diagnostics.OllamaBaseUrl = _settings.OllamaBaseUrl;
            _diagnostics.OllamaModel = _settings.OllamaModel;
            _diagnostics.OllamaReachable = true;
            _diagnostics.LastOllamaStatusCode = (int)response.StatusCode;
            _diagnosticsStore.Save();
            if (!response.IsSuccessStatusCode)
                return (false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            var body = Encoding.UTF8.GetString(bytes);
            using var document = JsonDocument.Parse(body);
            var models = document.RootElement.TryGetProperty("models", out var modelList)
                ? modelList.GetArrayLength() : 0;
            var hasModel = body.Contains(_settings.OllamaModel, StringComparison.OrdinalIgnoreCase);
            return (true, hasModel
                ? $"OK - {models} model(s), '{_settings.OllamaModel}' found"
                : $"OK - {models} model(s), but '{_settings.OllamaModel}' not found. Run: ollama pull {_settings.OllamaModel}");
        }
        catch (OperationCanceledException)
        {
            _diagnostics.OllamaReachable = false;
            _diagnosticsStore.Save();
            return (false, "Connection timed out");
        }
        catch (HttpRequestException)
        {
            _diagnostics.OllamaReachable = false;
            _diagnosticsStore.Save();
            return (false, "Ollama is not reachable. Start Ollama or disable translation.");
        }
        catch (Exception ex) { return (false, $"Error: {ex.Message}"); }
    }

    public async Task<TranslationProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var (success, message) = await TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        return new TranslationProviderHealth
        {
            ProviderName = ProviderName,
            ProviderType = ProviderType,
            IsAvailable = success,
            Status = success ? TranslationProviderStatus.Running : TranslationProviderStatus.Unreachable,
            Message = message,
            DurationMs = stopwatch.ElapsedMilliseconds,
            ConfigurationStatus = success ? "Configured" : "Server unreachable or model missing",
        };
    }

    /// <summary>Deep check: server reachable + selected model exists + a tiny real generation works.</summary>
    public async Task<TranslationProviderHealth> CheckDeepHealthAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var (reachable, message) = await TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!reachable)
        {
            stopwatch.Stop();
            return DeepHealth(false, TranslationProviderStatus.Unreachable,
                $"✗ {message} Fix: start Ollama (ollama serve) or check OllamaBaseUrl.",
                stopwatch.ElapsedMilliseconds);
        }
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            stopwatch.Stop();
            return DeepHealth(false, TranslationProviderStatus.NotConfigured,
                $"✗ {message} Fix: pull the model in Model Manager.",
                stopwatch.ElapsedMilliseconds);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(25));
        TranslationResult inference;
        try
        {
            inference = await TranslateAsync(new TranslationRequest
            {
                SourceText = "Hello.",
                SourceLanguage = _settings.SourceLanguage,
                TargetLanguage = _settings.TargetLanguage,
            }, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            inference = new TranslationResult { Success = false, ErrorMessage = "Inference test timed out (25 s)." };
        }
        stopwatch.Stop();

        return DeepHealth(inference.Success,
            inference.Success ? TranslationProviderStatus.Running : TranslationProviderStatus.Failed,
            inference.Success
                ? $"✓ server reachable ✓ model exists ✓ inference works ({inference.DurationMs} ms) — {_settings.OllamaModel}"
                : $"✗ inference failed: {inference.ErrorMessage}",
            stopwatch.ElapsedMilliseconds);
    }

    private TranslationProviderHealth DeepHealth(
        bool available, TranslationProviderStatus status, string message, long durationMs) => new()
    {
        ProviderName = ProviderName,
        ProviderType = ProviderType,
        IsAvailable = available,
        Status = status,
        Message = message,
        DurationMs = durationMs,
        ConfigurationStatus = available ? "Fully operational" : message,
    };

    private static string BuildPrompt(string sourceText) =>
        PromptTemplate.Replace("{TEXT}", sourceText, StringComparison.Ordinal);

    private static string ExtractResponseProperty(string rawResponse)
    {
        using var document = JsonDocument.Parse(rawResponse);
        return document.RootElement.TryGetProperty("response", out var response)
            ? response.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool TryExtractJsonTranslation(string responseText, out string translation)
    {
        translation = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(responseText);
            translation = FindFirstNonEmptyTranslation(document.RootElement);
            return !string.IsNullOrWhiteSpace(translation);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string FindFirstNonEmptyTranslation(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("translation") &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString()?.Trim() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }

                var nested = FindFirstNonEmptyTranslation(property.Value);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindFirstNonEmptyTranslation(item);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }

        return string.Empty;
    }

    private static bool TryExtractOllamaError(string responseText, out string error)
    {
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(responseText);
            if (!document.RootElement.TryGetProperty("error", out var value) ||
                value.ValueKind != JsonValueKind.String)
                return false;

            error = value.GetString()?.Trim() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(error);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CleanFallback(string rawOutput)
    {
        var text = Regex.Replace(rawOutput, @"<think>[\s\S]*?</think>", string.Empty,
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"```(?:json)?|```", string.Empty, RegexOptions.IgnoreCase);

        var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Select(line => Regex.Replace(line.Trim(),
                @"^(?:Translation|Turkish|Çeviri)\s*:\s*", string.Empty,
                RegexOptions.IgnoreCase))
            .Where(line => !string.IsNullOrWhiteSpace(line) &&
                !Regex.IsMatch(line, @"^(?:thinking|reasoning)\s*:", RegexOptions.IgnoreCase))
            .ToArray();

        if (lines.Length == 0)
            return string.Empty;

        var lastLine = lines[^1].Trim();
        var jsonLike = Regex.Match(lastLine,
            "[\\\"']translation[\\\"']\\s*:\\s*[\\\"'](?<value>.*?)[\\\"']\\s*}?$",
            RegexOptions.IgnoreCase);
        return (jsonLike.Success ? jsonLike.Groups["value"].Value : lastLine).Trim(' ', '"', '\'');
    }

    private TranslationResult Failure(
        TranslationRequest request,
        long durationMs,
        string error,
        string rawResponse = "",
        bool jsonParseSucceeded = false) => new()
    {
        SourceText = request.SourceText,
        SourceLanguage = request.SourceLanguage,
        TargetLanguage = request.TargetLanguage,
        ProviderName = ProviderName,
        DurationMs = durationMs,
        FromCache = false,
        Success = false,
        ErrorMessage = error,
        RawResponse = rawResponse,
        JsonParseSucceeded = jsonParseSucceeded,
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
