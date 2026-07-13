using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Subtitles;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Translation;

namespace PsGameTranslator.Infrastructure.Subtitles;

public sealed class SubtitleDisplayUpdateResult
{
    public bool ShouldUpdateOverlay { get; init; }
    public string DisplayText { get; init; } = string.Empty;
    public bool ShouldEnqueueTranslation { get; init; }
    public string Reason { get; init; } = string.Empty;
    public bool WasSourceIgnoredBecauseTranslationExists { get; init; }
    public bool WasTranslationLate { get; init; }
    public bool WasCacheHit { get; init; }
}

public sealed class SubtitleDisplayStateSnapshot
{
    public string CurrentSourceText { get; init; } = string.Empty;
    public string CurrentNormalizedSourceKey { get; init; } = string.Empty;
    public string CurrentDisplayText { get; init; } = string.Empty;
    public SubtitleDisplayLanguage CurrentDisplayLanguage { get; init; }
    public SubtitleDisplayState CurrentDisplayState { get; init; } = SubtitleDisplayState.Empty;
    public string CurrentTranslationText { get; init; } = string.Empty;
    public string LastOverlayUpdateReason { get; init; } = string.Empty;
    public DateTimeOffset? LastOverlayUpdatedAt { get; init; }
    public bool WasSourceIgnoredBecauseTranslationExists { get; init; }
    public bool WasTranslationLate { get; init; }
    public bool WasCacheHit { get; init; }
}

public sealed class SubtitleDisplayStateManager
{
    private static readonly string StateFilePath = Path.Combine(
        AppContext.BaseDirectory, "debug", "last_overlay_state.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly TranslationSettings _translationSettings;
    private readonly TranslationCache _translationCache;
    private readonly ISubtitleFormatter _subtitleFormatter;
    private readonly ILogger<SubtitleDisplayStateManager> _logger;

    private string _currentSourceText = string.Empty;
    private string _currentSourceDisplayText = string.Empty;
    private string _currentNormalizedSourceKey = string.Empty;
    private string _currentDisplayText = string.Empty;
    private SubtitleDisplayLanguage _currentDisplayLanguage = SubtitleDisplayLanguage.Unknown;
    private SubtitleDisplayState _currentDisplayState = SubtitleDisplayState.Empty;
    private string _currentTranslationText = string.Empty;
    private string _lastOverlayUpdateReason = "empty";
    private DateTimeOffset? _lastOverlayUpdatedAt;
    private bool _wasSourceIgnoredBecauseTranslationExists;
    private bool _wasTranslationLate;
    private bool _wasCacheHit;

    public SubtitleDisplayStateManager(
        TranslationSettings translationSettings,
        TranslationCache translationCache,
        ISubtitleFormatter subtitleFormatter,
        ILogger<SubtitleDisplayStateManager> logger)
    {
        _translationSettings = translationSettings;
        _translationCache = translationCache;
        _subtitleFormatter = subtitleFormatter;
        _logger = logger;
    }

    public SubtitleDisplayStateSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return CreateSnapshot();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _currentSourceText = string.Empty;
            _currentSourceDisplayText = string.Empty;
            _currentNormalizedSourceKey = string.Empty;
            _currentDisplayText = string.Empty;
            _currentDisplayLanguage = SubtitleDisplayLanguage.Unknown;
            _currentDisplayState = SubtitleDisplayState.Empty;
            _currentTranslationText = string.Empty;
            _lastOverlayUpdateReason = "reset";
            _lastOverlayUpdatedAt = null;
            _wasSourceIgnoredBecauseTranslationExists = false;
            _wasTranslationLate = false;
            _wasCacheHit = false;
            PersistSnapshotUnsafe();
        }
    }

    public async Task<SubtitleDisplayUpdateResult> HandleSourceAsync(
        FormattedSubtitle subtitle,
        CancellationToken cancellationToken)
    {
        var sourceText = !string.IsNullOrWhiteSpace(subtitle.MainText)
            ? subtitle.MainText.Trim()
            : subtitle.DisplayText.Trim();
        var normalizedKey = SubtitleTextNormalizer.NormalizeKey(sourceText);

        if (normalizedKey.Length == 0)
        {
            return new SubtitleDisplayUpdateResult { Reason = "empty_source_ignored" };
        }

        TranslationRequest? cacheRequest = null;
        string? cachedTranslation = null;
        var shouldTryCache = IsTranslationActive() && _translationSettings.UseTranslationCache;
        if (shouldTryCache)
        {
            cacheRequest = BuildRequest(sourceText);
            _translationCache.TryGet(cacheRequest, out cachedTranslation!);
        }

        FormattedSubtitle? cachedFormattedSubtitle = null;
        if (!string.IsNullOrWhiteSpace(cachedTranslation))
        {
            cachedFormattedSubtitle = await _subtitleFormatter
                .FormatAsync(cachedTranslation, subtitle.Confidence, cancellationToken)
                .ConfigureAwait(false);
        }

        lock (_gate)
        {
            _wasSourceIgnoredBecauseTranslationExists = false;
            _wasTranslationLate = false;
            _wasCacheHit = false;

            if (normalizedKey == _currentNormalizedSourceKey)
            {
                if (_currentDisplayState == SubtitleDisplayState.ShowingTranslated &&
                    _translationSettings.KeepTranslatedTextWhileSameSourceDetected)
                {
                    _wasSourceIgnoredBecauseTranslationExists = true;
                    return FinalizeResult(new SubtitleDisplayUpdateResult
                    {
                        DisplayText = _currentDisplayText,
                        Reason = "kept_translation_same_source",
                        WasSourceIgnoredBecauseTranslationExists = true,
                    });
                }

                if (_currentDisplayState == SubtitleDisplayState.ShowingSourcePendingTranslation)
                {
                    return FinalizeResult(new SubtitleDisplayUpdateResult
                    {
                        DisplayText = _currentDisplayText,
                        Reason = "same_source_pending_translation",
                    });
                }

                return FinalizeResult(new SubtitleDisplayUpdateResult
                {
                    DisplayText = _currentDisplayText,
                    Reason = "same_source_no_overlay_change",
                });
            }

            _currentSourceText = sourceText;
            _currentSourceDisplayText = subtitle.DisplayText;
            _currentNormalizedSourceKey = normalizedKey;
            _currentTranslationText = string.Empty;

            if (!IsTranslationActive())
            {
                return ApplyDisplayUnsafe(
                    subtitle.DisplayText,
                    SubtitleDisplayLanguage.Source,
                    SubtitleDisplayState.ShowingSourceFallback,
                    "translation_disabled_show_source",
                    shouldEnqueueTranslation: false);
            }

            if (!string.IsNullOrWhiteSpace(cachedTranslation) && cachedFormattedSubtitle is not null)
            {
                _currentTranslationText = cachedTranslation;
                _wasCacheHit = true;
                return ApplyDisplayUnsafe(
                    cachedFormattedSubtitle.DisplayText,
                    SubtitleDisplayLanguage.Target,
                    SubtitleDisplayState.ShowingTranslated,
                    "cache_hit_show_translation_immediately",
                    shouldEnqueueTranslation: false,
                    wasCacheHit: true);
            }

            if (ShouldShowSourceWhileTranslating())
            {
                return ApplyDisplayUnsafe(
                    subtitle.DisplayText,
                    SubtitleDisplayLanguage.Source,
                    SubtitleDisplayState.ShowingSourcePendingTranslation,
                    "new_source_show_source_pending_translation",
                    shouldEnqueueTranslation: true);
            }

            _currentDisplayState = SubtitleDisplayState.ShowingSourcePendingTranslation;
            _lastOverlayUpdateReason = "new_source_queued_without_overlay_change";
            PersistSnapshotUnsafe();
            return new SubtitleDisplayUpdateResult
            {
                DisplayText = _currentDisplayText,
                ShouldEnqueueTranslation = true,
                Reason = _lastOverlayUpdateReason,
            };
        }
    }

    public async Task<SubtitleDisplayUpdateResult> HandleTranslationAsync(
        TranslationResult result,
        CancellationToken cancellationToken)
    {
        var normalizedKey = SubtitleTextNormalizer.NormalizeKey(result.SourceText);
        FormattedSubtitle? formattedTranslatedSubtitle = null;
        if (result.Success)
        {
            formattedTranslatedSubtitle = await _subtitleFormatter
                .FormatAsync(result.TranslatedText, 1.0, cancellationToken)
                .ConfigureAwait(false);
        }

        lock (_gate)
        {
            _wasSourceIgnoredBecauseTranslationExists = false;
            _wasTranslationLate = false;
            _wasCacheHit = result.FromCache;

            if (normalizedKey != _currentNormalizedSourceKey)
            {
                _wasTranslationLate = true;
                _lastOverlayUpdateReason = "late_translation_saved_to_cache";
                PersistSnapshotUnsafe();
                return new SubtitleDisplayUpdateResult
                {
                    DisplayText = _currentDisplayText,
                    Reason = _lastOverlayUpdateReason,
                    WasTranslationLate = true,
                    WasCacheHit = result.FromCache,
                };
            }

            if (!result.Success)
            {
                if (_translationSettings.ShowOcrFallbackWhenTranslationFails &&
                    !string.IsNullOrWhiteSpace(_currentSourceDisplayText))
                {
                    return ApplyDisplayUnsafe(
                        _currentSourceDisplayText,
                        SubtitleDisplayLanguage.Source,
                        SubtitleDisplayState.ShowingSourceFallback,
                        "translation_failed_show_source_fallback",
                        shouldEnqueueTranslation: false,
                        shouldUpdateOverlay: true);
                }

                _lastOverlayUpdateReason = "translation_failed_overlay_unchanged";
                PersistSnapshotUnsafe();
                return new SubtitleDisplayUpdateResult
                {
                    DisplayText = _currentDisplayText,
                    Reason = _lastOverlayUpdateReason,
                };
            }

            _currentTranslationText = result.TranslatedText;
            return ApplyDisplayUnsafe(
                formattedTranslatedSubtitle?.DisplayText ?? result.TranslatedText,
                SubtitleDisplayLanguage.Target,
                SubtitleDisplayState.ShowingTranslated,
                "translation_replaced_current_source",
                shouldEnqueueTranslation: false,
                shouldUpdateOverlay: true,
                wasCacheHit: result.FromCache);
        }
    }

    public SubtitleDisplayUpdateResult HoldCurrent(string reason)
    {
        lock (_gate)
        {
            if (_currentDisplayState == SubtitleDisplayState.Empty)
            {
                return FinalizeResult(new SubtitleDisplayUpdateResult
                {
                    DisplayText = string.Empty,
                    Reason = "hold_ignored_empty",
                });
            }

            _currentDisplayState = SubtitleDisplayState.HoldingPrevious;
            _lastOverlayUpdateReason = reason;
            PersistSnapshotUnsafe();
            return new SubtitleDisplayUpdateResult
            {
                DisplayText = _currentDisplayText,
                Reason = reason,
            };
        }
    }

    public SubtitleDisplayUpdateResult Clear(string reason)
    {
        lock (_gate)
        {
            _currentSourceText = string.Empty;
            _currentSourceDisplayText = string.Empty;
            _currentNormalizedSourceKey = string.Empty;
            _currentDisplayText = string.Empty;
            _currentDisplayLanguage = SubtitleDisplayLanguage.Unknown;
            _currentDisplayState = SubtitleDisplayState.Empty;
            _currentTranslationText = string.Empty;
            _lastOverlayUpdatedAt = DateTimeOffset.Now;
            _lastOverlayUpdateReason = reason;
            PersistSnapshotUnsafe();
            return new SubtitleDisplayUpdateResult
            {
                DisplayText = string.Empty,
                ShouldUpdateOverlay = true,
                Reason = reason,
            };
        }
    }

    private SubtitleDisplayUpdateResult ApplyDisplayUnsafe(
        string displayText,
        SubtitleDisplayLanguage language,
        SubtitleDisplayState state,
        string reason,
        bool shouldEnqueueTranslation,
        bool shouldUpdateOverlay = true,
        bool wasCacheHit = false)
    {
        _currentDisplayText = displayText;
        _currentDisplayLanguage = language;
        _currentDisplayState = state;
        _lastOverlayUpdatedAt = DateTimeOffset.Now;
        _lastOverlayUpdateReason = reason;
        _wasCacheHit = wasCacheHit;
        PersistSnapshotUnsafe();
        _logger.LogInformation(
            "overlay_state_update - reason={Reason}, state={State}, language={Language}, cacheHit={CacheHit}",
            reason,
            state,
            language,
            wasCacheHit);
        return new SubtitleDisplayUpdateResult
        {
            DisplayText = displayText,
            ShouldUpdateOverlay = shouldUpdateOverlay,
            ShouldEnqueueTranslation = shouldEnqueueTranslation,
            Reason = reason,
            WasCacheHit = wasCacheHit,
        };
    }

    private SubtitleDisplayUpdateResult FinalizeResult(SubtitleDisplayUpdateResult result)
    {
        _lastOverlayUpdateReason = result.Reason;
        PersistSnapshotUnsafe();
        _logger.LogInformation(
            "overlay_state_kept - reason={Reason}, state={State}, language={Language}",
            result.Reason,
            _currentDisplayState,
            _currentDisplayLanguage);
        return result;
    }

    private TranslationRequest BuildRequest(string sourceText) => new()
    {
        SourceText = sourceText,
        SourceLanguage = _translationSettings.SourceLanguage,
        TargetLanguage = _translationSettings.TargetLanguage,
        GameProfileName = _translationSettings.GameProfile,
    };

    private bool IsTranslationActive() =>
        _translationSettings.EnableTranslation &&
        _translationSettings.ProviderType != TranslationProviderType.None &&
        _translationSettings.DisplayMode != TranslationDisplayMode.OcrOnly;

    private bool ShouldShowSourceWhileTranslating() =>
        _translationSettings.DisplayMode != TranslationDisplayMode.TranslatedOnly ||
        _translationSettings.ShowSourceWhileTranslating;

    private SubtitleDisplayStateSnapshot CreateSnapshot() => new()
    {
        CurrentSourceText = _currentSourceText,
        CurrentNormalizedSourceKey = _currentNormalizedSourceKey,
        CurrentDisplayText = _currentDisplayText,
        CurrentDisplayLanguage = _currentDisplayLanguage,
        CurrentDisplayState = _currentDisplayState,
        CurrentTranslationText = _currentTranslationText,
        LastOverlayUpdateReason = _lastOverlayUpdateReason,
        LastOverlayUpdatedAt = _lastOverlayUpdatedAt,
        WasSourceIgnoredBecauseTranslationExists = _wasSourceIgnoredBecauseTranslationExists,
        WasTranslationLate = _wasTranslationLate,
        WasCacheHit = _wasCacheHit,
    };

    private void PersistSnapshotUnsafe()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
            var json = JsonSerializer.Serialize(CreateSnapshot(), JsonOptions);
            File.WriteAllText(StateFilePath, json, Encoding.UTF8);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save overlay state diagnostics");
        }
    }
}
