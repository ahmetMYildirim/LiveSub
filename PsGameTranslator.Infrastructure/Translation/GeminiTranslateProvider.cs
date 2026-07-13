using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

/// <summary>
/// Google Gemini (Generative Language API) as a translation provider. Requires
/// an API key from aistudio.google.com (free tier available). Unavailable
/// (never called) when no key is configured.
/// </summary>
public sealed class GeminiTranslateProvider : ITranslationProvider
{
    private const int TimeoutMs = 8000;

    private const string SystemPrompt =
        "You are a professional English-to-Turkish game subtitle translator. " +
        "Return ONLY valid JSON in the form {\"translation\":\"...\"}. " +
        "Translate the user's English subtitle into fluent Turkish. " +
        "Do not explain. Keep proper names, character names, place names and " +
        "known game-specific terms unchanged.";

    private readonly TranslationSettings _settings;
    private readonly TranslationPostProcessor _postProcessor;
    private readonly ILogger<GeminiTranslateProvider> _logger;
    private readonly HttpClient _http;

    public GeminiTranslateProvider(
        TranslationSettings settings,
        TranslationPostProcessor postProcessor,
        ILogger<GeminiTranslateProvider> logger)
    {
        _settings = settings;
        _postProcessor = postProcessor;
        _logger = logger;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    private bool HasApiKey => !string.IsNullOrWhiteSpace(_settings.GeminiApiKey);

    public string ProviderName => $"gemini/{_settings.GeminiModel}";
    public TranslationProviderType ProviderType => TranslationProviderType.Gemini;
    public bool IsAvailable => HasApiKey;

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!HasApiKey)
            return Failure(request, 0, "Gemini is not configured. Paste an API key from aistudio.google.com.");

        var stopwatch = Stopwatch.StartNew();
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_settings.GeminiModel}:generateContent" +
                  $"?key={HttpUtility.UrlEncode(_settings.GeminiApiKey)}";
        var payload = JsonSerializer.Serialize(new
        {
            contents = new object[]
            {
                new
                {
                    role = "user",
                    parts = new object[] { new { text = $"{SystemPrompt}\n\nSubtitle: {request.SourceText}" } },
                },
            },
            generationConfig = new { temperature = 0.0, maxOutputTokens = 200 },
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(TimeoutMs));

        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, timeoutCts.Token).ConfigureAwait(false);
            var rawResponse = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Gemini request failed - HTTP {Status}: {Body}",
                    (int)response.StatusCode, Truncate(rawResponse, 300));
                return Failure(request, stopwatch.ElapsedMilliseconds,
                    $"Gemini returned HTTP {(int)response.StatusCode}: {ExtractErrorMessage(rawResponse)}", rawResponse);
            }

            var messageContent = ExtractMessageContent(rawResponse);
            var jsonParseSucceeded = TryExtractJsonTranslation(messageContent, out var parsedTranslation);
            if (!jsonParseSucceeded)
                parsedTranslation = messageContent.Trim().Trim('"');

            var postProcessed = _postProcessor.Process(request.SourceText, parsedTranslation);
            if (string.IsNullOrWhiteSpace(postProcessed))
                return Failure(request, stopwatch.ElapsedMilliseconds,
                    "Gemini returned an empty translation.", rawResponse, jsonParseSucceeded);

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
            return Failure(request, stopwatch.ElapsedMilliseconds, $"Gemini timed out ({TimeoutMs} ms).");
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            return Failure(request, stopwatch.ElapsedMilliseconds,
                $"Gemini is not reachable. Check your internet connection. Detail: {exception.Message}");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _logger.LogError(exception, "Gemini request failed unexpectedly");
            return Failure(request, stopwatch.ElapsedMilliseconds, $"Gemini error: {exception.Message}");
        }
    }

    public async Task<TranslationProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!HasApiKey)
            return Health(false, TranslationProviderStatus.MissingApiKey,
                "Gemini is not configured. Paste an API key from aistudio.google.com.", 0);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await TranslateAsync(new TranslationRequest
            {
                SourceText = "Hello.",
                SourceLanguage = _settings.SourceLanguage,
                TargetLanguage = _settings.TargetLanguage,
            }, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            return result.Success
                ? Health(true, TranslationProviderStatus.Running,
                    $"Gemini reachable ({result.DurationMs} ms).", stopwatch.ElapsedMilliseconds)
                : Health(false, TranslationProviderStatus.Unreachable,
                    result.ErrorMessage ?? "Gemini health check failed.", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return Health(false, TranslationProviderStatus.Failed, exception.Message, stopwatch.ElapsedMilliseconds);
        }
    }

    // Response shape: {"candidates":[{"content":{"parts":[{"text":"..."}]}}]}
    private static string ExtractMessageContent(string rawResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
                candidates.ValueKind == JsonValueKind.Array &&
                candidates.GetArrayLength() > 0 &&
                candidates[0].TryGetProperty("content", out var contentEl) &&
                contentEl.TryGetProperty("parts", out var parts) &&
                parts.ValueKind == JsonValueKind.Array &&
                parts.GetArrayLength() > 0 &&
                parts[0].TryGetProperty("text", out var text))
                return text.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            // Fall through to empty.
        }
        return string.Empty;
    }

    // Error shape: {"error":{"code":400,"message":"...","status":"..."}}
    private static string ExtractErrorMessage(string rawResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
                return message.GetString() ?? string.Empty;
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
