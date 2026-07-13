using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Subtitles;

namespace PsGameTranslator.Infrastructure.Translation;

/// <summary>
/// Coordinates the ordered subtitle translation pipeline (Parts C–J):
/// SubtitleCandidateValidator → OrderedSubtitleCaptureQueue → TranslationMemory/Cache/
/// in-flight lookup → ordered OPUS-MT dispatch → glossary post-processing →
/// TranslationPlaybackQueue. This is the sole entry point MonitoringViewModel calls
/// when TurkishOnlyMode is enabled; OCR never touches the overlay directly.
/// </summary>
public sealed class OrderedSubtitlePipeline : IDisposable
{
    private readonly OrderedSubtitleCaptureQueue _captureQueue;
    private readonly SubtitleCandidateValidator _validator;
    private readonly TranslationPlaybackQueue _playbackQueue;
    private readonly ITranslationLearningService _learning;
    private readonly TranslationCache _cache;
    private readonly TranslationProviderSelector _providerSelector;
    private readonly TranslationPostProcessor _postProcessor;
    private readonly RefinementOrchestrator _refinementOrchestrator;
    private readonly TranslationSettings _settings;
    private readonly PipelineDiagnostics _diagnostics;
    private readonly PipelineDiagnosticsStore _diagnosticsStore;
    private readonly ILogger<OrderedSubtitlePipeline> _logger;

    private readonly object _stabGate = new();
    private readonly object _dispatchGate = new();
    private readonly object _inFlightGate = new();
    private readonly object _statsGate = new();

    private readonly SemaphoreSlim _dispatchSignal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly Queue<CapturedSubtitleItem> _dispatchQueue = new();
    private readonly Dictionary<string, Task<TranslationResult>> _inFlight = new();

    private PendingCandidate? _pending;
    private long _pendingGeneration;

    // Rolling context for context-aware providers (DeepL): the last few
    // *captured* source lines, snapshotted per item at capture time so later
    // dispatch/cache lookups for that item always see the context as it was
    // when the line first appeared, not whatever has been captured since.
    private readonly object _historyGate = new();
    private readonly List<string> _recentSourceHistory = [];
    private readonly Dictionary<long, IReadOnlyList<string>> _contextByItemId = [];

    private long _opusDurationSampleCount;
    private double _opusDurationSum;
    private long _latencySampleCount;
    private double _latencySum;

    public OrderedSubtitlePipeline(
        OrderedSubtitleCaptureQueue captureQueue,
        SubtitleCandidateValidator validator,
        TranslationPlaybackQueue playbackQueue,
        ITranslationLearningService learning,
        TranslationCache cache,
        TranslationProviderSelector providerSelector,
        TranslationPostProcessor postProcessor,
        RefinementOrchestrator refinementOrchestrator,
        TranslationSettings settings,
        PipelineDiagnostics diagnostics,
        PipelineDiagnosticsStore diagnosticsStore,
        ILogger<OrderedSubtitlePipeline> logger)
    {
        _captureQueue = captureQueue;
        _validator = validator;
        _playbackQueue = playbackQueue;
        _learning = learning;
        _cache = cache;
        _providerSelector = providerSelector;
        _postProcessor = postProcessor;
        _refinementOrchestrator = refinementOrchestrator;
        _settings = settings;
        _diagnostics = diagnostics;
        _diagnosticsStore = diagnosticsStore;
        _logger = logger;
        _worker = Task.Run(TranslationWorkerLoopAsync);
    }

    /// <summary>Entry point for every non-empty OCR subtitle result.</summary>
    public void Submit(FormattedSubtitle subtitle, long frameNumber, SubtitleReplacementContext? replacementContext = null)
    {
        var rawText = !string.IsNullOrWhiteSpace(subtitle.MainText)
            ? subtitle.MainText.Trim()
            : subtitle.DisplayText.Trim();

        var validation = _validator.IsValidForReplacementMode(rawText);
        if (!validation.IsValid)
        {
            _diagnostics.LastRejectedSubtitleCandidate = rawText;
            _diagnostics.LastRejectedReason = validation.Reason;
            _diagnostics.RejectedBeforeQueueCount++;
            _diagnosticsStore.Save();
            _logger.LogDebug(
                "subtitle_candidate_rejected - reason={Reason}, text={Text}",
                validation.Reason, Truncate(rawText, 60));
            SaveCandidateValidationDiagnostic(rawText, validation, accepted: false);
            return;
        }

        _diagnostics.AcceptedSubtitleCandidateCount++;
        _diagnostics.LastAcceptedOcrCandidate = rawText;
        _playbackQueue.NotifyActivity(validation.NormalizedText, rawText, replacementContext);
        SaveCandidateValidationDiagnostic(rawText, validation, accepted: true);
        SaveSpeakerDetectionDiagnostic(subtitle, rawText, replacementContext);

        if (!_settings.MergeNearbySubtitleLines)
        {
            EnqueueForProcessing(rawText, validation.NormalizedText, subtitle.SpeakerName, frameNumber, replacementContext);
            return;
        }

        HandleStabilization(rawText, validation.NormalizedText, subtitle.SpeakerName, frameNumber, replacementContext);
    }

    public IReadOnlyList<CapturedSubtitleItem> GetCaptureSnapshot() => _captureQueue.GetSnapshot();
    public IReadOnlyList<TranslatedSubtitleDisplayItem> GetPlaybackSnapshot() => _playbackQueue.GetSnapshot();
    public int DispatchQueueCount { get { lock (_dispatchGate) return _dispatchQueue.Count; } }

    // ── Part H — stabilization / multi-line merge ────────────────────────────────

    private sealed class PendingCandidate
    {
        public string Text = string.Empty;
        public string NormalizedKey = string.Empty;
        public string SpeakerName = string.Empty;
        public long FrameNumber;
        public DateTimeOffset FirstArrivalAt;
        public SubtitleReplacementContext? ReplacementContext;
    }

    private void HandleStabilization(
        string text,
        string normalizedKey,
        string speakerName,
        long frameNumber,
        SubtitleReplacementContext? replacementContext)
    {
        long myGeneration;
        int delayMs;

        lock (_stabGate)
        {
            var isContinuation = _pending is not null &&
                (DateTimeOffset.Now - _pending.FirstArrivalAt).TotalMilliseconds < _settings.MaxSubtitleMergeWindowMs &&
                LooksLikeContinuation(_pending.NormalizedKey, normalizedKey);

            if (isContinuation)
            {
                var pending = _pending!;
                var grew = text.Length > pending.Text.Length;
                if (grew)
                {
                    pending.Text = text;
                    pending.NormalizedKey = normalizedKey;
                }
                if (replacementContext is not null)
                    pending.ReplacementContext = replacementContext.Clone();

                // Only restart the stabilization timer when the line actually
                // grew (the subtitle is still typing/scrolling in). Identical
                // re-reads of an already-complete line must NOT keep pushing the
                // dispatch back, otherwise a subtitle that lingers on screen is
                // re-read every OCR tick and never settles until the merge window
                // caps out seconds later. Letting the existing timer fire means
                // we translate the final, stable text once — not every animation
                // frame in between (which is what produced the jumbled/half-typed
                // garbage translations).
                if (!grew)
                    return;

                var elapsed = (DateTimeOffset.Now - pending.FirstArrivalAt).TotalMilliseconds;
                var remaining = _settings.MaxSubtitleMergeWindowMs - elapsed;
                delayMs = (int)Math.Max(0, Math.Min(_settings.SubtitleStabilizationMs, remaining));
            }
            else
            {
                // A genuinely different line arrived — flush whatever was pending
                // immediately so order is preserved, then start a new pending slot.
                if (_pending is not null)
                {
                    var toFlush = _pending;
                    Task.Run(() => EnqueueForProcessing(
                        toFlush.Text,
                        toFlush.NormalizedKey,
                        toFlush.SpeakerName,
                        toFlush.FrameNumber,
                        toFlush.ReplacementContext));
                }

                _pending = new PendingCandidate
                {
                    Text = text,
                    NormalizedKey = normalizedKey,
                    SpeakerName = speakerName,
                    FrameNumber = frameNumber,
                    FirstArrivalAt = DateTimeOffset.Now,
                    ReplacementContext = replacementContext?.Clone(),
                };
                delayMs = _settings.SubtitleStabilizationMs;
            }

            myGeneration = ++_pendingGeneration;
        }

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delayMs, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            PendingCandidate? toDispatch = null;
            lock (_stabGate)
            {
                if (_pendingGeneration == myGeneration && _pending is not null)
                {
                    toDispatch = _pending;
                    _pending = null;
                }
            }
            if (toDispatch is not null)
                EnqueueForProcessing(
                    toDispatch.Text,
                    toDispatch.NormalizedKey,
                    toDispatch.SpeakerName,
                    toDispatch.FrameNumber,
                    toDispatch.ReplacementContext);
        });
    }

    private static bool LooksLikeContinuation(string previous, string next) =>
        next.StartsWith(previous, StringComparison.Ordinal) ||
        previous.StartsWith(next, StringComparison.Ordinal) ||
        SubtitleTranslationQueue.Similarity(previous, next) >= 0.5;

    // ── Part C — capture queue + dedup ───────────────────────────────────────────

    private void EnqueueForProcessing(
        string text,
        string normalizedKey,
        string speakerName,
        long frameNumber,
        SubtitleReplacementContext? replacementContext)
    {
        var captureResult = _captureQueue.AddOrUpdate(
            text,
            normalizedKey,
            speakerName,
            frameNumber,
            replacementContext);
        if (!captureResult.IsNew)
        {
            _diagnostics.DuplicateSubtitleIgnoredCount++;
            _diagnostics.LastDedupReason = captureResult.Reason;
            _diagnosticsStore.Save();
            SaveTranslationDedupDiagnostic(normalizedKey, captureResult.Reason);
            return;
        }

        _diagnostics.LastCapturedSourceText = text;
        _diagnostics.CapturedQueueCount = _captureQueue.Count;
        _diagnosticsStore.Save();

        lock (_historyGate)
        {
            _contextByItemId[captureResult.Item.Id] =
                TranslationContextWindow.Build(_recentSourceHistory, text);
            _recentSourceHistory.Add(text);
            // A little slack beyond TranslationContextWindow.MaxLines (3) since
            // Build() walks back from the end and skips a line identical to the
            // current one — keeping a few extra avoids running out of distinct
            // lines to offer when consecutive captures repeat.
            while (_recentSourceHistory.Count > TranslationContextWindow.MaxLines + 3)
                _recentSourceHistory.RemoveAt(0);

            // Safety net: an item whose translation fails before reaching
            // PushReady (the normal cleanup point) would otherwise leak its
            // entry here forever. This is a rare path, so a coarse "just clear
            // it" cap is fine — the next few lines simply rebuild their context
            // from a clean slate instead of carrying stale entries indefinitely.
            if (_contextByItemId.Count > 200)
                _contextByItemId.Clear();
        }

        _ = ResolveTranslationAsync(captureResult.Item);
    }

    // ── Part D — memory/cache/in-flight lookup before OPUS-MT ────────────────────

    private async Task ResolveTranslationAsync(CapturedSubtitleItem item)
    {
        var gameName = string.IsNullOrWhiteSpace(_settings.GameProfile) ? "Default" : _settings.GameProfile;

        if (_settings.EnableTranslationMemory)
        {
            var memEntry = await _learning.LookupMemoryAsync(gameName, item.SourceText, item.SpeakerName)
                .ConfigureAwait(false);
            if (memEntry is not null)
            {
                _diagnostics.MemoryHitCount++;
                SaveTranslationDedupDiagnostic(item.NormalizedSourceKey, "memory_hit");
                item.Status = CapturedSubtitleStatus.MemoryHit;
                item.FromMemory = true;
                PushReady(item, memEntry.FinalTranslation, fromMemory: true, fromCache: false);
                _ = _learning.SaveMemoryHitRecordAsync(
                    item.SourceText, gameName, memEntry, _settings.SourceLanguage, _settings.TargetLanguage,
                    item.SpeakerName);
                return;
            }
        }

        if (_settings.UseTranslationCache)
        {
            var cacheRequest = BuildRequest(item);
            if (_cache.TryGet(cacheRequest, out var cached) && !string.IsNullOrWhiteSpace(cached))
            {
                _diagnostics.CacheHitCount++;
                SaveTranslationDedupDiagnostic(item.NormalizedSourceKey, "cache_hit");
                item.Status = CapturedSubtitleStatus.CacheHit;
                item.FromCache = true;
                PushReady(item, cached, fromMemory: false, fromCache: true);
                return;
            }
        }

        Task<TranslationResult>? existingTask;
        lock (_inFlightGate) { _inFlight.TryGetValue(BuildInFlightKey(item), out existingTask); }
        if (existingTask is not null)
        {
            _diagnostics.InFlightHitCount++;
            SaveTranslationDedupDiagnostic(item.NormalizedSourceKey, "in_flight_hit");
            item.Status = CapturedSubtitleStatus.Translating;
            _ = AttachToInFlightAsync(item, existingTask);
            return;
        }

        item.Status = CapturedSubtitleStatus.QueuedForTranslation;
        lock (_dispatchGate)
        {
            _dispatchQueue.Enqueue(item);
            TrimDispatchQueueUnsafe();
        }
        _dispatchSignal.Release();
    }

    private void TrimDispatchQueueUnsafe()
    {
        while (_dispatchQueue.Count > Math.Max(1, _settings.OrderedTranslationQueueMaxSize))
        {
            var list = _dispatchQueue.ToList();
            var expiredIndex = list.FindIndex(i => i.AgeMs > _settings.MaxAgeBeforeTranslationMs);
            if (expiredIndex < 0) break;

            list.RemoveAt(expiredIndex);
            _dispatchQueue.Clear();
            foreach (var i in list) _dispatchQueue.Enqueue(i);
            _diagnostics.ExpiredSkippedCount++;
        }
    }

    private async Task AttachToInFlightAsync(CapturedSubtitleItem item, Task<TranslationResult> existingTask)
    {
        try
        {
            var result = await existingTask.ConfigureAwait(false);
            if (!result.Success || string.IsNullOrWhiteSpace(result.TranslatedText)) return;

            var glossaryTranslation = _postProcessor.Process(item.SourceText, result.TranslatedText);
            item.Status = CapturedSubtitleStatus.Translated;
            PushReady(item, glossaryTranslation, fromMemory: false, fromCache: false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "ordered_pipeline_inflight_attach_failed");
        }
    }

    // ── Part E — ordered OPUS-MT translation worker ──────────────────────────────

    private async Task TranslationWorkerLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try { await _dispatchSignal.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            CapturedSubtitleItem? item;
            lock (_dispatchGate)
            {
                if (_dispatchQueue.Count == 0) continue;
                item = _dispatchQueue.Dequeue();
            }

            if (_settings.DropExpiredBeforeTranslation && item.AgeMs > _settings.MaxAgeBeforeTranslationMs)
            {
                _diagnostics.ExpiredSkippedCount++;
                item.Status = CapturedSubtitleStatus.Expired;
                continue;
            }

            // Never cancel an in-progress translation because a newer subtitle
            // arrived — OCR/capture keep running independently via the worker above.
            var inFlightKey = BuildInFlightKey(item);
            var translationTask = TranslateOneAsync(item, token);
            lock (_inFlightGate) { _inFlight[inFlightKey] = translationTask; }
            item.Status = CapturedSubtitleStatus.Translating;

            try { await translationTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "ordered_translation_worker_error");
            }
            finally
            {
                lock (_inFlightGate)
                {
                    if (_inFlight.TryGetValue(inFlightKey, out var current) && current == translationTask)
                        _inFlight.Remove(inFlightKey);
                }
            }
        }
    }

    private async Task<TranslationResult> TranslateOneAsync(CapturedSubtitleItem item, CancellationToken token)
    {
        // Part C guard: item.SourceText must be dialogue-only. If the detected
        // speaker still leads the translation input, something upstream regressed.
        if (!string.IsNullOrWhiteSpace(item.SpeakerName) &&
            item.SourceText.TrimStart().StartsWith(item.SpeakerName + " ", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "ERROR_SPEAKER_NAME_SENT_TO_TRANSLATION - speaker={Speaker}, input={Input}",
                item.SpeakerName, Truncate(item.SourceText, 80));
        }

        var request = BuildRequest(item);
        var selection = await _providerSelector
            .SelectProviderAsync(_settings.EnableTranslationProviderFallback, token)
            .ConfigureAwait(false);
        var providerName = selection.ActualProviderName;

        _diagnostics.TranslationStartedCount++;
        _diagnostics.LastTranslationStartedAt = DateTimeOffset.Now;
        _diagnostics.LastTranslationSourceText = item.SourceText;
        _diagnostics.LastTranslationProviderName = providerName;
        _diagnostics.ActualProviderUsed = providerName;
        _diagnostics.LastTranslationQueueStatus = "processing";
        _diagnostics.LastTranslationError = string.Empty;
        _diagnosticsStore.Save();
        _diagnostics.NotifyChanged();

        _diagnostics.ActualOpusCallCount++;
        SaveTranslationDedupDiagnostic(item.NormalizedSourceKey, "actual_provider_call");
        var stopwatch = Stopwatch.StartNew();
        var execution = await _providerSelector
            .TranslateAsync(request, allowFallback: true, token)
            .ConfigureAwait(false);
        var result = execution.Result;
        providerName = execution.Selection.ActualProviderName;
        stopwatch.Stop();
        RecordOpusDuration(stopwatch.ElapsedMilliseconds);
        _diagnostics.LastTranslationWasFallbackUsed = execution.Selection.FallbackUsed;
        _diagnostics.LastTranslationFallbackReason = execution.Selection.FallbackReason;
        _diagnostics.LastTranslationProviderName = providerName;
        _diagnostics.ActualProviderUsed = providerName;

        if (!result.Success || string.IsNullOrWhiteSpace(result.TranslatedText))
        {
            _diagnostics.TranslationFailedCount++;
            _diagnostics.LastTranslationFinishedAt = DateTimeOffset.Now;
            _diagnostics.LastTranslationDurationMs = result.DurationMs;
            _diagnostics.LastTranslationRawResponse = result.RawResponse;
            _diagnostics.LastTranslationParsedText = result.ParsedTranslation;
            _diagnostics.LastTranslationPostProcessedText = result.PostProcessedTranslation;
            _diagnostics.LastTranslationError = result.ErrorMessage ?? "Translation failed.";
            _diagnostics.LastTranslationWasFromCache = result.FromCache;
            _diagnostics.LastTranslationQueueStatus = "failed";
            _diagnostics.LastTranslationTime = DateTimeOffset.Now;
            _diagnosticsStore.Save();
            _diagnostics.NotifyChanged();
            SaveTranslationResultDiagnostic(result);
            return result; // Keep previous Turkish; never fall back to English (Part I).
        }

        var glossaryTranslation = _postProcessor.Process(item.SourceText, result.TranslatedText);

        if (_settings.UseTranslationCache)
            _cache.Store(request, glossaryTranslation);

        item.Status = CapturedSubtitleStatus.Translated;
        _diagnostics.LastTranslatedTurkishText = glossaryTranslation;
        _diagnostics.TranslationCompletedCount++;
        _diagnostics.LastTranslationFinishedAt = DateTimeOffset.Now;
        _diagnostics.LastTranslationDurationMs = result.DurationMs;
        _diagnostics.LastTranslationRawResponse = result.RawResponse;
        _diagnostics.LastTranslationParsedText = string.IsNullOrWhiteSpace(result.ParsedTranslation)
            ? result.TranslatedText
            : result.ParsedTranslation;
        _diagnostics.LastTranslationPostProcessedText = glossaryTranslation;
        _diagnostics.LastTranslationError = string.Empty;
        _diagnostics.LastTranslationProviderName = providerName;
        _diagnostics.ActualProviderUsed = providerName;
        _diagnostics.LastTranslationWasFromCache = result.FromCache;
        _diagnostics.LastTranslationQueueStatus = "completed";
        _diagnostics.LastTranslationTime = DateTimeOffset.Now;
        _diagnosticsStore.Save();
        _diagnostics.NotifyChanged();

        PushReady(item, glossaryTranslation, fromMemory: false, fromCache: false);
        SaveTranslationResultDiagnostic(result);

        _logger.LogInformation(
            "stage_latency - stage=translate provider={Provider} translate_ms={TranslateMs} " +
            "postprocess_incl_ms={TotalMs} queue_age_ms={QueueAgeMs:F0} text_len={TextLen}",
            providerName, result.DurationMs, stopwatch.ElapsedMilliseconds,
            item.AgeMs, item.SourceText.Length);

        // Hybrid / refinement: optional background Ollama post-edit of the machine
        // translation. Never blocks playback; overlay replacement is guarded inside
        // the orchestrator (source-key match + replacement-mode English guards).
        _refinementOrchestrator.TriggerBackgroundIfEnabled(
            item.SourceText, glossaryTranslation, item.NormalizedSourceKey,
            string.IsNullOrWhiteSpace(_settings.GameProfile) ? "Default" : _settings.GameProfile);

        if (_settings.EnableLearningRecords && IsValidForLearning(item.SourceText, glossaryTranslation))
        {
            var recordId = await _learning.SaveAutoRecordAsync(
                    item.SourceText, string.IsNullOrWhiteSpace(_settings.GameProfile) ? "Default" : _settings.GameProfile,
                    result.TranslatedText, glossaryTranslation,
                    providerName, stopwatch.ElapsedMilliseconds,
                    _settings.SourceLanguage, _settings.TargetLanguage,
                    item.SpeakerName).ConfigureAwait(false);
            item.TranslationRecordId = recordId > 0 ? recordId : null;
        }

        return result;
    }

    private void SaveTranslationResultDiagnostic(TranslationResult result)
    {
        try
        {
            var debugDirectory = Path.Combine(AppContext.BaseDirectory, "debug");
            Directory.CreateDirectory(debugDirectory);
            var json = System.Text.Json.JsonSerializer.Serialize(
                result,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            DebugFileWriter.QueueText(
                Path.Combine(debugDirectory, "last_translation_result.json"),
                json,
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save live translation result diagnostics");
        }
    }

    private bool IsValidForLearning(string sourceText, string finalTranslation)
    {
        if (string.IsNullOrWhiteSpace(finalTranslation)) return false;
        if (string.Equals(sourceText.Trim(), finalTranslation.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        return _validator.IsValidForReplacementMode(sourceText).IsValid;
    }

    private void SaveCandidateValidationDiagnostic(
        string rawText,
        SubtitleCandidateValidationResult validation,
        bool accepted)
    {
        try
        {
            var debugDirectory = Path.Combine(AppContext.BaseDirectory, "debug");
            Directory.CreateDirectory(debugDirectory);
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                RawOcrLines = rawText.Split('\n', StringSplitOptions.TrimEntries),
                AcceptedLines = accepted ? rawText.Split('\n', StringSplitOptions.TrimEntries) : [],
                RejectedLines = accepted ? [] : rawText.Split('\n', StringSplitOptions.TrimEntries),
                RejectionReasons = accepted ? Array.Empty<string>() : new[] { validation.Reason },
                SelectedSubtitleCandidate = accepted ? rawText : string.Empty,
                validation.NormalizedText,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            DebugFileWriter.QueueText(
                Path.Combine(debugDirectory, "last_subtitle_candidate_validation.json"),
                json,
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save subtitle candidate validation diagnostics");
        }
    }

    private void SaveSpeakerDetectionDiagnostic(
        FormattedSubtitle subtitle,
        string translationInput,
        SubtitleReplacementContext? replacementContext)
    {
        try
        {
            var speaker = subtitle.SpeakerName ?? string.Empty;
            var speakerLeaked = speaker.Length > 0 &&
                translationInput.TrimStart().StartsWith(speaker + " ", StringComparison.OrdinalIgnoreCase);
            if (speakerLeaked)
            {
                _logger.LogError(
                    "ERROR_SPEAKER_NAME_SENT_TO_TRANSLATION - speaker={Speaker}, input={Input}",
                    speaker, Truncate(translationInput, 80));
            }

            var debugDirectory = Path.Combine(AppContext.BaseDirectory, "debug");
            Directory.CreateDirectory(debugDirectory);
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                RawOcrText = subtitle.RawText,
                RawOcrLines = subtitle.RawText.Split('\n', StringSplitOptions.TrimEntries),
                LineBoundingBoxes = replacementContext?.OcrLineBoxes,
                DetectedSpeakerName = speaker.Length > 0 ? speaker : null,
                DialogueText = subtitle.MainText,
                TranslationInputText = translationInput,
                OverlaySpeakerDisplayText = speaker.Length > 0 ? speaker : null,
                WasSpeakerIncludedInTranslationInput = speakerLeaked,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            DebugFileWriter.QueueText(
                Path.Combine(debugDirectory, "last_speaker_detection.json"),
                json,
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save speaker detection diagnostics");
        }
    }

    private void SaveTranslationDedupDiagnostic(string sourceKey, string reason)
    {
        try
        {
            var debugDirectory = Path.Combine(AppContext.BaseDirectory, "debug");
            Directory.CreateDirectory(debugDirectory);
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                SourceKey = sourceKey,
                Reason = reason,
                MemoryHits = _diagnostics.MemoryHitCount,
                CacheHits = _diagnostics.CacheHitCount,
                InFlightHits = _diagnostics.InFlightHitCount,
                ActualOpusCalls = _diagnostics.ActualOpusCallCount,
                DuplicateIgnoredCount = _diagnostics.DuplicateSubtitleIgnoredCount,
                InFlightKeys = GetInFlightKeys(),
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            DebugFileWriter.QueueText(
                Path.Combine(debugDirectory, "last_translation_dedup_state.json"),
                json,
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save translation dedup diagnostics");
        }
    }

    private string[] GetInFlightKeys()
    {
        lock (_inFlightGate)
            return _inFlight.Keys.ToArray();
    }

    /// <summary>Part H: the same dialogue must not be translated twice while a
    /// request is in flight for the same game/provider/target-language.</summary>
    private string BuildInFlightKey(CapturedSubtitleItem item) =>
        $"{(string.IsNullOrWhiteSpace(_settings.GameProfile) ? "Default" : _settings.GameProfile)}|" +
        $"{_settings.ProviderType}|{_settings.TargetLanguage}|{item.NormalizedSourceKey}";

    // ── Part F — push to playback queue ──────────────────────────────────────────

    private void PushReady(CapturedSubtitleItem item, string translatedText, bool fromMemory, bool fromCache)
    {
        // This item's context snapshot (see EnqueueForProcessing) has done its
        // job for any cache/dispatch lookups on this item; drop it so
        // _contextByItemId doesn't grow unbounded over a long session.
        lock (_historyGate) _contextByItemId.Remove(item.Id);

        var displayItem = new TranslatedSubtitleDisplayItem
        {
            SourceText = item.SourceText,
            TranslatedText = translatedText,
            NormalizedSourceKey = item.NormalizedSourceKey,
            SpeakerName = item.SpeakerName,
            CreatedAt = item.FirstSeenAt,
            ReadyAt = DateTimeOffset.Now,
            MinDisplayMs = _settings.MinTurkishDisplayMs,
            MaxDisplayMs = _settings.MaxTurkishDisplayMs,
            DisplayDurationMs = EstimateReadableDisplayMs(translatedText),
            FromMemory = fromMemory,
            FromCache = fromCache,
            TranslationRecordId = item.TranslationRecordId,
            ReplacementContext = item.ReplacementContext?.Clone(),
            Source = fromMemory ? "MEMORY_HIT" : fromCache ? "CACHE_HIT" : "PLAYBACK_QUEUE",
        };

        RecordCaptureToDisplayLatency((displayItem.ReadyAt - displayItem.CreatedAt).TotalMilliseconds);
        _playbackQueue.Enqueue(displayItem);
        SaveTranslationToDisplayRouting(item, displayItem);
        SaveReplacementPipelineState(item, displayItem);
    }

    private void SaveTranslationToDisplayRouting(
        CapturedSubtitleItem item,
        TranslatedSubtitleDisplayItem displayItem)
    {
        try
        {
            var debugDirectory = Path.Combine(AppContext.BaseDirectory, "debug");
            Directory.CreateDirectory(debugDirectory);
            var regionValid = displayItem.ReplacementContext?.OverlayRect is { Width: > 100, Height: > 30 };
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                FinalTranslation = displayItem.TranslatedText,
                DisplayMode = "SubtitleReplacementOverlay",
                ReplacementRegionValid = regionValid,
                OverlayUpdateCalled = true,
                OverlayVisible = true,
                FailureReason = regionValid ? string.Empty : "ManualReplacementRegion is invalid.",
                SourceText = item.SourceText,
                displayItem.Source,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            DebugFileWriter.QueueText(
                Path.Combine(debugDirectory, "last_translation_to_display_routing.json"),
                json,
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save translation to display routing diagnostics");
        }
    }

    private void SaveReplacementPipelineState(
        CapturedSubtitleItem item, TranslatedSubtitleDisplayItem displayItem)
    {
        try
        {
            var speaker = item.SpeakerName ?? string.Empty;
            var speakerLeaked = speaker.Length > 0 &&
                item.SourceText.TrimStart().StartsWith(speaker + " ", StringComparison.OrdinalIgnoreCase);

            var debugDirectory = Path.Combine(AppContext.BaseDirectory, "debug");
            Directory.CreateDirectory(debugDirectory);
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                AcceptedSubtitleCandidate = item.SourceText,
                DetectedSpeakerName = speaker.Length > 0 ? speaker : null,
                DialogueTextSentToTranslation = item.SourceText,
                WasSpeakerSentToTranslation = speakerLeaked,
                ReplacementRegionUsed = item.ReplacementContext?.OverlayRect,
                UsedManualRegion = item.ReplacementContext is not null &&
                    !item.ReplacementContext.UsedFallbackRegion,
                MemoryHit = displayItem.FromMemory,
                CacheHit = displayItem.FromCache,
                FinalTurkish = displayItem.TranslatedText,
                WasEnglishDisplayed = false, // playback queue guard blocks English before display
                DisplayDurationMs = displayItem.DisplayDurationMs,
                TranslationLatencyMs = (long)(displayItem.ReadyAt - displayItem.CreatedAt).TotalMilliseconds,
                Counters = new
                {
                    _diagnostics.MemoryHitCount,
                    _diagnostics.CacheHitCount,
                    _diagnostics.InFlightHitCount,
                    _diagnostics.ActualOpusCallCount,
                    _diagnostics.DuplicateSubtitleIgnoredCount,
                    _diagnostics.RejectedBeforeQueueCount,
                    _diagnostics.ExpiredSkippedCount,
                    _diagnostics.AverageOpusDurationMs,
                },
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            DebugFileWriter.QueueText(
                Path.Combine(debugDirectory, "last_replacement_pipeline_state.json"),
                json,
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save replacement pipeline state diagnostics");
        }
    }

    private int EstimateReadableDisplayMs(string translatedText)
    {
        if (!_settings.EnableReadableSubtitleTiming)
            return _settings.MinTurkishDisplayMs;

        var clean = translatedText?.Trim() ?? string.Empty;
        if (clean.Length == 0)
            return _settings.MinTurkishDisplayMs;

        var charCount = clean.Count(c => !char.IsWhiteSpace(c));
        var estimatedLineCount = EstimateLineCount(clean, 42);
        var extraLineCount = Math.Max(0, estimatedLineCount - 1);
        var total = _settings.MinTurkishDisplayMs +
            (charCount * _settings.MsPerCharacter) +
            (extraLineCount * _settings.ExtraLineMs);

        return (int)Math.Clamp(total, _settings.MinTurkishDisplayMs, _settings.MaxTurkishDisplayMs);
    }

    private static int EstimateLineCount(string text, int maxCharsPerLine)
    {
        if (string.IsNullOrWhiteSpace(text)) return 1;
        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        if (lines.Length == 0) return 1;

        var count = 0;
        foreach (var line in lines)
            count += Math.Max(1, (int)Math.Ceiling(line.Length / (double)Math.Max(1, maxCharsPerLine)));

        return Math.Max(1, count);
    }

    // ── Diagnostics helpers ───────────────────────────────────────────────────────

    private void RecordOpusDuration(long ms)
    {
        lock (_statsGate)
        {
            _opusDurationSampleCount++;
            _opusDurationSum += ms;
            _diagnostics.AverageOpusDurationMs = _opusDurationSum / _opusDurationSampleCount;
        }
    }

    private void RecordCaptureToDisplayLatency(double ms)
    {
        lock (_statsGate)
        {
            _latencySampleCount++;
            _latencySum += ms;
            _diagnostics.AverageCaptureToDisplayLatencyMs = _latencySum / _latencySampleCount;
        }
    }

    private TranslationRequest BuildRequest(CapturedSubtitleItem item)
    {
        IReadOnlyList<string> context;
        lock (_historyGate)
            context = _contextByItemId.TryGetValue(item.Id, out var ctx) ? ctx : [];

        return new TranslationRequest
        {
            SourceText = item.SourceText,
            SourceLanguage = _settings.SourceLanguage,
            TargetLanguage = _settings.TargetLanguage,
            GameProfileName = _settings.GameProfile,
            // Metadata only: providers translate SourceText; the cache key may
            // include the speaker when IncludeSpeakerInMemoryKey is enabled (Part D).
            SpeakerName = string.IsNullOrWhiteSpace(item.SpeakerName) ? null : item.SpeakerName,
            // Preceding dialogue lines for context-aware providers (DeepL) — the
            // snapshot taken when this line was captured (see EnqueueForProcessing).
            PreviousContextLines = context,
        };
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    public void Dispose()
    {
        _cts.Cancel();
        try { _worker.Wait(2000); } catch { /* shutting down */ }
        _cts.Dispose();
        _dispatchSignal.Dispose();
    }
}
