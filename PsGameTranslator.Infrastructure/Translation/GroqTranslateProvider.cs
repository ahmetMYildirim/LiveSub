using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

/// <summary>
/// Groq — fast inference of open models (Llama, etc.) via an OpenAI-compatible
/// chat completions API. Requires an API key from console.groq.com (free tier
/// available). Unavailable (never called) when no key is configured.
/// </summary>
public sealed class GroqTranslateProvider : ITranslationProvider
{
    private const string Endpoint = "https://api.groq.com/openai/v1/chat/completions";
    private const int TimeoutMs = 8000;

    private const string SystemPrompt =
        "You are a professional English-to-Turkish video game subtitle translator. " +
        "Translate the user's line into natural, fluent, spoken Turkish that matches " +
        "the tone and register of game dialogue (casual when casual, dramatic when " +
        "dramatic — never stiff or overly literal). Preserve the meaning exactly; " +
        "do not add, omit, censor or explain anything, and do not translate proper " +
        "names, character names or place names. Return ONLY valid JSON in exactly " +
        "this form: {\"translation\":\"<the Turkish translation>\"}.";

    private readonly TranslationSettings _settings;
    private readonly TranslationPostProcessor _postProcessor;
    private readonly ILogger<GroqTranslateProvider> _logger;
    private readonly HttpClient _http;

    public GroqTranslateProvider(
        TranslationSettings settings,
        TranslationPostProcessor postProcessor,
        ILogger<GroqTranslateProvider> logger)
    {
        _settings = settings;
        _postProcessor = postProcessor;
        _logger = logger;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    private bool HasApiKey => !string.IsNullOrWhiteSpace(_settings.GroqApiKey);

    public string ProviderName => $"groq/{_settings.GroqModel}";
    public TranslationProviderType ProviderType => TranslationProviderType.Groq;
    public bool IsAvailable => HasApiKey;

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!HasApiKey)
            return Failure(request, 0, "Groq is not configured. Paste an API key from console.groq.com.");

        var stopwatch = Stopwatch.StartNew();
        var payload = JsonSerializer.Serialize(new
        {
            model = _settings.GroqModel,
            temperature = 0.0,
            max_tokens = 200,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = request.SourceText },
            },
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(TimeoutMs));

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.GroqApiKey);

            using var response = await _http.SendAsync(httpRequest, timeoutCts.Token).ConfigureAwait(false);
            var rawResponse = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Groq request failed - HTTP {Status}: {Body}",
                    (int)response.StatusCode, Truncate(rawResponse, 300));
                return Failure(request, stopwatch.ElapsedMilliseconds,
                    $"Groq returned HTTP {(int)response.StatusCode}: {ExtractErrorMessage(rawResponse)}", rawResponse);
            }

            var messageContent = ExtractMessageContent(rawResponse);
            var jsonParseSucceeded = TryExtractJsonTranslation(messageContent, out var parsedTranslation);
            if (!jsonParseSucceeded)
                parsedTranslation = messageContent.Trim().Trim('"');

            var postProcessed = _postProcessor.Process(request.SourceText, parsedTranslation);
            if (string.IsNullOrWhiteSpace(postProcessed))
                return Failure(request, stopwatch.ElapsedMilliseconds,
                    "Groq returned an empty translation.", rawResponse, jsonParseSucceeded);

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
            return Failure(request, stopwatch.ElapsedMilliseconds, $"Groq timed out ({TimeoutMs} ms).");
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            return Failure(request, stopwatch.ElapsedMilliseconds,
                $"Groq is not reachable. Check your internet connection. Detail: {exception.Message}");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _logger.LogError(exception, "Groq request failed unexpectedly");
            return Failure(request, stopwatch.ElapsedMilliseconds, $"Groq error: {exception.Message}");
        }
    }

    public async Task<TranslationProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!HasApiKey)
            return Health(false, TranslationProviderStatus.MissingApiKey,
                "Groq is not configured. Paste an API key from console.groq.com.", 0);

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
                    $"Groq reachable ({result.DurationMs} ms).", stopwatch.ElapsedMilliseconds)
                : Health(false, TranslationProviderStatus.Unreachable,
                    result.ErrorMessage ?? "Groq health check failed.", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return Health(false, TranslationProviderStatus.Failed, exception.Message, stopwatch.ElapsedMilliseconds);
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
