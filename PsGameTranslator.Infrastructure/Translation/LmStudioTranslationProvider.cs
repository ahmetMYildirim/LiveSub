using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

/// <summary>
/// Local LM Studio translation provider via its OpenAI-compatible API
/// (POST /v1/chat/completions, health via GET /v1/models).
/// </summary>
public sealed class LmStudioTranslationProvider : ITranslationProvider, ITranslationProviderDeepHealth
{
    private const string SystemPrompt =
        "You are a professional English-to-Turkish game subtitle translator. " +
        "Return ONLY valid JSON in the form {\"translation\":\"...\"}. " +
        "Translate the user's English subtitle into fluent Turkish. " +
        "Do not explain. Keep proper names, character names, place names and " +
        "known game-specific terms unchanged.";

    private readonly TranslationSettings _settings;
    private readonly TranslationPostProcessor _postProcessor;
    private readonly ILogger<LmStudioTranslationProvider> _logger;
    private readonly HttpClient _http;

    public LmStudioTranslationProvider(
        TranslationSettings settings,
        TranslationPostProcessor postProcessor,
        ILogger<LmStudioTranslationProvider> logger)
    {
        _settings = settings;
        _postProcessor = postProcessor;
        _logger = logger;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string ProviderName => string.IsNullOrWhiteSpace(_settings.LmStudioModel)
        ? "lmstudio"
        : $"lmstudio/{_settings.LmStudioModel}";

    public TranslationProviderType ProviderType => TranslationProviderType.LMStudio;

    public bool IsAvailable => true; // Health check probes the actual server.

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var url = _settings.LmStudioBaseUrl.TrimEnd('/') + "/v1/chat/completions";
        var stopwatch = Stopwatch.StartNew();
        var payload = JsonSerializer.Serialize(new
        {
            model = string.IsNullOrWhiteSpace(_settings.LmStudioModel) ? "local-model" : _settings.LmStudioModel,
            temperature = 0.0,
            max_tokens = 160,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = request.SourceText },
            },
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, _settings.LmStudioTimeoutMs)));

        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, timeoutCts.Token).ConfigureAwait(false);
            var rawResponse = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "LM Studio request failed - HTTP {Status}: {Body}",
                    (int)response.StatusCode, Truncate(rawResponse, 300));
                return Failure(request, stopwatch.ElapsedMilliseconds,
                    $"LM Studio returned HTTP {(int)response.StatusCode}. Is a model loaded?", rawResponse);
            }

            var messageContent = ExtractMessageContent(rawResponse);
            var jsonParseSucceeded = TryExtractJsonTranslation(messageContent, out var parsedTranslation);
            if (!jsonParseSucceeded)
                parsedTranslation = messageContent.Trim().Trim('"');

            var postProcessed = _postProcessor.Process(request.SourceText, parsedTranslation);
            if (string.IsNullOrWhiteSpace(postProcessed))
                return Failure(request, stopwatch.ElapsedMilliseconds,
                    "LM Studio returned an empty translation.", rawResponse, jsonParseSucceeded);

            return new TranslationResult
            {
                SourceText = request.SourceText,
                TranslatedText = postProcessed,
                SourceLanguage = request.SourceLanguage,
                TargetLanguage = request.TargetLanguage,
                ProviderName = ProviderName,
                DurationMs = stopwatch.ElapsedMilliseconds,
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
            return Failure(request, stopwatch.ElapsedMilliseconds,
                $"LM Studio timed out ({_settings.LmStudioTimeoutMs} ms).");
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            return Failure(request, stopwatch.ElapsedMilliseconds,
                $"LM Studio is not reachable at {_settings.LmStudioBaseUrl}. " +
                $"Start LM Studio and enable its local server. Detail: {exception.Message}");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _logger.LogError(exception, "LM Studio request failed unexpectedly");
            return Failure(request, stopwatch.ElapsedMilliseconds, $"LM Studio error: {exception.Message}");
        }
    }

    public async Task<TranslationProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var url = _settings.LmStudioBaseUrl.TrimEnd('/') + "/v1/models";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
                return Health(false, TranslationProviderStatus.Failed,
                    $"LM Studio /v1/models returned HTTP {(int)response.StatusCode}.", stopwatch.ElapsedMilliseconds);

            var modelCount = CountModels(body);
            return modelCount > 0
                ? Health(true, TranslationProviderStatus.Running,
                    $"LM Studio ready — {modelCount} model(s) available.", stopwatch.ElapsedMilliseconds)
                : Health(false, TranslationProviderStatus.NotConfigured,
                    "LM Studio server is running but no model is loaded.", stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return Health(false, TranslationProviderStatus.Unreachable,
                "LM Studio health check timed out.", stopwatch.ElapsedMilliseconds);
        }
        catch (HttpRequestException)
        {
            stopwatch.Stop();
            return Health(false, TranslationProviderStatus.ServerNotRunning,
                $"LM Studio is not reachable at {_settings.LmStudioBaseUrl}. Start LM Studio's local server.",
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return Health(false, TranslationProviderStatus.Failed, exception.Message, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>Deep check: endpoint reachable + model loaded + a tiny real completion works.</summary>
    public async Task<TranslationProviderHealth> CheckDeepHealthAsync(CancellationToken cancellationToken = default)
    {
        var basic = await CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        if (!basic.IsAvailable)
            return new TranslationProviderHealth
            {
                ProviderName = basic.ProviderName,
                ProviderType = basic.ProviderType,
                IsAvailable = false,
                Status = basic.Status,
                Message = $"✗ {basic.Message} Fix: start LM Studio, enable its local server and load a model.",
                DurationMs = basic.DurationMs,
                ConfigurationStatus = basic.ConfigurationStatus,
            };

        var stopwatch = Stopwatch.StartNew();
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

        return Health(
            inference.Success,
            inference.Success ? TranslationProviderStatus.Running : TranslationProviderStatus.Failed,
            inference.Success
                ? $"✓ endpoint reachable ✓ model loaded ✓ inference works ({inference.DurationMs} ms)"
                : $"✗ inference failed: {inference.ErrorMessage}",
            stopwatch.ElapsedMilliseconds);
    }

    private TranslationProviderHealth Health(
        bool available, TranslationProviderStatus status, string message, long durationMs) => new()
    {
        ProviderName = ProviderName,
        ProviderType = ProviderType,
        IsAvailable = available,
        Status = status,
        Message = message,
        DurationMs = durationMs,
        ConfigurationStatus = available ? "Configured" : message,
    };

    private static int CountModels(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("data", out var data) &&
                   data.ValueKind == JsonValueKind.Array
                ? data.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string ExtractMessageContent(string rawResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
                return content.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            // Fall through to empty.
        }
        return string.Empty;
    }

    private static bool TryExtractJsonTranslation(string text, out string translation)
    {
        translation = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Models sometimes wrap the JSON in markdown fences.
        var cleaned = text.Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
                          .Replace("```", string.Empty)
                          .Trim();
        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("translation", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                translation = value.GetString()?.Trim() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(translation);
            }
        }
        catch (JsonException)
        {
            return false;
        }
        return false;
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
        Success = false,
        ErrorMessage = error,
        RawResponse = rawResponse,
        JsonParseSucceeded = jsonParseSucceeded,
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
