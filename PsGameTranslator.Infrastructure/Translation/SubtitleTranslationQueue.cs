using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

public sealed class SubtitleQueueItem
{
    public string SourceText { get; init; } = string.Empty;
    public string DisplayText { get; init; } = string.Empty;
    public string SpeakerName { get; init; } = string.Empty;
    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<string> PreviousContextLines { get; init; } = Array.Empty<string>();
    public string ContextScopeKey { get; init; } = string.Empty;
    public string SourceLanguage { get; init; } = string.Empty;
    public string TargetLanguage { get; init; } = string.Empty;
    public string GameProfileName { get; init; } = string.Empty;

    public long AgeMs => (long)(DateTimeOffset.Now - EnqueuedAt).TotalMilliseconds;
}

/// <summary>
/// Ordered FIFO translation queue for fast dialogue. Unlike the single-slot
/// TranslationQueue, this keeps a small queue (default 5) so quick back-and-forth
/// subtitle lines are not lost. Repeated frames of the same subtitle are
/// deduplicated by fuzzy similarity; expired untranslated items are dropped.
/// In-flight translations are never cancelled; results are always cached.
/// </summary>
public sealed class SubtitleTranslationQueue : IDisposable
{
    private static readonly string StateFilePath = Path.Combine(
        AppContext.BaseDirectory, "debug", "last_subtitle_queue_state.json");

    private readonly TranslationProviderSelector _providerSelector;
    private readonly TranslationSettings _settings;
    private readonly TranslationCache _cache;
    private readonly PipelineDiagnostics _diagnostics;
    private readonly PipelineDiagnosticsStore _diagnosticsStore;
    private readonly ITranslationLearningService _learning;
    private readonly ILogger<SubtitleTranslationQueue> _logger;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly List<SubtitleQueueItem> _items = [];
    private readonly List<string> _contextHistory = [];
    private string _contextScopeKey = string.Empty;
    private string _activeSource = string.Empty;
    private string _lastCompletedSource = string.Empty;

    /// <summary>Raised on a background thread when a translation attempt finishes.</summary>
    public event Action<TranslationResult, SubtitleQueueItem>? Completed;

    public SubtitleTranslationQueue(
        TranslationProviderSelector providerSelector,
        TranslationSettings settings,
        TranslationCache cache,
        PipelineDiagnostics diagnostics,
        PipelineDiagnosticsStore diagnosticsStore,
        ITranslationLearningService learning,
        ILogger<SubtitleTranslationQueue> logger)
    {
        _providerSelector = providerSelector;
        _settings = settings;
        _cache = cache;
        _diagnostics = diagnostics;
        _diagnosticsStore = diagnosticsStore;
        _learning = learning;
        _logger = logger;
        _worker = Task.Run(WorkerLoopAsync);
    }

    // ── Enqueue ──────────────────────────────────────────────────────────────────

    public bool Enqueue(FormattedSubtitle subtitle)
    {
        var source = subtitle.MainText.Trim();
        if (source.Length == 0) return false;

        lock (_gate)
        {
            EnsureContextScopeLocked();
            // Deduplicate repeated OCR frames of the same subtitle.
            if (IsDuplicate(source))
            {
                _logger.LogDebug("subtitle_not_enqueued_duplicate - {Text}", Truncate(source, 60));
                return false;
            }

            // Make room: expired items first, then the oldest.
            if (_items.Count >= Math.Max(1, _settings.MaxTranslationQueueSize))
            {
                if (_settings.DropExpiredUntranslatedSubtitles)
                {
                    var removed = _items.RemoveAll(i => i.AgeMs > _settings.MaxSubtitleAgeMs);
                    if (removed > 0)
                    {
                        _diagnostics.TranslationExpiredCount += removed;
                        _diagnostics.LastQueueDropReason = $"expired_dropped ({removed})";
                        _logger.LogInformation("subtitle_queue_expired_dropped - count={Count}", removed);
                    }
                }

                if (_items.Count >= Math.Max(1, _settings.MaxTranslationQueueSize))
                {
                    var oldest = _items[0];
                    _items.RemoveAt(0);
                    _diagnostics.TranslationDroppedCount++;
                    _diagnostics.LastQueueDropReason = "queue_full_dropped_oldest";
                    _logger.LogWarning(
                        "subtitle_queue_full_dropped_oldest - {Text}", Truncate(oldest.SourceText, 60));
                }
            }

            _items.Add(new SubtitleQueueItem
            {
                SourceText = source,
                DisplayText = subtitle.DisplayText,
                SpeakerName = subtitle.SpeakerName,
                PreviousContextLines = TranslationContextWindow.Build(_contextHistory, source),
                ContextScopeKey = _contextScopeKey,
                SourceLanguage = _settings.SourceLanguage,
                TargetLanguage = _settings.TargetLanguage,
                GameProfileName = _settings.GameProfile,
            });
            _contextHistory.Add(source);
            if (_contextHistory.Count > TranslationContextWindow.MaxLines) _contextHistory.RemoveAt(0);
        }

        _signal.Release();
        _diagnostics.TranslationEnqueueCount++;
        _logger.LogInformation("subtitle_enqueued - {Text}", Truncate(source, 80));
        PublishQueueDiagnostics();
        return true;
    }

    private bool IsDuplicate(string source)
    {
        var threshold = Math.Clamp(_settings.SubtitleDedupSimilarityThreshold, 0.5, 1.0);

        if (Similarity(source, _activeSource) >= threshold) return true;
        if (Similarity(source, _lastCompletedSource) >= threshold) return true;
        return _items.Any(item => Similarity(source, item.SourceText) >= threshold);
    }

    // ── Worker ───────────────────────────────────────────────────────────────────

    private async Task WorkerLoopAsync()
    {
        var cancellationToken = _cts.Token;

        while (!cancellationToken.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            SubtitleQueueItem? item;
            lock (_gate)
            {
                if (_items.Count == 0) continue;
                item = _items[0];
                _items.RemoveAt(0);

                // Sequential processing preserves order for short dialogue bursts.
                // Expired items are skipped so the queue catches up to live dialogue.
                if (_settings.DropExpiredUntranslatedSubtitles &&
                    item.AgeMs > _settings.MaxSubtitleAgeMs)
                {
                    _diagnostics.TranslationExpiredCount++;
                    _diagnostics.LastQueueDropReason = "expired_before_processing";
                    _logger.LogInformation(
                        "subtitle_expired_before_processing - ageMs={AgeMs}, text={Text}",
                        item.AgeMs, Truncate(item.SourceText, 60));
                    PublishQueueDiagnostics();
                    continue;
                }

                _activeSource = item.SourceText;
            }

            _diagnostics.TranslationStartedCount++;
            _diagnostics.LastTranslationStartedAt = DateTimeOffset.Now;
            _diagnostics.LastTranslationSourceText = item.SourceText;
            _diagnostics.LastTranslationQueueStatus = "processing";
            PublishQueueDiagnostics();

            var result = await TranslateItemAsync(item, cancellationToken).ConfigureAwait(false);
            if (result is null) break; // shutdown

            lock (_gate)
            {
                _activeSource = string.Empty;
                if (result.Success && string.Equals(item.ContextScopeKey, _contextScopeKey, StringComparison.Ordinal))
                    _lastCompletedSource = item.SourceText;
            }

            _diagnostics.LastSubtitleAgeMsWhenTranslationCompleted = item.AgeMs;
            _diagnostics.LastTranslationFinishedAt = DateTimeOffset.Now;
            _diagnostics.LastTranslationTime = DateTimeOffset.Now;
            _diagnostics.LastTranslationDurationMs = result.DurationMs;
            _diagnostics.LastTranslationRawResponse = result.RawOutput ?? string.Empty;
            _diagnostics.LastTranslationParsedText = result.TranslatedText;
            _diagnostics.LastTranslationPostProcessedText = result.TranslatedText;
            _diagnostics.LastTranslationProviderName = result.ProviderName;
            _diagnostics.LastTranslationError = result.ErrorMessage ?? string.Empty;
            _diagnostics.LastTranslationWasFromCache = result.FromCache;
            _diagnostics.LastTranslationQueueStatus = result.Success ? "completed" : "failed";
            if (result.Success) _diagnostics.TranslationCompletedCount++;
            else _diagnostics.TranslationFailedCount++;
            PublishQueueDiagnostics();

            try { Completed?.Invoke(result, item); }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Subtitle translation Completed handler failed");
            }
        }
    }

    private async Task<TranslationResult?> TranslateItemAsync(
        SubtitleQueueItem item, CancellationToken cancellationToken)
    {
        var request = new TranslationRequest
        {
            SourceText = item.SourceText,
            SourceLanguage = item.SourceLanguage,
            TargetLanguage = item.TargetLanguage,
            SpeakerName = string.IsNullOrWhiteSpace(item.SpeakerName) ? null : item.SpeakerName,
            GameProfileName = item.GameProfileName,
            PreviousContextLines = item.PreviousContextLines,
        };

        if (_settings.CacheEnabled && _cache.TryGet(request, out var cached))
        {
            _diagnostics.TranslationCacheHitCount++;
            _logger.LogInformation("subtitle_translation_cache_hit - {Text}", Truncate(item.SourceText, 60));
            return new TranslationResult
            {
                SourceText = item.SourceText,
                TranslatedText = cached,
                SourceLanguage = request.SourceLanguage,
                TargetLanguage = request.TargetLanguage,
                Provider = "cache",
                FromCache = true,
                Success = true,
                ParsedTranslation = cached,
                PostProcessedTranslation = cached,
                JsonParseSucceeded = true,
            };
        }

        // Translation memory is currently keyed only by source text. Do not let
        // a context-free memory entry override a context-aware provider request:
        // the same line can legitimately translate differently in another scene.
        var gameName = _settings.GameProfile ?? "Default";
        var hasContext = request.PreviousContextLines.Any(line => !string.IsNullOrWhiteSpace(line));
        var memEntry = hasContext
            ? null
            : await _learning.LookupMemoryAsync(gameName, item.SourceText).ConfigureAwait(false);
        if (memEntry is not null)
        {
            _logger.LogInformation(
                "translation_memory_hit - {Text} → {Translation}",
                Truncate(item.SourceText, 60), Truncate(memEntry.FinalTranslation, 60));
            _ = _learning.SaveMemoryHitRecordAsync(
                item.SourceText, gameName, memEntry,
                request.SourceLanguage, request.TargetLanguage);
            return new TranslationResult
            {
                SourceText = item.SourceText,
                TranslatedText = memEntry.FinalTranslation,
                SourceLanguage = request.SourceLanguage,
                TargetLanguage = request.TargetLanguage,
                Provider = "TranslationMemory",
                FromCache = false,
                Success = true,
                ParsedTranslation = memEntry.FinalTranslation,
                PostProcessedTranslation = memEntry.FinalTranslation,
                JsonParseSucceeded = true,
            };
        }

        var provider = _providerSelector.SelectProvider();
        if (provider is null)
        {
            return new TranslationResult
            {
                SourceText = item.SourceText,
                Success = false,
                ErrorMessage = "No translation provider selected.",
            };
        }

        _diagnostics.ActualProviderUsed = provider.ProviderName;

        try
        {
            // Never cancelled mid-flight because of newer OCR text — only app shutdown.
            var execution = await _providerSelector
                .TranslateAsync(request, allowFallback: true, cancellationToken)
                .ConfigureAwait(false);
            var result = execution.Result;
            _diagnostics.ActualProviderUsed = execution.Selection.ActualProviderName;
            _diagnostics.LastTranslationProviderName = execution.Selection.ActualProviderName;
            _diagnostics.LastTranslationWasFallbackUsed = execution.Selection.FallbackUsed;
            _diagnostics.LastTranslationFallbackReason = execution.Selection.FallbackReason;

            if (result.Success && _settings.CacheEnabled)
            {
                _cache.Store(request, result.TranslatedText);
                _diagnostics.TranslationCacheSavedCount++;
            }

            if (result.Success)
            {
                _ = _learning.SaveAutoRecordAsync(
                    item.SourceText, gameName,
                    result.ParsedTranslation, result.PostProcessedTranslation,
                    result.ProviderName, result.DurationMs,
                    request.SourceLanguage, request.TargetLanguage);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Subtitle translation worker error");
            return new TranslationResult
            {
                SourceText = item.SourceText,
                Success = false,
                ErrorMessage = $"Translation error: {exception.Message}",
            };
        }
    }

    // ── Similarity (normalized Levenshtein ratio) ────────────────────────────────

    internal static double Similarity(string first, string second)
    {
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second)) return 0;

        var a = Normalize(first);
        var b = Normalize(second);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;

        var distance = Levenshtein(a, b);
        return 1.0 - (double)distance / Math.Max(a.Length, b.Length);
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.ToLowerInvariant()
            .Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────────

    private void PublishQueueDiagnostics()
    {
        string itemsPreview;
        int count;
        lock (_gate)
        {
            count = _items.Count;
            itemsPreview = string.Join(" | ",
                _items.Select(i => $"\"{Truncate(i.SourceText, 40)}\" ({i.AgeMs} ms)"));
        }

        _diagnostics.TranslationQueueCount = count;
        _diagnostics.TranslationQueueItems = itemsPreview;
        _diagnostics.NotifyChanged();
        _diagnosticsStore.Save();
        SaveStateSnapshot(count, itemsPreview);
    }

    private void SaveStateSnapshot(int count, string itemsPreview)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
            var snapshot = new
            {
                Timestamp = DateTimeOffset.Now,
                QueueCount = count,
                QueueItems = itemsPreview,
                ActiveSource = _activeSource,
                LastCompletedSource = _lastCompletedSource,
                _diagnostics.TranslationEnqueueCount,
                _diagnostics.TranslationStartedCount,
                _diagnostics.TranslationCompletedCount,
                _diagnostics.TranslationFailedCount,
                _diagnostics.TranslationLateCompletedCount,
                _diagnostics.TranslationExpiredCount,
                _diagnostics.TranslationCacheSavedCount,
                _diagnostics.TranslationCacheHitCount,
                _diagnostics.LastQueueDropReason,
                _diagnostics.LastSubtitleAgeMsWhenTranslationCompleted,
                _diagnostics.LastOverlayReplaceReason,
                Settings = new
                {
                    _settings.MaxTranslationQueueSize,
                    _settings.SubtitleDedupSimilarityThreshold,
                    _settings.MinSubtitleDisplayMs,
                    _settings.MaxSubtitleAgeMs,
                    _settings.DropExpiredUntranslatedSubtitles,
                    _settings.PrioritizeCurrentSubtitle,
                    _settings.ShowSourceWhileTranslating,
                },
            };
            DebugFileWriter.QueueText(
                StateFilePath,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save subtitle queue state diagnostics");
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    public void ResetContext()
    {
        lock (_gate) ResetContextLocked(BuildContextScopeKey());
    }

    private void EnsureContextScopeLocked()
    {
        var currentScope = BuildContextScopeKey();
        if (string.Equals(currentScope, _contextScopeKey, StringComparison.Ordinal)) return;
        ResetContextLocked(currentScope);
    }

    private void ResetContextLocked(string scopeKey)
    {
        _contextHistory.Clear();
        _items.Clear();
        _activeSource = string.Empty;
        _lastCompletedSource = string.Empty;
        _contextScopeKey = scopeKey;
    }

    private string BuildContextScopeKey() =>
        $"{_settings.GameProfile}|{_settings.SourceLanguage}|{_settings.TargetLanguage}";

    public void Dispose()
    {
        _cts.Cancel();
        try { _worker.Wait(2000); } catch { /* shutting down */ }
        _cts.Dispose();
        _signal.Dispose();
    }
}
