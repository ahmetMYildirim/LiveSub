using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

/// <summary>
/// Lightweight Ollama-based post-refinement step. Receives English source +
/// machine-translated Turkish and returns a minimally corrected Turkish subtitle.
/// Never called from the hot capture loop — always background or manual.
/// </summary>
public sealed partial class OllamaRefinementProvider
{
    private readonly TranslationSettings _settings;
    private readonly ILogger<OllamaRefinementProvider> _logger;
    private readonly HttpClient _http;

    public OllamaRefinementProvider(
        TranslationSettings settings,
        ILogger<OllamaRefinementProvider> logger)
    {
        _settings = settings;
        _logger = logger;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<TranslationRefinementResult> RefineAsync(
        TranslationRefinementRequest request,
        CancellationToken cancellationToken = default)
    {
        var model = _settings.OllamaRefinementModel;
        var url = _settings.OllamaBaseUrl.TrimEnd('/') + "/api/generate";
        var prompt = BuildPrompt(request);
        var payload = JsonSerializer.Serialize(new
        {
            model,
            stream = false,
            think = false,
            format = "json",
            prompt,
            options = new
            {
                temperature = 0.0,
                top_p = 0.5,
                num_predict = 80,
                repeat_penalty = 1.1,
            },
        });

        var timer = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Math.Max(500, _settings.OllamaRefinementTimeoutMs));

        _logger.LogInformation(
            "ollama_refinement_start - model={Model}, source={Source}, machine={Machine}",
            model,
            Truncate(request.SourceText, 60),
            Truncate(request.MachineTranslatedText, 60));
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, timeoutCts.Token)
                .ConfigureAwait(false);
            var rawBytes = await response.Content
                .ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
            var rawResponse = Encoding.UTF8.GetString(rawBytes);
            timer.Stop();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ollama_refinement_http_error - HTTP {Status}: {Body}",
                    (int)response.StatusCode, Truncate(rawResponse, 200));
                return Failure(request, model, timer.ElapsedMilliseconds, rawResponse,
                    $"HTTP {(int)response.StatusCode}");
            }

            var innerText = ExtractResponseProperty(rawResponse);
            if (!TryExtractRefinedText(innerText, out var refined) || string.IsNullOrWhiteSpace(refined))
            {
                _logger.LogWarning("ollama_refinement_parse_failed - raw={Raw}", Truncate(rawResponse, 200));
                return Failure(request, model, timer.ElapsedMilliseconds, rawResponse, "JSON parse failed");
            }

            _logger.LogInformation(
                "ollama_refinement_ok - model={Model}, refined={Refined}, durationMs={Ms}",
                model, Truncate(refined, 80), timer.ElapsedMilliseconds);

            return new TranslationRefinementResult
            {
                SourceText = request.SourceText,
                MachineTranslatedText = request.MachineTranslatedText,
                RefinedText = refined.Trim(),
                Model = model,
                Success = true,
                RawOutput = rawResponse,
                DurationMs = timer.ElapsedMilliseconds,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timer.Stop();
            _logger.LogWarning("ollama_refinement_timeout - model={Model}, durationMs={Ms}",
                model, timer.ElapsedMilliseconds);
            return new TranslationRefinementResult
            {
                SourceText = request.SourceText,
                MachineTranslatedText = request.MachineTranslatedText,
                RefinedText = request.MachineTranslatedText,
                Model = model,
                Success = false,
                TimedOut = true,
                ErrorMessage = $"Refinement timed out after {_settings.OllamaRefinementTimeoutMs} ms.",
                DurationMs = timer.ElapsedMilliseconds,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            timer.Stop();
            _logger.LogWarning(exception, "ollama_refinement_exception - model={Model}", model);
            return Failure(request, model, timer.ElapsedMilliseconds, null, exception.Message);
        }
    }

    private static string BuildPrompt(TranslationRefinementRequest request)
    {
        var glossarySection = request.RelevantGlossaryTerms.Count > 0
            ? string.Join("\n", request.RelevantGlossaryTerms.Select(t =>
                t.ShouldTranslate
                    ? $"{t.SourceTerm} = {t.TargetTerm}"
                    : $"{t.SourceTerm} = {t.TargetTerm} (do not translate, keep as-is)"))
            : "(no specific glossary terms)";

        return
            "You are a Turkish game subtitle editor.\n\n" +
            "Return only valid JSON:\n" +
            "{\"refined\":\"...\"}\n\n" +
            "Task:\n" +
            "Improve the Turkish subtitle using the English source only for meaning verification.\n" +
            "Do not retranslate from scratch unless the machine translation is clearly wrong.\n" +
            "Keep the subtitle short, natural, and fluent.\n\n" +
            "Rules:\n" +
            "- Correct Turkish spelling, characters, grammar, and punctuation.\n" +
            "- Preserve the full meaning of the English source.\n" +
            "- Do not add explanations.\n" +
            "- Do not add new information.\n" +
            "- Do not remove important words such as \"more\", \"never\", \"must\", \"not\".\n" +
            "- Preserve protected game terms and proper names.\n" +
            "- Keep the result suitable for a two-line subtitle.\n" +
            "- If the machine translation is already good, return it with only minimal corrections.\n\n" +
            "Glossary:\n" +
            glossarySection + "\n\n" +
            "Examples:\n" +
            "English source: More marks of the dragon's fury.\n" +
            "Machine Turkish: Ejderhanın öfkesinin daha fazla izi.\n" +
            "Refined Turkish: Ejderhanın öfkesinden kalan daha fazla iz.\n\n" +
            "English source: I have never seen such a beast before.\n" +
            "Machine Turkish: Daha önce böyle bir yaratık görmedim.\n" +
            "Refined Turkish: Böyle bir canavarı daha önce hiç görmemiştim.\n\n" +
            "English source: The Arisen must return to Vermund and speak with the Pawn.\n" +
            "Machine Turkish: Arisen, Vermund'a dönmeli ve piyonla konuşmalı.\n" +
            "Refined Turkish: Arisen, Vermund'a dönüp Pawn ile konuşmalı.\n\n" +
            "English source:\n" +
            request.SourceText + "\n\n" +
            "Machine Turkish:\n" +
            request.MachineTranslatedText;
    }

    private static string ExtractResponseProperty(string rawResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            return doc.RootElement.TryGetProperty("response", out var prop)
                ? prop.GetString() ?? rawResponse
                : rawResponse;
        }
        catch { return rawResponse; }
    }

    private static bool TryExtractRefinedText(string text, out string refined)
    {
        refined = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("refined", out var prop))
            {
                refined = prop.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(refined);
            }
        }
        catch { /* fall through to regex */ }

        var match = RefinedJsonRegex().Match(text);
        if (match.Success)
        {
            refined = match.Groups[1].Value;
            return !string.IsNullOrWhiteSpace(refined);
        }
        return false;
    }

    private static TranslationRefinementResult Failure(
        TranslationRefinementRequest request,
        string model,
        long durationMs,
        string? rawOutput,
        string errorMessage) =>
        new()
        {
            SourceText = request.SourceText,
            MachineTranslatedText = request.MachineTranslatedText,
            RefinedText = request.MachineTranslatedText, // fall back to machine translation
            Model = model,
            Success = false,
            ErrorMessage = errorMessage,
            RawOutput = rawOutput,
            DurationMs = durationMs,
        };

    private static string Truncate(string? text, int maxLength) =>
        text is null ? string.Empty :
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    [GeneratedRegex(@"""refined""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Singleline)]
    private static partial Regex RefinedJsonRegex();
}
