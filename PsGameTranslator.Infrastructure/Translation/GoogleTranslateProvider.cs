using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

/// <summary>
/// Google Translate provider with two backends:
///  - No API key configured: the free, unofficial "gtx" client endpoint used by
///    the public translate.googleapis.com web widget. Not officially supported;
///    Google can change or rate-limit it without notice.
///  - API key configured (TranslationSettings.GoogleTranslateApiKey): the
///    official Cloud Translation API v2 (translation.googleapis.com), billed
///    per Google Cloud pricing with a 500,000 char/month free tier.
/// </summary>
public sealed class GoogleTranslateProvider : ITranslationProvider
{
    private const string FreeEndpoint = "https://translate.googleapis.com/translate_a/single";
    private const string OfficialEndpoint = "https://translation.googleapis.com/language/translate/v2";
    private const int TimeoutMs = 6000;

    private readonly TranslationSettings _settings;
    private readonly TranslationPostProcessor _postProcessor;
    private readonly ILogger<GoogleTranslateProvider> _logger;
    private readonly HttpClient _http;

    public GoogleTranslateProvider(
        TranslationSettings settings,
        TranslationPostProcessor postProcessor,
        ILogger<GoogleTranslateProvider> logger)
    {
        _settings = settings;
        _postProcessor = postProcessor;
        _logger = logger;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    private bool HasApiKey => !string.IsNullOrWhiteSpace(_settings.GoogleTranslateApiKey);

    public string ProviderName => HasApiKey ? "google-translate/official" : "google-translate/free";
    public TranslationProviderType ProviderType => TranslationProviderType.GoogleTranslate;
    public bool IsAvailable => true; // Health check probes the endpoint.

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var useOfficialApi = HasApiKey;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(TimeoutMs));

        try
        {
            using var response = useOfficialApi
                ? await _http.PostAsync(
                    $"{OfficialEndpoint}?key={HttpUtility.UrlEncode(_settings.GoogleTranslateApiKey)}",
                    new StringContent(JsonSerializer.Serialize(new
                    {
                        q = request.SourceText,
                        source = request.SourceLanguage,
                        target = request.TargetLanguage,
                        format = "text",
                    }), System.Text.Encoding.UTF8, "application/json"),
                    timeoutCts.Token).ConfigureAwait(false)
                : await _http.GetAsync(BuildFreeUrl(request.SourceLanguage, request.TargetLanguage, request.SourceText), timeoutCts.Token).ConfigureAwait(false);
            var rawResponse = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Google Translate request failed - HTTP {Status}: {Body}",
                    (int)response.StatusCode, Truncate(rawResponse, 300));
                return Failure(request, stopwatch.ElapsedMilliseconds,
                    $"Google Translate returned HTTP {(int)response.StatusCode}. {ExtractOfficialErrorMessage(rawResponse, useOfficialApi)}", rawResponse);
            }

            var parsedTranslation = useOfficialApi
                ? ExtractOfficialTranslation(rawResponse)
                : ExtractFreeTranslation(rawResponse);
            if (string.IsNullOrWhiteSpace(parsedTranslation))
                return Failure(request, stopwatch.ElapsedMilliseconds,
                    "Google Translate returned an empty translation.", rawResponse);

            var postProcessed = _postProcessor.Process(request.SourceText, parsedTranslation);

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
                JsonParseSucceeded = true,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return Failure(request, stopwatch.ElapsedMilliseconds, $"Google Translate timed out ({TimeoutMs} ms).");
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            return Failure(request, stopwatch.ElapsedMilliseconds,
                $"Google Translate is not reachable. Check your internet connection. Detail: {exception.Message}");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _logger.LogError(exception, "Google Translate request failed unexpectedly");
            return Failure(request, stopwatch.ElapsedMilliseconds, $"Google Translate error: {exception.Message}");
        }
    }

    public async Task<TranslationProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
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
                    $"Google Translate reachable ({result.DurationMs} ms).", stopwatch.ElapsedMilliseconds)
                : Health(false, TranslationProviderStatus.Unreachable,
                    result.ErrorMessage ?? "Google Translate health check failed.", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return Health(false, TranslationProviderStatus.Failed, exception.Message, stopwatch.ElapsedMilliseconds);
        }
    }

    private static string BuildFreeUrl(string sourceLanguage, string targetLanguage, string text)
    {
        var query = HttpUtility.UrlEncode(text);
        var sl = HttpUtility.UrlEncode(sourceLanguage);
        var tl = HttpUtility.UrlEncode(targetLanguage);
        return $"{FreeEndpoint}?client=gtx&sl={sl}&tl={tl}&dt=t&q={query}";
    }

    // Free-endpoint response shape: [[["translated chunk","source chunk",null,null,...], ...], ...]
    // Multiple chunks are concatenated back together in order.
    private static string ExtractFreeTranslation(string rawResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return string.Empty;

            var sentences = doc.RootElement[0];
            if (sentences.ValueKind != JsonValueKind.Array)
                return string.Empty;

            var parts = new List<string>();
            foreach (var sentence in sentences.EnumerateArray())
            {
                if (sentence.ValueKind == JsonValueKind.Array &&
                    sentence.GetArrayLength() > 0 &&
                    sentence[0].ValueKind == JsonValueKind.String)
                {
                    parts.Add(sentence[0].GetString() ?? string.Empty);
                }
            }
            return string.Concat(parts).Trim();
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    // Official Cloud Translation API v2 response shape:
    // {"data":{"translations":[{"translatedText":"..."}]}}
    private static string ExtractOfficialTranslation(string rawResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("translations", out var translations) &&
                translations.ValueKind == JsonValueKind.Array &&
                translations.GetArrayLength() > 0 &&
                translations[0].TryGetProperty("translatedText", out var text))
            {
                return System.Net.WebUtility.HtmlDecode(text.GetString() ?? string.Empty).Trim();
            }
        }
        catch (JsonException)
        {
            // Fall through to empty.
        }
        return string.Empty;
    }

    // Official API error shape: {"error":{"code":400,"message":"...","status":"..."}}
    private static string ExtractOfficialErrorMessage(string rawResponse, bool useOfficialApi)
    {
        if (!useOfficialApi) return string.Empty;
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
        TranslationRequest request, long durationMs, string error, string rawResponse = "") => new()
    {
        SourceText = request.SourceText,
        SourceLanguage = request.SourceLanguage,
        TargetLanguage = request.TargetLanguage,
        ProviderName = ProviderName,
        DurationMs = durationMs,
        Success = false,
        ErrorMessage = error,
        RawResponse = rawResponse,
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
