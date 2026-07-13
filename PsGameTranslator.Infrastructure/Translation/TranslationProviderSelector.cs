using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

/// <summary>
/// Single source of truth for which ITranslationProvider is used at runtime.
/// FakeTranslationProvider is only returned when the explicit debug flag is set;
/// it must never leak into normal MachineTranslation/Ollama operation.
/// </summary>
public sealed class TranslationProviderSelector
{
    private static readonly string DebugDirectory = Path.Combine(AppContext.BaseDirectory, "debug");

    private readonly TranslationSettings _settings;
    private readonly FakeTranslationProvider _fakeProvider;
    private readonly MachineTranslationProvider _machineProvider;
    private readonly MachineTranslationServerManager _machineServer;
    private readonly OllamaTranslationService _ollamaProvider;
    private readonly IReadOnlyDictionary<TranslationProviderType, ITranslationProvider> _optionalProviders;
    private readonly TranslationDatasetCollector _datasetCollector;
    private readonly ILogger<TranslationProviderSelector> _logger;

    public TranslationProviderSelector(
        TranslationSettings settings,
        FakeTranslationProvider fakeProvider,
        MachineTranslationProvider machineProvider,
        MachineTranslationServerManager machineServer,
        OllamaTranslationService ollamaProvider,
        IEnumerable<ITranslationProvider> optionalProviders,
        TranslationDatasetCollector datasetCollector,
        ILogger<TranslationProviderSelector> logger)
    {
        _settings = settings;
        _fakeProvider = fakeProvider;
        _machineProvider = machineProvider;
        _machineServer = machineServer;
        _ollamaProvider = ollamaProvider;
        _optionalProviders = optionalProviders
            .GroupBy(provider => provider.ProviderType)
            .ToDictionary(group => group.Key, group => group.First());
        _datasetCollector = datasetCollector;
        _logger = logger;
    }

    /// <summary>Returns null when ProviderType is None (no translation should run).</summary>
    public ITranslationProvider? SelectProvider()
    {
        var selection = SelectProviderForMode(allowFallback: _settings.EnableTranslationProviderFallback);
        SaveSelectionDiagnostics(selection, null, null, []);
        return selection.ActualProvider;
    }

    // Hot-path guard: SelectProviderAsync runs for every translated subtitle.
    // A fresh all-provider health sweep costs several network round-trips
    // (3 s timeout each when a provider is down), so reuse recent results.
    private static readonly TimeSpan HealthCacheTtl = TimeSpan.FromSeconds(5);
    private IReadOnlyList<TranslationProviderHealth> _cachedHealth = [];
    private DateTimeOffset _cachedHealthAt = DateTimeOffset.MinValue;

    private async Task<IReadOnlyList<TranslationProviderHealth>> GetHealthCachedAsync(
        CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _cachedHealthAt < HealthCacheTtl && _cachedHealth.Count > 0)
            return _cachedHealth;

        var health = await CheckAllProviderHealthAsync(cancellationToken).ConfigureAwait(false);
        _cachedHealth = health;
        _cachedHealthAt = DateTimeOffset.UtcNow;
        return health;
    }

    public async Task<TranslationProviderSelection> SelectProviderAsync(
        bool allowFallback,
        CancellationToken cancellationToken = default)
    {
        var selection = SelectProviderForMode(allowFallback);
        var health = await GetHealthCachedAsync(cancellationToken).ConfigureAwait(false);
        var selectedHealth = health.FirstOrDefault(h => h.ProviderType == selection.SelectedProviderType);

        if (selection.ActualProvider is not null &&
            RequiresOpusServer(selection.ActualProvider.ProviderType) &&
            _settings.AutoStartOpusServer)
        {
            var ready = await _machineServer.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                selection = selection with
                {
                    ActualProvider = null,
                    ActualProviderName = "none",
                    ProviderStatus = TranslationProviderStatus.ServerNotRunning,
                    FallbackReason = string.IsNullOrWhiteSpace(selection.FallbackReason)
                        ? _machineServer.LastStartError
                        : selection.FallbackReason + " " + _machineServer.LastStartError,
                };
            }
        }

        selection = selection with
        {
            ProviderStatus = selectedHealth?.Status ?? selection.ProviderStatus,
            ProviderHealthStates = health,
        };

        SaveSelectionDiagnostics(selection, null, null, health);
        return selection;
    }

    public async Task<TranslationProviderExecutionResult> TranslateAsync(
        TranslationRequest request,
        bool allowFallback,
        CancellationToken cancellationToken = default)
    {
        var selection = await SelectProviderAsync(allowFallback, cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();

        TranslationResult result;
        if (selection.ActualProvider is null)
        {
            result = new TranslationResult
            {
                SourceText = request.SourceText,
                SourceLanguage = request.SourceLanguage,
                TargetLanguage = request.TargetLanguage,
                ProviderName = "none",
                Success = false,
                ErrorMessage = selection.FallbackReason.Length > 0
                    ? selection.FallbackReason
                    : "No translation provider selected.",
            };
        }
        else
        {
            result = await selection.ActualProvider.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
            // Fall back on any provider failure — including OPUS-MT. Previously
            // an OPUS primary was excluded (!RequiresOpusServer), so when OPUS
            // timed out (common while the game contends for the same GPU) the
            // line was silently dropped, producing the "3-4 subtitles in a row
            // go missing" gap. Now an OPUS timeout falls through to the next
            // provider in FallbackProviderOrder (Google first) so the line still
            // gets translated instead of disappearing. Only triggers on actual
            // failure/empty output, never on a successful (if imperfect) OPUS result.
            if ((!result.Success || string.IsNullOrWhiteSpace(result.TranslatedText)) &&
                allowFallback &&
                _settings.EnableTranslationProviderFallback)
            {
                var reason = result.ErrorMessage ?? "Selected provider failed.";
                var failedType = selection.ActualProvider.ProviderType;
                var fallbackOrder = _settings.FallbackProviderOrder.Count > 0
                    ? (IEnumerable<TranslationProviderType>)_settings.FallbackProviderOrder
                    : [TranslationProviderType.MachineTranslation];

                foreach (var fallbackType in fallbackOrder)
                {
                    if (fallbackType == failedType) continue;
                    var fallbackProvider = ResolveProvider(fallbackType);
                    if (fallbackProvider is null || fallbackProvider.IsAvailable == false) continue;

                    if (RequiresOpusServer(fallbackType) && _settings.AutoStartOpusServer)
                    {
                        var ready = await _machineServer.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
                        if (!ready) continue;
                    }

                    _logger.LogWarning(
                        "translation_provider_fallback - selected={Selected}, fallback={Fallback}, reason={Reason}",
                        selection.SelectedProviderType, fallbackProvider.ProviderName, reason);

                    var fallbackResult = await fallbackProvider.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
                    selection = selection with
                    {
                        ActualProvider = fallbackProvider,
                        ActualProviderName = fallbackProvider.ProviderName,
                        FallbackProviderName = fallbackProvider.ProviderName,
                        FallbackUsed = true,
                        FallbackReason = $"Selected provider failed. Falling back to {fallbackProvider.ProviderName}. Reason: {reason}",
                    };
                    result = fallbackResult;
                    if (result.Success && !string.IsNullOrWhiteSpace(result.TranslatedText)) break;
                }
            }
        }

        stopwatch.Stop();
        selection = selection with
        {
            ActualProviderName = string.IsNullOrWhiteSpace(result.ProviderName)
                ? selection.ActualProviderName
                : result.ProviderName,
            ProviderLatencyMs = result.DurationMs > 0 ? result.DurationMs : stopwatch.ElapsedMilliseconds,
        };

        // DeepL/Google are used as "teacher" quality references for OPUS-MT
        // fine-tuning — every other provider (including OPUS-MT itself) is
        // ignored inside Record().
        if (result.Success && selection.ActualProvider is not null)
            _datasetCollector.Record(request.SourceText, result.TranslatedText, selection.ActualProvider.ProviderType);

        SaveSelectionDiagnostics(selection, request, result, selection.ProviderHealthStates);
        SaveFlowDiagnostics(request, result, selection);
        return new TranslationProviderExecutionResult(selection, result);
    }

    public Task<IReadOnlyList<TranslationProviderHealth>> CheckAllProviderHealthAsync(
        CancellationToken cancellationToken = default) =>
        CheckAllProviderHealthAsync(deep: false, cancellationToken);

    /// <summary>
    /// Health for every known provider. <paramref name="deep"/> runs real inference
    /// tests where supported — only use from explicit UI actions, never on the
    /// translation hot path.
    /// </summary>
    public async Task<IReadOnlyList<TranslationProviderHealth>> CheckAllProviderHealthAsync(
        bool deep,
        CancellationToken cancellationToken = default)
    {
        var providers = EnumerateKnownProviders().ToArray();
        var tasks = providers.Select(provider =>
            deep && provider is ITranslationProviderDeepHealth deepProvider
                ? deepProvider.CheckDeepHealthAsync(cancellationToken)
                : provider.CheckHealthAsync(cancellationToken));
        var health = await Task.WhenAll(tasks).ConfigureAwait(false);
        _cachedHealth = health;
        _cachedHealthAt = DateTimeOffset.UtcNow;
        SaveProviderHealthDiagnostics(health);
        return health;
    }

    private TranslationProviderSelection SelectProviderForMode(bool allowFallback)
    {
        if (_settings.UseFakeTranslationProviderForDebug)
        {
            _logger.LogWarning("translation_provider_selected - FakeTranslationProvider (debug flag is ON)");
            return BuildSelection(_settings.ProviderType, _fakeProvider, allowFallback, fallbackUsed: false, string.Empty);
        }

        var selectedType = ResolveSelectedProviderType();
        var selectedProvider = ResolveProvider(selectedType);
        var selectedUnavailable = selectedProvider is null || selectedProvider.IsAvailable == false;
        var modeAllowsFallback = allowFallback &&
            _settings.EnableTranslationProviderFallback &&
            _settings.ProviderChainMode is TranslationProviderChainMode.ProviderChain or TranslationProviderChainMode.HybridBalanced;

        if (!selectedUnavailable)
            return BuildSelection(selectedType, selectedProvider, allowFallback, fallbackUsed: false, string.Empty);

        var reason = selectedProvider is null
            ? $"{selectedType} provider is not registered."
            : $"{selectedProvider.ProviderName} is not configured or not implemented.";

        if (modeAllowsFallback && selectedType != TranslationProviderType.OpusMT)
        {
            return BuildSelection(
                selectedType,
                _machineProvider,
                allowFallback,
                fallbackUsed: true,
                $"Selected provider failed. Falling back to OPUS-MT. Reason: {reason}");
        }

        return new TranslationProviderSelection
        {
            SelectedProviderType = selectedType,
            SelectedProviderName = selectedProvider?.ProviderName ?? selectedType.ToString(),
            ActualProvider = null,
            ActualProviderName = "none",
            FallbackEnabled = allowFallback && _settings.EnableTranslationProviderFallback,
            FallbackProviderName = modeAllowsFallback ? _machineProvider.ProviderName : "none",
            FallbackUsed = false,
            FallbackReason = reason,
            ProviderStatus = selectedProvider is null
                ? TranslationProviderStatus.NotImplemented
                : TranslationProviderStatus.NotConfigured,
        };
    }

    private TranslationProviderType ResolveSelectedProviderType()
    {
        if (_settings.ProviderChainMode == TranslationProviderChainMode.LocalOnly)
            return TranslationProviderType.OpusMT;
        if (_settings.ProviderType == TranslationProviderType.MachineTranslation)
            return TranslationProviderType.OpusMT;
        if (_settings.ProviderType == TranslationProviderType.HybridMachineThenOllama)
            return TranslationProviderType.OpusMT;
        return _settings.ProviderType;
    }

    private ITranslationProvider? ResolveProvider(TranslationProviderType providerType) =>
        providerType switch
        {
            TranslationProviderType.None => null,
            TranslationProviderType.OpusMT or TranslationProviderType.MachineTranslation => _machineProvider,
            TranslationProviderType.Ollama => _ollamaProvider,
            TranslationProviderType.HybridMachineThenOllama => _machineProvider,
            _ => _optionalProviders.TryGetValue(providerType, out var optional) ? optional : null,
        };

    private TranslationProviderSelection BuildSelection(
        TranslationProviderType selectedType,
        ITranslationProvider? actualProvider,
        bool allowFallback,
        bool fallbackUsed,
        string fallbackReason)
    {
        _logger.LogInformation(
            "translation_provider_selected - selectedType={SelectedType}, actualProvider={ActualProvider}",
            selectedType, actualProvider?.ProviderName ?? "none");

        return new TranslationProviderSelection
        {
            SelectedProviderType = selectedType,
            SelectedProviderName = selectedType.ToString(),
            ActualProvider = actualProvider,
            ActualProviderName = actualProvider?.ProviderName ?? "none",
            FallbackEnabled = allowFallback && _settings.EnableTranslationProviderFallback,
            FallbackProviderName = fallbackUsed ? _machineProvider.ProviderName : "none",
            FallbackUsed = fallbackUsed,
            FallbackReason = fallbackReason,
            ProviderStatus = actualProvider is null
                ? TranslationProviderStatus.NotConfigured
                : RequiresOpusServer(actualProvider.ProviderType)
                    ? TranslationProviderStatus.ServerNotRunning
                    : TranslationProviderStatus.Available,
        };
    }

    private IEnumerable<ITranslationProvider> EnumerateKnownProviders()
    {
        yield return _machineProvider;
        yield return _ollamaProvider;
        foreach (var provider in _optionalProviders.Values)
            yield return provider;
    }

    private static bool RequiresOpusServer(TranslationProviderType providerType) =>
        providerType is TranslationProviderType.OpusMT or TranslationProviderType.MachineTranslation;

    private static void SaveProviderHealthDiagnostics(IReadOnlyList<TranslationProviderHealth> health)
    {
        Directory.CreateDirectory(DebugDirectory);
        DebugFileWriter.QueueText(
            Path.Combine(DebugDirectory, "translation_provider_health.json"),
            JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                Providers = health,
            }, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private void SaveSelectionDiagnostics(
        TranslationProviderSelection selection,
        TranslationRequest? request,
        TranslationResult? result,
        IReadOnlyList<TranslationProviderHealth> health)
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            DebugFileWriter.QueueText(
                Path.Combine(DebugDirectory, "last_translation_provider_selection.json"),
                JsonSerializer.Serialize(new
                {
                    Timestamp = DateTimeOffset.Now,
                    SelectedProvider = selection.SelectedProviderType.ToString(),
                    SelectedProviderName = selection.SelectedProviderName,
                    ActualProvider = selection.ActualProviderName,
                    selection.FallbackEnabled,
                    FallbackProvider = selection.FallbackProviderName,
                    selection.FallbackUsed,
                    selection.FallbackReason,
                    ProviderStatus = selection.ProviderStatus.ToString(),
                    ProviderHealthStates = health,
                    ProviderConfigurationStatus = health.Select(h => new
                    {
                        h.ProviderName,
                        h.ProviderType,
                        Status = h.Status.ToString(),
                        h.ConfigurationStatus,
                        h.Message,
                    }),
                    LastTranslationRequest = request is null ? null : new
                    {
                        request.SourceText,
                        request.SourceLanguage,
                        request.TargetLanguage,
                        request.SpeakerName,
                        request.GameProfileName,
                    },
                    LastTranslationResponse = result is null ? null : new
                    {
                        result.ProviderName,
                        result.Success,
                        result.TranslatedText,
                        result.ErrorMessage,
                        result.RawResponse,
                    },
                    ProviderLatency = selection.ProviderLatencyMs,
                }, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save translation provider selection diagnostics");
        }
    }

    private static void SaveFlowDiagnostics(
        TranslationRequest request,
        TranslationResult result,
        TranslationProviderSelection selection)
    {
        Directory.CreateDirectory(DebugDirectory);
        DebugFileWriter.QueueText(
            Path.Combine(DebugDirectory, "last_translation_flow.json"),
            JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                SourceText = request.SourceText,
                request.SpeakerName,
                SpeakerNameExcluded = string.IsNullOrWhiteSpace(request.SpeakerName) ||
                    !request.SourceText.Contains(request.SpeakerName, StringComparison.OrdinalIgnoreCase),
                DialogueText = request.SourceText,
                MemoryHit = false,
                CacheHit = result.FromCache,
                SelectedProvider = selection.SelectedProviderType.ToString(),
                ActualProvider = selection.ActualProviderName,
                selection.FallbackUsed,
                selection.FallbackReason,
                RawProviderTranslation = result.TranslatedText,
                PostprocessedTranslation = result.PostProcessedTranslation,
                FinalTranslation = string.IsNullOrWhiteSpace(result.PostProcessedTranslation)
                    ? result.TranslatedText
                    : result.PostProcessedTranslation,
                SentToOverlay = false,
            }, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }
}

public sealed record TranslationProviderExecutionResult(
    TranslationProviderSelection Selection,
    TranslationResult Result);

public sealed record TranslationProviderSelection
{
    public TranslationProviderType SelectedProviderType { get; init; }
    public string SelectedProviderName { get; init; } = string.Empty;
    public ITranslationProvider? ActualProvider { get; init; }
    public string ActualProviderName { get; init; } = string.Empty;
    public bool FallbackEnabled { get; init; }
    public string FallbackProviderName { get; init; } = string.Empty;
    public bool FallbackUsed { get; init; }
    public string FallbackReason { get; init; } = string.Empty;
    public TranslationProviderStatus ProviderStatus { get; init; }
    public long ProviderLatencyMs { get; init; }
    public IReadOnlyList<TranslationProviderHealth> ProviderHealthStates { get; init; } = [];
}
