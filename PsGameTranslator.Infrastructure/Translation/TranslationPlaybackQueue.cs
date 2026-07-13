using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Core.Subtitles;
using PsGameTranslator.Overlay;

namespace PsGameTranslator.Infrastructure.Translation;

/// <summary>
/// The single choke point that writes Turkish text to the overlay in
/// TurkishOnlyMode. Holds ready translations in arrival order and displays
/// them one at a time, respecting readable display timing so short dialogue
/// does not flicker away or get replaced too early.
/// </summary>
public sealed class TranslationPlaybackQueue : IDisposable
{
    private readonly IOverlayService _overlayService;
    private readonly TranslationSettings _settings;
    private readonly SubtitleFormatterSettings _formatterSettings;
    private readonly PipelineDiagnostics _diagnostics;
    private readonly PipelineDiagnosticsStore _diagnosticsStore;
    private readonly ILogger<TranslationPlaybackQueue> _logger;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly List<TranslatedSubtitleDisplayItem> _queue = [];

    private TranslatedSubtitleDisplayItem? _currentlyDisplayed;
    private DateTimeOffset? _displayedAt;
    private DateTimeOffset _lastActivityAt = DateTimeOffset.Now;
    private string _lastOverlayText = string.Empty;
    private ReplacementSubtitleState _replacementState = new();
    private DateTimeOffset _lastOverlayClosedWarningAt = DateTimeOffset.MinValue;

    /// <summary>Warn (throttled) when translations are ready but the overlay window is closed —
    /// otherwise every write is silently suppressed and the app looks broken.</summary>
    private void WarnOverlayClosedUnsafe(string context)
    {
        _diagnostics.LastOverlayUpdateSource = "OVERLAY_CLOSED_SUPPRESSED";
        if ((DateTimeOffset.Now - _lastOverlayClosedWarningAt).TotalSeconds < 10) return;
        _lastOverlayClosedWarningAt = DateTimeOffset.Now;
        _logger.LogWarning(
            "OVERLAY_CLOSED - translations are ready but the overlay window is not open ({Context}). " +
            "Open the overlay (or enable auto-start overlay) to see subtitles.", context);
        _diagnosticsStore.Save();
        _diagnostics.NotifyChanged();
    }

    public TranslationPlaybackQueue(
        IOverlayService overlayService,
        TranslationSettings settings,
        SubtitleFormatterSettings formatterSettings,
        PipelineDiagnostics diagnostics,
        PipelineDiagnosticsStore diagnosticsStore,
        ILogger<TranslationPlaybackQueue> logger)
    {
        _overlayService = overlayService;
        _settings = settings;
        _formatterSettings = formatterSettings;
        _diagnostics = diagnostics;
        _diagnosticsStore = diagnosticsStore;
        _logger = logger;
        _pump = Task.Run(PumpLoopAsync);
    }

    public int Count { get { lock (_gate) return _queue.Count + (_currentlyDisplayed is null ? 0 : 1); } }

    public void NotifyActivity(
        string sourceKey,
        string sourceText,
        SubtitleReplacementContext? replacementContext = null)
    {
        lock (_gate)
        {
            _lastActivityAt = DateTimeOffset.Now;

            if (IsReplacementMode &&
                replacementContext is not null &&
                _settings.ShowMaskWhileTranslationPending)
            {
                var isSameSource = string.Equals(
                    _replacementState.CurrentSourceKey,
                    sourceKey,
                    StringComparison.Ordinal);

                if (isSameSource)
                {
                    _replacementState.LastSeenAt = DateTimeOffset.Now;
                    _replacementState.CurrentOriginalSubtitleRect = replacementContext.ScreenRect.Clone();
                }
                else
                {
                    if (_currentlyDisplayed is not null &&
                        _currentlyDisplayed.NormalizedSourceKey != sourceKey)
                    {
                        _logger.LogInformation(
                            "Replacement source changed; detaching old Turkish. old={OldKey}, new={NewKey}",
                            _currentlyDisplayed.NormalizedSourceKey,
                            sourceKey);
                    }

                    _currentlyDisplayed = null;
                    _displayedAt = null;
                    _queue.RemoveAll(item => item.NormalizedSourceKey != sourceKey);
                    _lastOverlayText = string.Empty;
                    _replacementState = new ReplacementSubtitleState
                    {
                        CurrentSourceKey = sourceKey,
                        CurrentSourceText = sourceText,
                        CurrentOriginalSubtitleRect = replacementContext.ScreenRect.Clone(),
                        CurrentStatus = ReplacementSubtitleStatus.MaskingPendingTranslation,
                        FirstSeenAt = DateTimeOffset.Now,
                        LastSeenAt = DateTimeOffset.Now,
                        TranslationStartedAt = DateTimeOffset.Now,
                    };
                }

                if (!isSameSource || _replacementState.CurrentStatus == ReplacementSubtitleStatus.MaskingPendingTranslation)
                    TryShowPendingMaskUnsafe(sourceKey, sourceText, replacementContext);
            }
        }
        _signal.Release();
    }

    public void Enqueue(TranslatedSubtitleDisplayItem item)
    {
        lock (_gate)
        {
            _lastActivityAt = DateTimeOffset.Now;
            TrimExpiredUnsafe();

            if (IsReplacementMode &&
                !string.Equals(item.NormalizedSourceKey, _replacementState.CurrentSourceKey, StringComparison.Ordinal))
            {
                // Fast dialogue: the source line changed while this translation was in
                // flight. Instead of always discarding (which loses whole lines during
                // rapid exchanges), show it briefly when it is still fresh and the
                // newer line's translation has not arrived yet.
                var ageMs = (DateTimeOffset.Now - item.CreatedAt).TotalMilliseconds;
                var currentKeyHasTranslation =
                    _queue.Any(q => string.Equals(q.NormalizedSourceKey, _replacementState.CurrentSourceKey, StringComparison.Ordinal)) ||
                    (_currentlyDisplayed is not null &&
                     string.Equals(_currentlyDisplayed.NormalizedSourceKey, _replacementState.CurrentSourceKey, StringComparison.Ordinal));
                var graceApplies = _settings.EnableStaleTranslationGraceWindow &&
                    ageMs <= _settings.StaleTranslationGraceMs &&
                    !currentKeyHasTranslation;

                if (!graceApplies)
                {
                    _diagnostics.ExpiredSkippedCount++;
                    _diagnostics.LastOverlayUpdateSource = "STALE_TRANSLATION_NOT_DISPLAYED";
                    _logger.LogInformation(
                        "Late translation retained outside display; current={CurrentKey}, completed={CompletedKey}",
                        _replacementState.CurrentSourceKey,
                        item.NormalizedSourceKey);
                    SaveReplacementDiagnosticsUnsafe(item.TranslatedText, item, "STALE_TRANSLATION_NOT_DISPLAYED", false);
                    return;
                }

                _diagnostics.LastOverlayUpdateSource = "STALE_TRANSLATION_GRACE_DISPLAYED";
                _logger.LogInformation(
                    "Late translation shown within grace window ({AgeMs:F0} ms); current={CurrentKey}, completed={CompletedKey}",
                    ageMs, _replacementState.CurrentSourceKey, item.NormalizedSourceKey);
            }

            if (IsReplacementMode)
            {
                _replacementState.TranslationCompletedAt = DateTimeOffset.Now;
                _replacementState.TranslationRecordId = item.TranslationRecordId;
            }

            if (_settings.ReplaceSameSourceIfBetter)
            {
                if (_currentlyDisplayed is not null &&
                    _currentlyDisplayed.NormalizedSourceKey == item.NormalizedSourceKey)
                {
                    _currentlyDisplayed.TranslatedText = item.TranslatedText;
                    WriteOverlayUnsafe(item.TranslatedText, item.Source, _currentlyDisplayed);
                    return;
                }

                var existingIndex = _queue.FindIndex(
                    q => q.NormalizedSourceKey == item.NormalizedSourceKey);
                if (existingIndex >= 0)
                {
                    _queue[existingIndex] = item;
                    return;
                }
            }

            _queue.Add(item);
            while (_queue.Count > Math.Max(1, _settings.MaxPlaybackQueueSize))
            {
                _queue.RemoveAt(0);
                _diagnostics.ExpiredSkippedCount++;
            }
        }
        _signal.Release();
    }

    public IReadOnlyList<TranslatedSubtitleDisplayItem> GetSnapshot()
    {
        lock (_gate)
        {
            var snapshot = new List<TranslatedSubtitleDisplayItem>();
            if (_currentlyDisplayed is not null) snapshot.Add(_currentlyDisplayed);
            snapshot.AddRange(_queue);
            return snapshot;
        }
    }

    private bool IsReplacementMode =>
        _overlayService.CurrentSettings.DisplayMode == SubtitleDisplayMode.SubtitleReplacementOverlay;

    private async Task PumpLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            // 50 ms poll: enqueues wake the pump immediately via _signal; the
            // timeout only drives min-display-time transitions and idle clearing.
            try { await _signal.WaitAsync(50, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try { Tick(); }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "translation_playback_queue_tick_failed");
            }
        }
    }

    private void Tick()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            TrimExpiredUnsafe();

            if (_currentlyDisplayed is null)
            {
                if (_queue.Count > 0)
                {
                    DisplayNextUnsafe();
                }
                else if ((!string.IsNullOrEmpty(_lastOverlayText) ||
                    (IsReplacementMode && _replacementState.CurrentStatus == ReplacementSubtitleStatus.MaskingPendingTranslation)) &&
                    (now - _lastActivityAt).TotalMilliseconds >= _settings.ClearOverlayAfterNoSubtitleMs)
                {
                    WriteOverlayUnsafe(string.Empty, "PLAYBACK_QUEUE", null);
                }
                return;
            }

            var currentMinDisplayMs = (double)Math.Max(
                _settings.MinTurkishDisplayMs,
                _currentlyDisplayed.DisplayDurationMs > 0
                    ? _currentlyDisplayed.DisplayDurationMs
                    : _currentlyDisplayed.MinDisplayMs);

            // Under burst pressure, shrink the hold time so queued lines drain
            // in order instead of being trimmed away.
            if (_queue.Count > 1)
                currentMinDisplayMs = Math.Max(
                    _settings.MinTurkishDisplayUnderPressureMs,
                    currentMinDisplayMs / (1 + 0.5 * (_queue.Count - 1)));

            var minElapsed = _displayedAt is null ||
                (now - _displayedAt.Value).TotalMilliseconds >= currentMinDisplayMs;

            if (_queue.Count > 0 && minElapsed)
            {
                DisplayNextUnsafe();
                return;
            }

            if (_queue.Count == 0 &&
                minElapsed &&
                (now - _lastActivityAt).TotalMilliseconds >= _settings.ClearOverlayAfterNoSubtitleMs)
            {
                _currentlyDisplayed = null;
                _displayedAt = null;
                WriteOverlayUnsafe(string.Empty, "PLAYBACK_QUEUE", null);
                return;
            }

            if (IsReplacementMode && minElapsed)
            {
                _replacementState.CurrentStatus = ReplacementSubtitleStatus.HoldingTurkish;
                _diagnostics.LastReplacementStatus = _replacementState.CurrentStatus.ToString();
            }

            _diagnostics.LastOverlayUpdateSource = "PREVIOUS_TURKISH_HOLD";
        }
    }

    private void DisplayNextUnsafe()
    {
        while (_queue.Count > 0)
        {
            var next = _queue[0];
            _queue.RemoveAt(0);

            if (_settings.DropExpiredTranslations &&
                (DateTimeOffset.Now - next.CreatedAt).TotalMilliseconds > _settings.MaxTranslationAgeForDisplayMs)
            {
                _diagnostics.ExpiredSkippedCount++;
                continue;
            }

            next.DisplayedAt = DateTimeOffset.Now;
            _currentlyDisplayed = next;
            _displayedAt = next.DisplayedAt;
            if (IsReplacementMode)
            {
                _replacementState.CurrentTurkishText = next.TranslatedText;
                _replacementState.CurrentStatus = ReplacementSubtitleStatus.ShowingTurkish;
                _replacementState.DisplayStartedAt = next.DisplayedAt;
                _replacementState.MinDisplayUntil = next.DisplayedAt?.AddMilliseconds(next.DisplayDurationMs);
            }
            WriteOverlayUnsafe(next.TranslatedText, next.Source, next);
            _diagnostics.LastDisplayedTurkishText = next.TranslatedText;
            _diagnostics.LastSubtitleDisplayDurationMs = next.DisplayDurationMs;

            var displayedAt = next.DisplayedAt ?? DateTimeOffset.Now;
            _logger.LogInformation(
                "stage_latency - stage=overlay playback_wait_ms={WaitMs:F0} e2e_ms={EndToEndMs:F0} " +
                "memory_hit={MemoryHit} cache_hit={CacheHit}",
                (displayedAt - next.ReadyAt).TotalMilliseconds,
                (displayedAt - next.CreatedAt).TotalMilliseconds,
                next.FromMemory, next.FromCache);
            return;
        }

        _currentlyDisplayed = null;
        _displayedAt = null;
    }

    private void TrimExpiredUnsafe()
    {
        if (!_settings.DropExpiredTranslations) return;

        _queue.RemoveAll(item =>
        {
            var expired = (DateTimeOffset.Now - item.CreatedAt).TotalMilliseconds > _settings.MaxTranslationAgeForDisplayMs;
            if (expired) _diagnostics.ExpiredSkippedCount++;
            return expired;
        });
    }

    private void TryShowPendingMaskUnsafe(
        string sourceKey,
        string sourceText,
        SubtitleReplacementContext context)
    {
        if (!_overlayService.IsOpen)
        {
            WarnOverlayClosedUnsafe("pending mask");
            return;
        }

        try
        {
            _overlayService.UpdateReplacementOverlay(new SubtitleReplacementOverlayUpdate
            {
                Text = string.Empty,
                SourceText = sourceText,
                Reason = "PENDING_MASK",
                ShowMaskOnly = true,
                DisplayDurationMs = 0,
                Context = context.Clone(),
            });
            _diagnostics.LastReplacementSourceKey = sourceKey;
            _diagnostics.LastReplacementStatus = ReplacementSubtitleStatus.MaskingPendingTranslation.ToString();
            SaveReplacementDiagnosticsUnsafe(string.Empty, null, "PENDING_MASK", false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to show pending replacement mask");
        }
    }

    private void WriteOverlayUnsafe(string text, string source, TranslatedSubtitleDisplayItem? item)
    {
        if (!_settings.TurkishOnlyMode) return;

        _lastOverlayText = text;
        _diagnostics.LastOverlayUpdateSource = source;
        _diagnostics.PlaybackQueueCount = _queue.Count + (_currentlyDisplayed is null ? 0 : 1);
        _diagnosticsStore.Save();

        if (!_overlayService.IsOpen)
        {
            if (!string.IsNullOrEmpty(text))
                WarnOverlayClosedUnsafe("translated text");
            return;
        }

        try
        {
            if (IsReplacementMode && string.IsNullOrWhiteSpace(text))
            {
                _replacementState.CurrentStatus = ReplacementSubtitleStatus.Expired;
                _overlayService.UpdateText(string.Empty);
                SaveReplacementDiagnosticsUnsafe(string.Empty, item, source, false);
            }
            else if (IsReplacementMode && item?.ReplacementContext is not null)
            {
                // Anti-English guard: block text identical to the OCR source — except
                // very short lines (interjections/proper nouns like "Haymish!" or
                // "Vermund."), whose correct Turkish IS the source text.
                var normalizedOverlay = SubtitleTextNormalizer.NormalizeKey(text);
                var normalizedSource = SubtitleTextNormalizer.NormalizeKey(item.SourceText);
                var equalsSource = string.Equals(normalizedOverlay, normalizedSource, StringComparison.Ordinal);
                var sourceWordCount = normalizedSource.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                if ((equalsSource && sourceWordCount > 2) ||
                    text.TrimStart().StartsWith("[TR]", StringComparison.OrdinalIgnoreCase))
                {
                    _diagnostics.WasEnglishBlockedInReplacementMode = true;
                    _diagnostics.WasEnglishShownInReplacementMode = false;
                    _logger.LogError("ERROR_ENGLISH_DISPLAYED_IN_REPLACEMENT_MODE blocked source={Source}", item.SourceText);
                    TryShowPendingMaskUnsafe(item.NormalizedSourceKey, item.SourceText, item.ReplacementContext);
                    return;
                }

                _overlayService.UpdateReplacementOverlay(new SubtitleReplacementOverlayUpdate
                {
                    Text = text,
                    SourceText = item.SourceText,
                    SpeakerName = GetSpeakerForDisplay(item),
                    Reason = source,
                    ShowMaskOnly = false,
                    DisplayDurationMs = item.DisplayDurationMs,
                    Context = item.ReplacementContext.Clone(),
                });
                _diagnostics.WasEnglishShownInReplacementMode = false;
                _diagnostics.WasEnglishBlockedInReplacementMode = false;
                _diagnostics.LastReplacementStatus = _replacementState.CurrentStatus.ToString();
                _diagnostics.LastReplacementSourceKey = _replacementState.CurrentSourceKey;
                SaveReplacementDiagnosticsUnsafe(text, item, source, false);
            }
            else
            {
                if (IsReplacementMode)
                {
                    _diagnostics.WasEnglishBlockedInReplacementMode = true;
                    _diagnostics.WasEnglishShownInReplacementMode = false;
                    _logger.LogError("ERROR_ENGLISH_DISPLAYED_IN_REPLACEMENT_MODE blocked unbound overlay update");
                }
                else
                {
                    // Speaker on its own line above the Turkish text — never merged
                    // into the translated sentence (Part E).
                    var speaker = GetSpeakerForDisplay(item);
                    _overlayService.UpdateText(
                        speaker.Length > 0 && text.Length > 0 ? speaker + "\n" + text : text);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to update overlay text from playback queue");
            _diagnostics.LastOverlayUpdateSource = "ERROR";
        }

        _diagnostics.NotifyChanged();
    }

    /// <summary>Part E: speaker is display metadata only, gated by ShowSpeakerName.</summary>
    private string GetSpeakerForDisplay(TranslatedSubtitleDisplayItem? item) =>
        _formatterSettings.ShowSpeakerName && !string.IsNullOrWhiteSpace(item?.SpeakerName)
            ? item!.SpeakerName.Trim()
            : string.Empty;

    private void SaveReplacementDiagnosticsUnsafe(
        string translatedText,
        TranslatedSubtitleDisplayItem? item,
        string reason,
        bool englishShown)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "debug"));

            var snapshot = _overlayService.LastReplacementSnapshot;
            if (snapshot is not null)
            {
                _diagnostics.LastReplacementRect = snapshot.Context.OverlayRect.ToString();
                _diagnostics.LastReplacementSourceRect = snapshot.Context.ScreenRect.ToString();
            }

            var coordinatesJson = JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                OcrLineBoxes = snapshot?.Context.OcrLineBoxes,
                SelectedLineBoxes = snapshot?.Context.SelectedLineBoxes,
                OcrLineRect = snapshot?.Context.OcrLineRect,
                UnionSubtitleRectInCrop = snapshot?.Context.UnionSubtitleRectInCrop,
                CropRect = snapshot?.Context.CropRect,
                CropRectInWindow = snapshot?.Context.CropRectInWindow,
                WindowRect = snapshot?.Context.WindowRect,
                ScreenRect = snapshot?.Context.ScreenRect,
                OverlayRect = snapshot?.Context.OverlayRect,
                DpiScale = new
                {
                    DpiScaleX = snapshot?.Context.DpiScaleX,
                    DpiScaleY = snapshot?.Context.DpiScaleY,
                },
                UsedFallbackRegion = snapshot?.Context.UsedFallbackRegion,
                SelectedLinesText = snapshot?.Context.SelectedLinesText,
                Monitor = new
                {
                    DeviceName = snapshot?.Context.MonitorDeviceName,
                    Bounds = snapshot?.Context.MonitorBounds,
                },
            }, new JsonSerializerOptions { WriteIndented = true });

            DebugFileWriter.QueueText(
                Path.Combine(AppContext.BaseDirectory, "debug", "last_replacement_overlay_coordinates.json"),
                coordinatesJson,
                Encoding.UTF8);

            var speakerName = item?.SpeakerName ?? string.Empty;
            var translationInput = item?.SourceText ?? string.Empty;
            var wasSpeakerIncludedInTranslationInput = speakerName.Length > 0 &&
                translationInput.TrimStart().StartsWith(speakerName + " ", StringComparison.OrdinalIgnoreCase);
            if (wasSpeakerIncludedInTranslationInput)
                _logger.LogError(
                    "ERROR_SPEAKER_NAME_SENT_TO_TRANSLATION - speaker={Speaker}", speakerName);

            var stateJson = JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                SourceText = item?.SourceText ?? snapshot?.SourceText ?? string.Empty,
                TranslatedText = translatedText,
                SpeakerName = speakerName.Length > 0 ? speakerName : null,
                DialogueText = translationInput,
                TranslationInput = translationInput,
                WasSpeakerIncludedInTranslationInput = wasSpeakerIncludedInTranslationInput,
                SourceKey = item?.NormalizedSourceKey ?? string.Empty,
                OriginalSubtitleRect = snapshot?.Context.ScreenRect,
                ReplacementRect = snapshot?.Context.OverlayRect,
                MaskSettings = _overlayService.CurrentSettings.Replacement,
                DisplayDurationMs = item?.DisplayDurationMs ?? snapshot?.DisplayDurationMs ?? 0,
                QueueState = new
                {
                    Current = _currentlyDisplayed?.NormalizedSourceKey,
                    PendingCount = _queue.Count,
                    PendingKeys = _queue.Select(q => q.NormalizedSourceKey).ToArray(),
                },
                MemoryHit = item?.FromMemory ?? false,
                CacheHit = item?.FromCache ?? false,
                TranslationDurationMs = _diagnostics.LastTranslationDurationMs,
                OverlayUpdateReason = reason,
                EnglishWasShown = englishShown,
                ReplacementState = _replacementState.Clone(),
            }, new JsonSerializerOptions { WriteIndented = true });

            DebugFileWriter.QueueText(
                Path.Combine(AppContext.BaseDirectory, "debug", "last_subtitle_replacement_state.json"),
                stateJson,
                Encoding.UTF8);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save replacement overlay diagnostics");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _pump.Wait(2000); } catch { }
        _cts.Dispose();
        _signal.Dispose();
    }
}
