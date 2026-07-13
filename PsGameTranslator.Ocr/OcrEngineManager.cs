using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Ocr;

namespace PsGameTranslator.Ocr;

public sealed class OcrEngineManager
{
    private static readonly string ProviderResultPath = Path.Combine(
        AppContext.BaseDirectory, "debug", "last_ocr_provider_result.json");

    private static readonly string BestSelectionPath = Path.Combine(
        AppContext.BaseDirectory, "debug", "last_ocr_best_selection.json");

    private static readonly string ProviderSelectionPath = Path.Combine(
        AppContext.BaseDirectory, "debug", "last_ocr_provider_selection.json");

    private static readonly string ProviderHealthPath = Path.Combine(
        AppContext.BaseDirectory, "debug", "last_ocr_provider_health.json");

    private readonly IReadOnlyList<IOcrProvider> _providers;
    private readonly OcrEngineSettings _settings;
    private readonly OcrResultScorer _scorer;
    private readonly ILogger<OcrEngineManager> _logger;

    private string _previousSubtitleKey = string.Empty;

    public OcrEngineManager(
        IEnumerable<IOcrProvider> providers,
        OcrEngineSettings settings,
        OcrResultScorer scorer,
        ILogger<OcrEngineManager> logger)
    {
        _providers = providers.ToArray();
        _settings = settings;
        _scorer = scorer;
        _logger = logger;
    }

    public string LastProviderUsed { get; private set; } = string.Empty;
    public string LastSelectedProvider { get; private set; } = string.Empty;
    public string LastFallbackReason { get; private set; } = string.Empty;
    public bool LastFallbackUsed { get; private set; }
    /// <summary>Human-readable note about the last provider selection (e.g. server bypassed for subprocess).</summary>
    public string LastSelectionNote { get; private set; } = string.Empty;
    public double LastBestScore { get; private set; }
    public BestOcrSelection LastSelection { get; private set; } = new();

    public IReadOnlyList<IOcrProvider> Providers => _providers;

    public async Task<OcrResult> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
    {
        _settings.ApplyProfileDefaults(preserveSelectedProvider: true);
        LastSelectedProvider = _settings.PreferredProvider.ToString();
        LastProviderUsed = string.Empty;
        LastFallbackUsed = false;
        LastFallbackReason = string.Empty;
        LastSelectionNote = string.Empty;

        // MultiOCR is not a concrete engine: selecting it means "run all available engines in parallel".
        var mode = _settings.EnableParallelOcr || _settings.PreferredProvider == OcrProviderType.MultiOCR
            ? OcrExecutionMode.ParallelBestResult
            : _settings.ExecutionMode;

        var results = mode switch
        {
            OcrExecutionMode.ParallelBestResult => await RunParallelAsync(request, cancellationToken).ConfigureAwait(false),
            OcrExecutionMode.FastFirstThenVerify => await RunFastFirstThenVerifyAsync(request, cancellationToken).ConfigureAwait(false),
            _ => [await RunSingleAsync(request, cancellationToken).ConfigureAwait(false)],
        };

        var selection = _scorer.SelectBest(results, _previousSubtitleKey);
        LastSelection = selection;
        LastBestScore = selection.Score;
        LastProviderUsed = selection.BestResult.ProviderName;

        if (!string.IsNullOrWhiteSpace(selection.DialogueText))
            _previousSubtitleKey = NormalizeKey(selection.DialogueText);

        SaveDiagnostics(results, selection);

        return selection.BestResult;
    }

    public async Task<IReadOnlyList<OcrProviderHealth>> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        var tasks = _providers.Select(provider => provider.CheckHealthAsync(cancellationToken));
        var health = await Task.WhenAll(tasks).ConfigureAwait(false);
        SaveHealthDiagnostics(health);
        return health;
    }

    private async Task<OcrResult> RunSingleAsync(
        OcrRequest request,
        CancellationToken cancellationToken)
    {
        var selection = SelectProviderForSingleRun(_settings.PreferredProvider);
        var provider = selection.Provider;
        SaveProviderSelectionDiagnostics(selection, null);

        if (provider is null)
        {
            return new OcrResult
            {
                Success = false,
                ProviderName = "none",
                ErrorMessage = selection.FallbackReason.Length > 0
                    ? selection.FallbackReason
                    : "No OCR providers are registered.",
            };
        }

        var result = await RunProviderWithTimeoutAsync(provider, request, cancellationToken).ConfigureAwait(false);
        SaveProviderSelectionDiagnostics(selection, result);
        return result;
    }

    private async Task<IReadOnlyList<OcrResult>> RunParallelAsync(
        OcrRequest request,
        CancellationToken cancellationToken)
    {
        var providers = PriorityForCurrentProfile()
            .Select(SelectProvider)
            .Where(provider => provider is not null)
            .Cast<IOcrProvider>()
            .Where(provider => provider.IsAvailable)
            .DistinctBy(provider => provider.Name)
            .ToArray();

        if (providers.Length == 0)
            return [await RunSingleAsync(request, cancellationToken).ConfigureAwait(false)];

        var tasks = providers.Select(provider =>
            RunProviderWithTimeoutAsync(provider, request, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<OcrResult>> RunFastFirstThenVerifyAsync(
        OcrRequest request,
        CancellationToken cancellationToken)
    {
        var first = await RunSingleAsync(request, cancellationToken).ConfigureAwait(false);
        if (first.Success && first.Confidence >= 0.75)
            return [first];

        var verifyProvider = _providers.FirstOrDefault(provider =>
            provider.ProviderType == OcrProviderType.PaddleOCR &&
            provider.IsAvailable &&
            !string.Equals(provider.Name, first.ProviderName, StringComparison.OrdinalIgnoreCase));
        if (verifyProvider is null)
            return [first];

        var second = await RunProviderWithTimeoutAsync(verifyProvider, request, cancellationToken).ConfigureAwait(false);
        return [first, second];
    }

    private async Task<OcrResult> RunProviderWithTimeoutAsync(
        IOcrProvider provider,
        OcrRequest request,
        CancellationToken cancellationToken)
    {
        // Subprocess PaddleOCR reloads the model on every call; the profile timeout
        // (700-2500 ms) is tuned for the persistent server and would fail every cold run.
        var timeoutMs = provider is PaddleOcrProvider
            ? Math.Max(_settings.OcrTimeoutMs, _settings.SubprocessOcrTimeoutMs)
            : _settings.OcrTimeoutMs;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Max(200, timeoutMs));

        try
        {
            var result = await provider.RecognizeAsync(request, timeout.Token).ConfigureAwait(false);
            return result.ProviderName.Length > 0
                ? result
                : new OcrResult
                {
                    ProviderName = provider.Name,
                    Text = result.Text,
                    Confidence = result.Confidence,
                    Region = result.Region,
                    Lines = result.Lines,
                    DurationMs = result.DurationMs,
                    Success = result.Success,
                    ErrorMessage = result.ErrorMessage,
                    RawOutput = result.RawOutput,
                };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new OcrResult
            {
                ProviderName = provider.Name,
                Success = false,
                ErrorMessage = $"OCR provider '{provider.Name}' timed out after {timeoutMs} ms.",
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "ocr_provider_failed - provider={Provider}", provider.Name);
            return new OcrResult
            {
                ProviderName = provider.Name,
                Success = false,
                ErrorMessage = exception.Message,
            };
        }
    }

    private IOcrProvider? SelectProvider(OcrProviderType providerType)
    {
        var providers = _providers
            .Where(provider => provider.ProviderType == providerType)
            .OrderByDescending(provider => provider.IsAvailable)
            .ThenByDescending(provider => provider.Name.Contains("Server", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return providers.FirstOrDefault(provider => provider.IsAvailable);
    }

    private IOcrProvider? SelectFirstAvailable(IEnumerable<OcrProviderType> priority) =>
        priority.Select(SelectProvider).FirstOrDefault(provider => provider is not null);

    private OcrProviderSelection SelectProviderForSingleRun(OcrProviderType selectedType)
    {
        var selectedCandidates = _providers
            .Where(provider => provider.ProviderType == selectedType)
            .OrderByDescending(provider => provider.IsAvailable)
            .ThenByDescending(provider => provider.Name.Contains("Server", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var selectedAvailable = selectedCandidates.FirstOrDefault(provider => provider.IsAvailable);
        if (selectedAvailable is not null)
        {
            // The engine matches the selection, but flag transport downgrades (server → subprocess)
            // so the UI can tell the user why OCR is suddenly slow.
            var serverCandidate = selectedCandidates.FirstOrDefault(provider =>
                provider.Name.Contains("Server", StringComparison.OrdinalIgnoreCase));
            if (serverCandidate is not null && !serverCandidate.IsAvailable &&
                !ReferenceEquals(serverCandidate, selectedAvailable))
            {
                LastSelectionNote =
                    $"{selectedType} server is not running — using the slower subprocess transport. " +
                    "Start the OCR server (or enable auto-start) for low-latency OCR.";
                _logger.LogWarning(
                    "ocr_transport_downgrade - selected={SelectedProvider}, actual={ActualProvider}",
                    selectedType, selectedAvailable.Name);
            }

            LastProviderUsed = selectedAvailable.Name;
            return new OcrProviderSelection(
                SelectedProvider: selectedType.ToString(),
                ActualProvider: selectedAvailable.Name,
                Provider: selectedAvailable,
                FallbackEnabled: _settings.EnableOcrProviderFallback,
                FallbackUsed: false,
                FallbackReason: string.Empty,
                ProviderAvailability: BuildAvailabilitySnapshot());
        }

        var selectedRegistered = selectedCandidates.FirstOrDefault();
        var reason = selectedRegistered switch
        {
            null => $"{selectedType} OCR provider is not registered.",
            UnavailableOcrProvider unavailable => $"{selectedType}: {unavailable.Message}",
            _ => $"{selectedType} OCR provider is unavailable/not configured.",
        };

        if (!_settings.EnableOcrProviderFallback)
        {
            LastProviderUsed = "none";
            LastFallbackReason = reason;
            return new OcrProviderSelection(
                SelectedProvider: selectedType.ToString(),
                ActualProvider: "none",
                Provider: null,
                FallbackEnabled: false,
                FallbackUsed: false,
                FallbackReason: reason,
                ProviderAvailability: BuildAvailabilitySnapshot());
        }

        var fallbackOrder = new[]
        {
            OcrProviderType.PaddleOCR,
            OcrProviderType.WindowsOCR,
            OcrProviderType.MockOCR,
        };

        var fallback = SelectFirstAvailable(fallbackOrder);
        if (fallback is null)
        {
            LastProviderUsed = "none";
            LastFallbackReason = reason + " No fallback OCR provider is available.";
            return new OcrProviderSelection(
                SelectedProvider: selectedType.ToString(),
                ActualProvider: "none",
                Provider: null,
                FallbackEnabled: true,
                FallbackUsed: false,
                FallbackReason: LastFallbackReason,
                ProviderAvailability: BuildAvailabilitySnapshot());
        }

        LastProviderUsed = fallback.Name;
        LastFallbackUsed = true;
        LastFallbackReason = reason;
        _logger.LogWarning(
            "ocr_provider_fallback - selected={SelectedProvider}, actual={ActualProvider}, reason={Reason}",
            selectedType, fallback.Name, reason);

        return new OcrProviderSelection(
            SelectedProvider: selectedType.ToString(),
            ActualProvider: fallback.Name,
            Provider: fallback,
            FallbackEnabled: true,
            FallbackUsed: true,
            FallbackReason: reason,
            ProviderAvailability: BuildAvailabilitySnapshot());
    }

    private object[] BuildAvailabilitySnapshot() =>
        _providers.Select(provider => new
        {
            provider.Name,
            provider.ProviderType,
            provider.IsAvailable,
        }).Cast<object>().ToArray();

    private IReadOnlyList<OcrProviderType> PriorityForCurrentProfile() =>
        _settings.Profile switch
        {
            OcrProfile.Fast => _settings.FastProviderPriority,
            OcrProfile.Accurate => _settings.AccurateProviderPriority,
            _ => _settings.BalancedProviderPriority,
        };

    private static string NormalizeKey(string text)
    {
        var normalized = text.ToLowerInvariant().Trim();
        return System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ");
    }

    private void SaveDiagnostics(IReadOnlyList<OcrResult> results, BestOcrSelection selection)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProviderResultPath)!);
            var providerJson = JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                _settings.Profile,
                _settings.ExecutionMode,
                _settings.PreferredProvider,
                Results = results.Select(result => new
                {
                    result.ProviderName,
                    result.Text,
                    result.Confidence,
                    result.DurationMs,
                    result.Success,
                    result.ErrorMessage,
                    LineCount = result.Lines.Count,
                }).ToArray(),
            }, new JsonSerializerOptions { WriteIndented = true });
            DebugFileWriter.QueueText(ProviderResultPath, providerJson, new UTF8Encoding(false));

            var selectionJson = JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                BestProvider = selection.BestResult.ProviderName,
                selection.CandidateText,
                selection.SpeakerName,
                selection.DialogueText,
                selection.Score,
                Rejected = selection.RejectedResults.Select(rejected => new
                {
                    rejected.Result.ProviderName,
                    rejected.Score,
                    rejected.Reason,
                    rejected.Result.Text,
                }).ToArray(),
                selection.Reasons,
            }, new JsonSerializerOptions { WriteIndented = true });
            DebugFileWriter.QueueText(BestSelectionPath, selectionJson, new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save OCR provider diagnostics");
        }
    }

    private void SaveProviderSelectionDiagnostics(OcrProviderSelection selection, OcrResult? result)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProviderSelectionPath)!);
            var json = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                selectedProvider = selection.SelectedProvider,
                actualProvider = selection.ActualProvider,
                fallbackEnabled = selection.FallbackEnabled,
                fallbackUsed = selection.FallbackUsed,
                fallbackReason = selection.FallbackReason,
                providerAvailability = selection.ProviderAvailability,
                providerHealthStatus = _providers.Select(p => p.IsAvailable ? "Available" : "Unavailable"),
                providerResult = result is null ? null : new
                {
                    result.ProviderName,
                    result.Success,
                    result.ErrorMessage,
                    result.Confidence,
                    LineCount = result.Lines.Count,
                    result.DurationMs,
                },
            }, new JsonSerializerOptions { WriteIndented = true });
            DebugFileWriter.QueueText(ProviderSelectionPath, json, new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save OCR provider selection diagnostics");
        }
    }

    private void SaveHealthDiagnostics(IReadOnlyList<OcrProviderHealth> health)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProviderHealthPath)!);
            var json = JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                SelectedProviderFromUi = _settings.PreferredProvider,
                ActualProviderUsed = string.IsNullOrWhiteSpace(LastProviderUsed) ? "none" : LastProviderUsed,
                FallbackEnabled = _settings.EnableOcrProviderFallback,
                LastFallbackReason,
                Providers = health,
            }, new JsonSerializerOptions { WriteIndented = true });
            DebugFileWriter.QueueText(ProviderHealthPath, json, new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save OCR provider health diagnostics");
        }
    }

    private sealed record OcrProviderSelection(
        string SelectedProvider,
        string ActualProvider,
        IOcrProvider? Provider,
        bool FallbackEnabled,
        bool FallbackUsed,
        string FallbackReason,
        object[] ProviderAvailability);
}
