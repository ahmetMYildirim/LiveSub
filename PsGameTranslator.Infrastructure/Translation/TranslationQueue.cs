using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

public sealed class TranslationQueue : IDisposable
{
    private readonly ITranslationService _service;
    private readonly TranslationSettings _settings;
    private readonly TranslationCache _cache;
    private readonly PipelineDiagnostics _diagnostics;
    private readonly PipelineDiagnosticsStore _diagnosticsStore;
    private readonly ILogger<TranslationQueue> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private readonly Task _worker;
    private TranslationRequest? _pending;
    private string _activeText = string.Empty;
    private string _lastAcceptedText = string.Empty;

    public event Action<TranslationResult>? TranslationCompleted;
    public event Action? DiagnosticsChanged;

    public TranslationQueue(
        ITranslationService service,
        TranslationSettings settings,
        TranslationCache cache,
        PipelineDiagnostics diagnostics,
        PipelineDiagnosticsStore diagnosticsStore,
        ILogger<TranslationQueue> logger)
    {
        _service = service;
        _settings = settings;
        _cache = cache;
        _diagnostics = diagnostics;
        _diagnosticsStore = diagnosticsStore;
        _logger = logger;
        _worker = Task.Run(WorkerLoopAsync);
    }

    public bool Enqueue(string cleanedText)
    {
        if (string.IsNullOrWhiteSpace(cleanedText))
            return false;

        cleanedText = cleanedText.Trim();
        lock (_gate)
        {
            if (IsFuzzyDuplicate(cleanedText, _pending?.SourceText) ||
                IsFuzzyDuplicate(cleanedText, _activeText) ||
                IsFuzzyDuplicate(cleanedText, _lastAcceptedText))
            {
                _logger.LogDebug("Translation not enqueued - fuzzy duplicate: {Text}", Truncate(cleanedText, 80));
                return false;
            }

            var request = new TranslationRequest
            {
                SourceText = cleanedText,
                SourceLanguage = _settings.SourceLanguage,
                TargetLanguage = _settings.TargetLanguage,
                GameProfile = _settings.GameProfile,
            };

            var replaced = _pending is not null;
            _pending = request;
            _lastAcceptedText = cleanedText;
            if (!replaced)
                _signal.Release();
            else
                _logger.LogInformation("Pending translation replaced by newer subtitle");
        }

        _diagnostics.LastTranslationEnqueuedAt = DateTimeOffset.Now;
        _diagnostics.LastTranslationSourceText = cleanedText;
        _diagnostics.LastTranslationQueueStatus = "enqueued";
        _diagnostics.LastTranslationWasDroppedAsStale = false;
        _diagnostics.LastTranslationDropReason = string.Empty;
        _diagnostics.TranslationEnqueueCount++;
        _logger.LogInformation("translation_enqueued - {Text}", Truncate(cleanedText, 80));
        PublishDiagnostics();
        return true;
    }

    public void MarkDroppedAsStale(string reason)
    {
        _diagnostics.LastTranslationQueueStatus = "dropped_stale";
        _diagnostics.LastTranslationWasDroppedAsStale = true;
        _diagnostics.LastTranslationDropReason = reason;
        _diagnostics.TranslationDroppedCount++;
        PublishDiagnostics();
    }

    private async Task WorkerLoopAsync()
    {
        var cancellationToken = _cts.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            TranslationRequest? request;
            lock (_gate)
            {
                request = _pending;
                _pending = null;
                _activeText = request?.SourceText ?? string.Empty;
            }
            if (request is null) continue;

            _diagnostics.LastTranslationStartedAt = DateTimeOffset.Now;
            _diagnostics.LastTranslationQueueStatus = "processing";
            _diagnostics.LastTranslationSourceText = request.SourceText;
            _diagnostics.TranslationStartedCount++;
            PublishDiagnostics();

            TranslationResult result;
            if (_settings.CacheEnabled && _cache.TryGet(request, out var cached))
            {
                result = new TranslationResult
                {
                    SourceText = request.SourceText,
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
                _diagnostics.TranslationCacheHitCount++;
                _diagnostics.LastTranslationQueueStatus = "cache_hit";
                _logger.LogInformation("Translation cache hit - {Text}", Truncate(request.SourceText, 80));
            }
            else
            {
                try
                {
                    result = await _service.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Translation worker error");
                    result = new TranslationResult
                    {
                        SourceText = request.SourceText,
                        Success = false,
                        ErrorMessage = $"Translation error: {exception.Message}",
                    };
                }

                if (result.Success && _settings.CacheEnabled)
                    _cache.Store(request, result.TranslatedText);
            }

            lock (_gate) _activeText = string.Empty;
            if (!result.Success)
            {
                lock (_gate)
                {
                    if (string.Equals(_lastAcceptedText, request.SourceText, StringComparison.Ordinal))
                        _lastAcceptedText = string.Empty;
                }
                _logger.LogInformation("Failed translation released for retry - {Text}",
                    Truncate(request.SourceText, 80));
            }
            ApplyResultDiagnostics(result);
            try { TranslationCompleted?.Invoke(result); }
            catch (Exception exception) { _logger.LogWarning(exception, "TranslationCompleted handler failed"); }
        }
    }

    private void ApplyResultDiagnostics(TranslationResult result)
    {
        _diagnostics.LastTranslationFinishedAt = DateTimeOffset.Now;
        _diagnostics.LastTranslationDurationMs = result.DurationMs;
        _diagnostics.LastTranslationRawResponse = result.RawResponse;
        _diagnostics.LastTranslationParsedText = result.ParsedTranslation;
        _diagnostics.LastTranslationPostProcessedText = result.PostProcessedTranslation;
        _diagnostics.LastTranslationError = result.ErrorMessage ?? string.Empty;
        _diagnostics.LastTranslationWasFromCache = result.FromCache;

        if (result.Success)
        {
            _diagnostics.TranslationCompletedCount++;
            if (!result.FromCache)
                _diagnostics.LastTranslationQueueStatus = "completed";
        }
        else
        {
            _diagnostics.TranslationFailedCount++;
            _diagnostics.LastTranslationQueueStatus = "failed";
        }
        PublishDiagnostics();
    }

    private void PublishDiagnostics()
    {
        _diagnosticsStore.Save();
        _diagnostics.NotifyChanged();
        try { DiagnosticsChanged?.Invoke(); }
        catch (Exception exception) { _logger.LogDebug(exception, "DiagnosticsChanged handler failed"); }
    }

    private static bool IsFuzzyDuplicate(string value, string? other)
    {
        if (string.IsNullOrWhiteSpace(other)) return false;
        var left = Normalize(value);
        var right = Normalize(other);
        if (left == right) return true;
        if (left.Length < 20 || right.Length < 20) return false;
        var allowedDistance = Math.Max(1, Math.Min(left.Length, right.Length) / 33);
        return Math.Abs(left.Length - right.Length) <= allowedDistance &&
            LevenshteinDistance(left, right, allowedDistance) <= allowedDistance;
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    private static int LevenshteinDistance(string left, string right, int stopAfter)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var rowMinimum = current[0];
            for (var j = 1; j <= right.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
                rowMinimum = Math.Min(rowMinimum, current[j]);
            }
            if (rowMinimum > stopAfter) return rowMinimum;
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    public void Dispose()
    {
        // Idempotent: a singleton can be disposed more than once on shutdown,
        // and cancelling/disposing an already-disposed CTS throws.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts.Cancel();
        try { _worker.Wait(2000); } catch { }
        _cts.Dispose();
        _signal.Dispose();
    }
}
