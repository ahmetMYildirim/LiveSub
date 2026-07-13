using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

/// <summary>
/// Calls the local Python OPUS-MT translation server
/// (tools/translation/translation_server.py) over HTTP.
/// Never returns the source text as a translation and never adds labels.
/// </summary>
public sealed class MachineTranslationProvider : ITranslationProvider, ITranslationProviderDeepHealth
{
    public string ProviderName => "MachineTranslationProvider";
    public TranslationProviderType ProviderType => TranslationProviderType.MachineTranslation;

    private readonly TranslationSettings _settings;
    private readonly ILogger<MachineTranslationProvider> _logger;
    private readonly HttpClient _http;

    public MachineTranslationProvider(
        TranslationSettings settings,
        ILogger<MachineTranslationProvider> logger)
    {
        _settings = settings;
        _logger = logger;
        // Timeout is controlled per request via MachineTranslationTimeoutMs.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var url = _settings.MachineTranslationBaseUrl.TrimEnd('/') + "/translate";
        var stopwatch = Stopwatch.StartNew();

        var normalizedText = NormalizeOverCapitalization(request.SourceText);
        var payload = JsonSerializer.Serialize(new
        {
            text = normalizedText,
            sourceLanguage = request.SourceLanguage,
            targetLanguage = request.TargetLanguage,
            mode = ResolveServerMode(),
        });

        _logger.LogInformation(
            "machine_translation_request_started - url={Url}, timeoutMs={TimeoutMs}, text={Text}",
            url, _settings.MachineTranslationTimeoutMs, Truncate(request.SourceText, 80));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(100, _settings.MachineTranslationTimeoutMs)));

        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, timeoutCts.Token).ConfigureAwait(false);
            var responseBytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
            var rawResponse = Encoding.UTF8.GetString(responseBytes);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "machine_translation_http_error - status={Status}, body={Body}",
                    (int)response.StatusCode, Truncate(rawResponse, 300));
                return Failure(request, stopwatch.ElapsedMilliseconds,
                    $"Translation server returned HTTP {(int)response.StatusCode}.", rawResponse);
            }

            using var document = JsonDocument.Parse(rawResponse);
            var root = document.RootElement;

            var serverSuccess = root.TryGetProperty("success", out var successElement) &&
                successElement.ValueKind == JsonValueKind.True;
            var serverError = root.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString()
                    : null;
            var translation = root.TryGetProperty("translation", out var translationElement) &&
                translationElement.ValueKind == JsonValueKind.String
                    ? translationElement.GetString() ?? string.Empty
                    : string.Empty;
            var fromCache = root.TryGetProperty("fromCache", out var fromCacheElement) &&
                fromCacheElement.ValueKind == JsonValueKind.True;

            if (!serverSuccess)
            {
                _logger.LogWarning(
                    "machine_translation_server_error - error={Error}", serverError ?? "unknown");
                return Failure(request, stopwatch.ElapsedMilliseconds,
                    $"Translation server reported failure: {serverError ?? "unknown error"}", rawResponse);
            }

            if (string.IsNullOrWhiteSpace(translation))
            {
                _logger.LogWarning("machine_translation_empty_translation");
                return Failure(request, stopwatch.ElapsedMilliseconds,
                    "Translation server returned an empty translation.", rawResponse);
            }

            _logger.LogInformation(
                "machine_translation_succeeded - durationMs={DurationMs}, translation={Translation}",
                stopwatch.ElapsedMilliseconds, Truncate(translation, 120));

            return new TranslationResult
            {
                SourceText = request.SourceText,
                TranslatedText = translation,
                SourceLanguage = request.SourceLanguage,
                TargetLanguage = request.TargetLanguage,
                ProviderName = ProviderName,
                DurationMs = stopwatch.ElapsedMilliseconds,
                FromCache = fromCache,
                Success = true,
                RawResponse = rawResponse,
                ParsedTranslation = translation,
                PostProcessedTranslation = translation,
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
            _logger.LogWarning(
                "machine_translation_timeout - timeoutMs={TimeoutMs}", _settings.MachineTranslationTimeoutMs);
            return Failure(request, stopwatch.ElapsedMilliseconds,
                $"Translation server timed out ({_settings.MachineTranslationTimeoutMs} ms).");
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                "machine_translation_unreachable - {Message}", exception.Message);
            return Failure(request, stopwatch.ElapsedMilliseconds,
                "Translation server is not reachable. Start it with tools/translation/start_translation_server.ps1.");
        }
        catch (JsonException exception)
        {
            stopwatch.Stop();
            _logger.LogWarning(exception, "machine_translation_parse_error");
            return Failure(request, stopwatch.ElapsedMilliseconds,
                $"Could not parse translation server response: {exception.Message}");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _logger.LogError(exception, "machine_translation_unexpected_error");
            return Failure(request, stopwatch.ElapsedMilliseconds,
                $"Translation error: {exception.Message}");
        }
    }

    /// <summary>
    /// Checks GET /health first; if that fails, tries POST /translate with "Hello.".
    /// </summary>
    public async Task<(bool Success, string Message)> TestServerAsync(
        CancellationToken cancellationToken = default)
    {
        var healthUrl = _settings.MachineTranslationBaseUrl.TrimEnd('/') + "/health";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            using var response = await _http.GetAsync(healthUrl, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var modelLoaded =
                    (root.TryGetProperty("modelLoaded", out var loadedElement) &&
                     loadedElement.ValueKind == JsonValueKind.True) ||
                    (root.TryGetProperty("model_loaded", out var loadedSnake) &&
                     loadedSnake.ValueKind == JsonValueKind.True);
                var provider = root.TryGetProperty("provider", out var providerElement)
                    ? providerElement.GetString()
                    : root.TryGetProperty("model", out var modelElement)
                        ? modelElement.GetString()
                        : "unknown";

                return modelLoaded
                    ? (true, $"Server reachable - model loaded ({provider}).")
                    : (false, "Server reachable but model is NOT loaded yet. Wait for startup to finish.");
            }

            return (false, $"Server health check returned HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception healthException)
        {
            _logger.LogInformation(
                "machine_translation_health_failed_trying_translate - {Message}", healthException.Message);
        }

        // Health failed — fall back to a real translate call.
        var result = await TranslateAsync(new TranslationRequest
        {
            SourceText = "Hello.",
            SourceLanguage = _settings.SourceLanguage,
            TargetLanguage = _settings.TargetLanguage,
        }, cancellationToken).ConfigureAwait(false);

        if (result.Success)
            return (true, $"Server reachable via /translate (health endpoint failed). Sample: {result.TranslatedText}");
        if (result.ErrorMessage?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true)
            return (false, "Timeout: server did not answer in time. It may still be loading the model.");
        if (result.ErrorMessage?.Contains("not reachable", StringComparison.OrdinalIgnoreCase) == true)
            return (false, "Server unreachable. Start it with tools/translation/start_translation_server.ps1.");
        if (result.ErrorMessage?.Contains("parse", StringComparison.OrdinalIgnoreCase) == true)
            return (false, $"Parse error: {result.ErrorMessage}");
        return (false, result.ErrorMessage ?? "Server test failed.");
    }

    public async Task<TranslationProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var (success, message) = await TestServerAsync(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        return new TranslationProviderHealth
        {
            ProviderName = ProviderName,
            ProviderType = ProviderType,
            IsAvailable = success,
            Status = success ? TranslationProviderStatus.Running : TranslationProviderStatus.ServerNotRunning,
            Message = message,
            DurationMs = stopwatch.ElapsedMilliseconds,
            ConfigurationStatus = success ? "Configured" : "Server not running",
        };
    }

    /// <summary>Deep check: server reachable + model/tokenizer loaded + a real inference works.</summary>
    public async Task<TranslationProviderHealth> CheckDeepHealthAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var (serverOk, serverMessage) = await TestServerAsync(cancellationToken).ConfigureAwait(false);
        if (!serverOk)
        {
            stopwatch.Stop();
            return new TranslationProviderHealth
            {
                ProviderName = ProviderName,
                ProviderType = ProviderType,
                IsAvailable = false,
                Status = TranslationProviderStatus.ServerNotRunning,
                Message = $"✗ {serverMessage} Fix: start the translation server (Translation tab) or enable auto-start.",
                DurationMs = stopwatch.ElapsedMilliseconds,
                ConfigurationStatus = "Server not running or model not loaded",
            };
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        TranslationResult inference;
        try
        {
            inference = await TranslateAsync(new TranslationRequest
            {
                SourceText = "Hello, friend.",
                SourceLanguage = _settings.SourceLanguage,
                TargetLanguage = _settings.TargetLanguage,
            }, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            inference = new TranslationResult { Success = false, ErrorMessage = "Inference test timed out (15 s)." };
        }
        stopwatch.Stop();

        return new TranslationProviderHealth
        {
            ProviderName = ProviderName,
            ProviderType = ProviderType,
            IsAvailable = inference.Success,
            Status = inference.Success ? TranslationProviderStatus.Running : TranslationProviderStatus.Failed,
            Message = inference.Success
                ? $"✓ model loaded ✓ tokenizer loaded ✓ inference works ({inference.DurationMs} ms) — {_settings.MachineTranslationModel}"
                : $"✗ inference failed: {inference.ErrorMessage}",
            DurationMs = stopwatch.ElapsedMilliseconds,
            ConfigurationStatus = inference.Success ? "Fully operational" : "Inference failing",
        };
    }

    private TranslationResult Failure(
        TranslationRequest request, long durationMs, string error, string? rawResponse = null) => new()
    {
        SourceText = request.SourceText,
        TranslatedText = string.Empty,
        SourceLanguage = request.SourceLanguage,
        TargetLanguage = request.TargetLanguage,
        ProviderName = ProviderName,
        DurationMs = durationMs,
        Success = false,
        ErrorMessage = error,
        RawResponse = rawResponse ?? string.Empty,
    };

    private string ResolveServerMode() =>
        _settings.Profile == TranslationProfile.Accurate ? "quality" : "fast";

    /// <summary>
    /// Many games render their subtitles in Title Case ("That Filch was no Fuss",
    /// "The Quartermaster stashed his Loot"). OPUS-MT reads mid-sentence capitals
    /// as proper nouns and copies those words into the Turkish untranslated
    /// ("Filch'in Fuss olmadığını...", "Loot'unu Tavern..."). When a line is
    /// clearly over-capitalised like that, lower-case the mid-sentence words so
    /// the model actually translates them. Normal sentence-case text (where a
    /// capital really does mark a proper noun) is left untouched. Cloud engines
    /// (DeepL/Google) are robust to this, so it is only applied for OPUS-MT here.
    /// </summary>
    internal static string NormalizeOverCapitalization(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var tokens = text.Split(' ');
        int midTitleCased = 0, midWords = 0;
        var atSentenceStart = true;
        foreach (var token in tokens)
        {
            if (token.Length == 0) continue;
            if (!atSentenceStart)
            {
                midWords++;
                if (IsTitleCased(token)) midTitleCased++;
            }
            atSentenceStart = EndsSentence(token);
        }

        // Trigger when a meaningful share of mid-sentence words are Title-Cased —
        // the signature of game "Title Case" styling. Normal prose with the odd
        // proper noun stays well under this (e.g. one name in 5 words = 0.2), so
        // it is left untouched; game lines that capitalise most nouns clear it.
        if (midWords < 2 || (double)midTitleCased / midWords < 0.30)
            return text;

        var builder = new StringBuilder(text.Length);
        atSentenceStart = true;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (i > 0) builder.Append(' ');
            var token = tokens[i];
            if (token.Length == 0) { atSentenceStart = false; continue; }

            if (!atSentenceStart && IsTitleCased(token) && !IsAlwaysCapitalized(token))
                builder.Append(LowercaseFirstLetter(token));
            else
                builder.Append(token);

            atSentenceStart = EndsSentence(token);
        }
        return builder.ToString();
    }

    // "Xxxx" (leading letter upper, at least one following lower letter). Rejects
    // ALL-CAPS acronyms ("RAW", "HUD") so those are left as-is.
    private static bool IsTitleCased(string token)
    {
        var i = 0;
        while (i < token.Length && !char.IsLetter(token[i])) i++;
        if (i >= token.Length || !char.IsUpper(token[i])) return false;

        var sawLower = false;
        for (var j = i + 1; j < token.Length; j++)
        {
            if (!char.IsLetter(token[j])) continue;
            if (char.IsUpper(token[j])) return false;
            sawLower = true;
        }
        return sawLower;
    }

    private static bool IsAlwaysCapitalized(string token) =>
        token is "I" || token.StartsWith("I'", StringComparison.Ordinal);

    private static string LowercaseFirstLetter(string token)
    {
        var i = 0;
        while (i < token.Length && !char.IsLetter(token[i])) i++;
        if (i >= token.Length) return token;
        return string.Concat(token.AsSpan(0, i), char.ToLowerInvariant(token[i]).ToString(), token.AsSpan(i + 1));
    }

    private static bool EndsSentence(string token) =>
        token.EndsWith('.') || token.EndsWith('!') || token.EndsWith('?') ||
        token.EndsWith(":") || token.EndsWith("…"); // colon / ellipsis also start a fresh clause

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
