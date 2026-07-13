using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

/// <summary>
/// DeepL — dedicated translation API (no prompt engineering needed). Requires
/// an API key from deepl.com/pro-api (free tier available, 500,000 chars/month).
/// Free-tier keys end in ":fx" and must use the api-free host; paid keys use
/// the regular api host. Unavailable (never called) when no key is configured.
/// </summary>
public sealed class DeepLTranslateProvider : ITranslationProvider
{
    private const int TimeoutMs = 6000;

    private readonly TranslationSettings _settings;
    private readonly TranslationPostProcessor _postProcessor;
    private readonly ILogger<DeepLTranslateProvider> _logger;
    private readonly HttpClient _http;

    public DeepLTranslateProvider(
        TranslationSettings settings,
        TranslationPostProcessor postProcessor,
        ILogger<DeepLTranslateProvider> logger,
        HttpMessageHandler? httpMessageHandler = null)
    {
        _settings = settings;
        _postProcessor = postProcessor;
        _logger = logger;
        _http = httpMessageHandler is null
            ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient(httpMessageHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private bool HasApiKey => !string.IsNullOrWhiteSpace(_settings.DeepLApiKey);
    private bool IsFreeTierKey => _settings.DeepLApiKey.TrimEnd().EndsWith(":fx", StringComparison.OrdinalIgnoreCase);
    private string Endpoint => IsFreeTierKey
        ? "https://api-free.deepl.com/v2/translate"
        : "https://api.deepl.com/v2/translate";

    public string ProviderName => "deepl";
    public TranslationProviderType ProviderType => TranslationProviderType.DeepL;
    public bool IsAvailable => HasApiKey;

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!HasApiKey)
            return Failure(request, 0, "DeepL is not configured. Paste an API key from deepl.com/pro-api.");

        var stopwatch = Stopwatch.StartNew();
        var context = TranslationContextWindow.Join(request.PreviousContextLines);
        var payloadValues = new Dictionary<string, object?>
        {
            ["text"] = new[] { request.SourceText },
            ["source_lang"] = request.SourceLanguage.ToUpperInvariant(),
            ["target_lang"] = NormalizeTargetLang(request.TargetLanguage),
        };
        if (context.Length > 0) payloadValues["context"] = context;
        var payload = JsonSerializer.Serialize(payloadValues);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(TimeoutMs));

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", _settings.DeepLApiKey);

            using var response = await _http.SendAsync(httpRequest, timeoutCts.Token).ConfigureAwait(false);
            var rawResponse = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "DeepL request failed - HTTP {Status}: {Body}",
                    (int)response.StatusCode, Truncate(rawResponse, 300));
                return Failure(request, stopwatch.ElapsedMilliseconds,
                    $"DeepL returned HTTP {(int)response.StatusCode}: {ExtractErrorMessage(rawResponse)}", rawResponse);
            }

            var parsedTranslation = ExtractTranslation(rawResponse);
            if (string.IsNullOrWhiteSpace(parsedTranslation))
                return Failure(request, stopwatch.ElapsedMilliseconds,
                    "DeepL returned an empty translation.", rawResponse);

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
            return Failure(request, stopwatch.ElapsedMilliseconds, $"DeepL timed out ({TimeoutMs} ms).");
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            return Failure(request, stopwatch.ElapsedMilliseconds,
                $"DeepL is not reachable. Check your internet connection. Detail: {exception.Message}");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _logger.LogError(exception, "DeepL request failed unexpectedly");
            return Failure(request, stopwatch.ElapsedMilliseconds, $"DeepL error: {exception.Message}");
        }
    }

    public async Task<TranslationProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!HasApiKey)
            return Health(false, TranslationProviderStatus.MissingApiKey,
                "DeepL is not configured. Paste an API key from deepl.com/pro-api.", 0);

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
                    $"DeepL reachable ({result.DurationMs} ms).", stopwatch.ElapsedMilliseconds)
                : Health(false, TranslationProviderStatus.Unreachable,
                    result.ErrorMessage ?? "DeepL health check failed.", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return Health(false, TranslationProviderStatus.Failed, exception.Message, stopwatch.ElapsedMilliseconds);
        }
    }

    // DeepL uses "TR" for Turkish but requires the "EN-US"/"EN-GB" region suffix
    // for English as a *target* (not needed here, source-only in this app), and
    // plain "TR" as target is already valid.
    private static string NormalizeTargetLang(string targetLanguage) =>
        targetLanguage.ToUpperInvariant();

    // Response shape: {"translations":[{"detected_source_language":"EN","text":"..."}]}
    private static string ExtractTranslation(string rawResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            if (doc.RootElement.TryGetProperty("translations", out var translations) &&
                translations.ValueKind == JsonValueKind.Array &&
                translations.GetArrayLength() > 0 &&
                translations[0].TryGetProperty("text", out var text))
                return (text.GetString() ?? string.Empty).Trim();
        }
        catch (JsonException)
        {
            // Fall through to empty.
        }
        return string.Empty;
    }

    // Error shape: {"message":"..."}
    private static string ExtractErrorMessage(string rawResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            if (doc.RootElement.TryGetProperty("message", out var message))
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
