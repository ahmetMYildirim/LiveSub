using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

public sealed record VisionGameIdentificationResult(
    bool Success,
    string? RecognizedGameName,
    GameGlossaryInfo? CatalogMatch,
    string RawModelAnswer,
    string? ErrorMessage,
    long DurationMs);

/// <summary>
/// Identifies which game a screenshot is from using a local vision-capable
/// Ollama model, for capture sources where the window title is useless for
/// game detection (PS5 Remote Play shows a generic client title, a YouTube
/// video's title is not the game's real name). Only ever called on-demand —
/// never from the hot OCR loop — since a vision model call takes seconds.
///
/// The prompt deliberately does NOT hand the model the list of known games
/// up front: earlier phrasing ("if it matches one of these titles...") made
/// the model force-fit visually-similar games onto the closest known title
/// (Dark Souls III -> Elden Ring, Oblivion Remastered -> Skyrim) instead of
/// admitting a different or unknown game.
///
/// Even with an open-ended prompt, a small (4B) vision model still confuses
/// games from the same studio/engine (Bloodborne -> Elden Ring, AC Syndicate
/// -> Watch Dogs Legion / Immortals Fenyx Rising) often enough that trusting
/// its guess outright would be actively harmful (wrong character names get
/// force-corrected into the subtitle). Having the same model re-check its
/// own answer with a second yes/no call did not help — it is not a prompting
/// problem, the model's actual recognition confidence is simply too low for
/// fine-grained game identification at this size. So this class never
/// auto-activates a glossary on its own: it only returns a best-guess
/// CatalogMatch, and PsGameTranslator.App.ViewModels.CaptureViewModel puts
/// that guess in front of the user for a real yes/no before
/// ActiveGameCoordinator.ConfirmPendingGameAsync loads anything.
/// </summary>
public sealed class OllamaVisionGameIdentifier
{
    private readonly TranslationSettings _settings;
    private readonly ILogger<OllamaVisionGameIdentifier> _logger;
    private readonly HttpClient _http;

    public OllamaVisionGameIdentifier(TranslationSettings settings, ILogger<OllamaVisionGameIdentifier> logger)
    {
        _settings = settings;
        _logger = logger;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<VisionGameIdentificationResult> IdentifyAsync(
        byte[] pngScreenshot, CancellationToken cancellationToken = default)
    {
        var identifyPrompt =
            "You are looking at a screenshot from a video game. " +
            "Look carefully at the HUD, UI style, art style, character models, and any visible text or logos.\n\n" +
            "What is the exact, real title of this specific game?\n\n" +
            "Rules:\n" +
            "- Answer with ONLY the game's title — no explanation, no punctuation, no extra words.\n" +
            "- Do NOT guess a similar-looking or same-genre game. Two games from the same studio or " +
            "engine (e.g. different Souls-like games, different Assassin's Creed games, or different " +
            "Bethesda RPGs) can look very alike — if you are not confident it is this exact title, " +
            "do not name a different specific game as if it were a guess.\n" +
            "- If you cannot identify the exact game with confidence, answer exactly \"Unknown\".";

        var timer = Stopwatch.StartNew();
        var identifyOutcome = await CallOllamaAsync(identifyPrompt, pngScreenshot, 32, cancellationToken);
        timer.Stop();
        if (!identifyOutcome.Success)
            return new VisionGameIdentificationResult(false, null, null, string.Empty,
                identifyOutcome.ErrorMessage, timer.ElapsedMilliseconds);

        var answer = identifyOutcome.Answer!.Trim().Trim('"', '.', ' ');
        _logger.LogInformation("vision_game_id_ok - answer={Answer}, durationMs={Ms}", answer, timer.ElapsedMilliseconds);

        if (answer.Length == 0 || answer.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return new VisionGameIdentificationResult(true, null, null, answer, null, timer.ElapsedMilliseconds);

        // A small vision model's free-text guess is not reliable enough to
        // trust blindly — even when it happens to match a catalog title, the
        // glossary is only ever activated after the human confirms it in the
        // UI (see ActiveGameCoordinator.ConfirmPendingGameAsync). An earlier
        // attempt at having the model re-check its own answer with a second
        // yes/no call did not meaningfully improve accuracy, since the same
        // weak model is doing the judging both times.
        var catalogMatch = MatchKnownGame(answer);
        return new VisionGameIdentificationResult(true, answer, catalogMatch, answer, null, timer.ElapsedMilliseconds);
    }

    private async Task<(bool Success, string? Answer, string? ErrorMessage)> CallOllamaAsync(
        string prompt, byte[] pngScreenshot, int numPredict, CancellationToken cancellationToken)
    {
        var model = _settings.OllamaVisionModel;
        var url = _settings.OllamaBaseUrl.TrimEnd('/') + "/api/generate";
        var payload = JsonSerializer.Serialize(new
        {
            model,
            stream = false,
            prompt,
            images = new[] { Convert.ToBase64String(pngScreenshot) },
            options = new { temperature = 0.0, num_predict = numPredict },
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Math.Max(1000, _settings.OllamaVisionTimeoutMs));

        _logger.LogInformation("vision_game_id_start - model={Model}", model);
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, timeoutCts.Token).ConfigureAwait(false);
            var rawBytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
            var rawResponse = Encoding.UTF8.GetString(rawBytes);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("vision_game_id_http_error - HTTP {Status}: {Body}",
                    (int)response.StatusCode, Truncate(rawResponse, 200));
                return (false, null, $"HTTP {(int)response.StatusCode}");
            }

            return (true, ExtractResponseProperty(rawResponse), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("vision_game_id_timeout - model={Model}", model);
            return (false, null, $"Timed out after {_settings.OllamaVisionTimeoutMs} ms.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "vision_game_id_exception - model={Model}", model);
            return (false, null, exception.Message);
        }
    }

    /// <summary>
    /// Only matches when the model's free-text answer is a close, specific match
    /// to a catalog title — a bare substring/keyword check is deliberately NOT
    /// used here (that was the earlier bug: a looser check let unrelated answers
    /// slip through as matches).
    /// </summary>
    private static GameGlossaryInfo? MatchKnownGame(string answer)
    {
        var exact = GameGlossaryCatalog.Games.FirstOrDefault(g =>
            g.DisplayName.Equals(answer, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        return GameGlossaryCatalog.Games.FirstOrDefault(g =>
            g.MatchKeywords.Any(k => answer.Equals(k, StringComparison.OrdinalIgnoreCase)) ||
            answer.Contains(g.DisplayName, StringComparison.OrdinalIgnoreCase));
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

    private static string Truncate(string? text, int maxLength) =>
        text is null ? string.Empty :
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}
