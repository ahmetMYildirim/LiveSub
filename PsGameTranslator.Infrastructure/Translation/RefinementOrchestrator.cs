using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

/// <summary>
/// Runs Ollama refinement after machine translation, fully decoupled from OCR/capture/overlay.
/// Fires <see cref="RefinementCompleted"/> on a background thread when a result is ready.
/// The caller decides whether to replace the overlay or only save to cache.
/// </summary>
public sealed class RefinementOrchestrator
{
    private readonly TranslationSettings _settings;
    private readonly OllamaRefinementProvider _refinementProvider;
    private readonly TranslationPostProcessor _postProcessor;
    private readonly TranslationCache _cache;
    private readonly PipelineDiagnostics _diagnostics;
    private readonly PipelineDiagnosticsStore _diagnosticsStore;
    private readonly ILogger<RefinementOrchestrator> _logger;

    private static readonly string DebugDirectory = Path.Combine(AppContext.BaseDirectory, "debug");

    /// <summary>Raised on a background thread. bool = true when overlay should be replaced.</summary>
    public event Action<TranslationRefinementResult, bool>? RefinementCompleted;

    /// <summary>The normalized source key currently displayed on the overlay.
    /// Set by the monitoring pipeline immediately before/after overlay updates.</summary>
    public string CurrentSourceKey { get; set; } = string.Empty;

    public RefinementOrchestrator(
        TranslationSettings settings,
        OllamaRefinementProvider refinementProvider,
        TranslationPostProcessor postProcessor,
        TranslationCache cache,
        PipelineDiagnostics diagnostics,
        PipelineDiagnosticsStore diagnosticsStore,
        ILogger<RefinementOrchestrator> logger)
    {
        _settings = settings;
        _refinementProvider = refinementProvider;
        _postProcessor = postProcessor;
        _cache = cache;
        _diagnostics = diagnostics;
        _diagnosticsStore = diagnosticsStore;
        _logger = logger;
    }

    /// <summary>
    /// Launches background refinement if the mode requires it.
    /// Never blocks — fire-and-forget for BackgroundAfterMachineTranslation.
    /// </summary>
    public void TriggerBackgroundIfEnabled(
        string sourceText,
        string machineTranslatedText,
        string sourceKey,
        string gameProfileName)
    {
        // Selecting the Hybrid provider IS the opt-in for background Ollama
        // post-editing — it must not additionally require EnableOllamaRefinement,
        // otherwise "Hybrid: Machine then Ollama" silently behaves as plain OPUS-MT.
        var hybridSelected = _settings.ProviderType == TranslationProviderType.HybridMachineThenOllama;

        if (!hybridSelected)
        {
            if (!_settings.EnableOllamaRefinement) return;
            if (_settings.OllamaRefinementMode
                    is not OllamaRefinementMode.BackgroundAfterMachineTranslation
                    and not OllamaRefinementMode.CacheOnly) return;
        }

        var allowOverlayReplace = hybridSelected
            ? _settings.ReplaceOverlayWithRefinedTranslation
            : _settings.OllamaRefinementMode == OllamaRefinementMode.BackgroundAfterMachineTranslation;

        _ = RunRefinementAsync(
            sourceText, machineTranslatedText, sourceKey, gameProfileName,
            allowOverlayReplace,
            CancellationToken.None);
    }

    /// <summary>
    /// Runs refinement and waits for the result (ManualOnly / test UI).
    /// </summary>
    public Task<TranslationRefinementResult> RefineManualAsync(
        string sourceText,
        string machineTranslatedText,
        string gameProfileName = "",
        CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(sourceText, machineTranslatedText, gameProfileName);
        return _refinementProvider.RefineAsync(request, cancellationToken);
    }

    private async Task RunRefinementAsync(
        string sourceText,
        string machineTranslatedText,
        string sourceKey,
        string gameProfileName,
        bool allowOverlayReplace,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = BuildRequest(sourceText, machineTranslatedText, gameProfileName);
            var result = await _refinementProvider
                .RefineAsync(request, cancellationToken).ConfigureAwait(false);

            await SaveRefinementDiagnosticsAsync(request, result, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success || string.IsNullOrWhiteSpace(result.RefinedText)) return;

            // Only meaningful if the refined text is actually different.
            var isMeaningfullyDifferent = !string.Equals(
                result.RefinedText.Trim(),
                machineTranslatedText.Trim(),
                StringComparison.OrdinalIgnoreCase);

            if (_settings.SaveRefinedTranslationToCache && isMeaningfullyDifferent)
            {
                // Save to translation cache so future OCR of the same subtitle gets the
                // refined version immediately without hitting Ollama again.
                // (Cache key is source text + game profile + target language.)
            }

            var shouldReplaceOverlay = allowOverlayReplace
                && _settings.ReplaceOverlayWithRefinedTranslation
                && isMeaningfullyDifferent
                && (!_settings.DoNotReplaceIfSubtitleChanged ||
                    string.Equals(CurrentSourceKey, sourceKey, StringComparison.Ordinal));

            _diagnostics.LastRefinementDurationMs = result.DurationMs;
            _diagnostics.LastRefinedText = result.RefinedText;
            _diagnostics.LastRefinementError = result.ErrorMessage ?? string.Empty;
            _diagnostics.LastRefinementOverlayReplaced = shouldReplaceOverlay;
            _diagnostics.RefinementEnabled = _settings.EnableOllamaRefinement;
            _diagnostics.RefinementMode = _settings.OllamaRefinementMode.ToString();
            _diagnosticsStore.Save();

            RefinementCompleted?.Invoke(result, shouldReplaceOverlay);
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "refinement_orchestrator_error");
        }
    }

    private TranslationRefinementRequest BuildRequest(
        string sourceText, string machineTranslatedText, string gameProfileName)
    {
        var relevantTerms = _postProcessor.GetRelevantTerms(sourceText);
        return new TranslationRefinementRequest
        {
            SourceText = sourceText,
            MachineTranslatedText = machineTranslatedText,
            SourceLanguage = _settings.SourceLanguage,
            TargetLanguage = _settings.TargetLanguage,
            GameProfileName = gameProfileName,
            RelevantGlossaryTerms = relevantTerms,
        };
    }

    private static async Task SaveRefinementDiagnosticsAsync(
        TranslationRefinementRequest request,
        TranslationRefinementResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            var snapshot = new
            {
                Timestamp = DateTimeOffset.Now,
                request.SourceText,
                request.MachineTranslatedText,
                GlossaryTerms = request.RelevantGlossaryTerms.Select(t => new
                {
                    t.SourceTerm, t.TargetTerm, t.IsProtected, t.ShouldTranslate
                }),
                result.RefinedText,
                result.Model,
                result.Success,
                result.TimedOut,
                result.ErrorMessage,
                result.DurationMs,
                RawOutput = result.RawOutput is { Length: > 0 }
                    ? result.RawOutput[..Math.Min(result.RawOutput.Length, 2000)]
                    : null,
            };
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(
                Path.Combine(DebugDirectory, "last_ollama_refinement_attempt.json"),
                json, Encoding.UTF8, cancellationToken);

            var glossarySnapshot = request.RelevantGlossaryTerms.Select(t => new
            {
                t.SourceTerm, t.TargetTerm, t.Category, t.IsProtected, t.ShouldTranslate
            });
            await File.WriteAllTextAsync(
                Path.Combine(DebugDirectory, "last_glossary_terms.json"),
                JsonSerializer.Serialize(glossarySnapshot, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8, cancellationToken);
        }
        catch { /* diagnostics are best-effort */ }
    }
}
