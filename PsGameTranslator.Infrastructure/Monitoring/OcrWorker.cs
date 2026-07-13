using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Ocr;
using PsGameTranslator.Ocr;

namespace PsGameTranslator.Infrastructure.Monitoring;

/// <summary>
/// Processes OCR requests one at a time, fully decoupled from the capture loop.
/// Buffers a small ordered queue of changed subtitle crops (Part D) so fast
/// dialogue frames that arrive while OCR/translation is busy are processed in
/// order instead of being overwritten by the latest frame. Visually identical
/// frames (same crop hash) are kept only once. Never runs OCR in parallel.
/// </summary>
public sealed class OcrWorker : IDisposable
{
    private static readonly string BufferStatePath = Path.Combine(
        AppContext.BaseDirectory, "debug", "last_ocr_frame_buffer_state.json");

    private static readonly string DebugDirectory = Path.Combine(AppContext.BaseDirectory, "debug");

    private readonly OcrEngineManager _ocrEngineManager;
    private readonly ILogger<OcrWorker> _logger;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    private readonly Queue<PendingOcrFrame> _buffer = new();
    private string _lastEnqueuedCropHash = string.Empty;
    private volatile bool _isBusy;
    private int _ocrTimeoutMs = 10000;
    private int _minOcrIntervalMs = 250;
    private bool _processLatestPendingAfterOcr = true;
    private int _maxBufferSize = 6;
    private int _maxBufferedFrameAgeMs = 2500;
    private DateTimeOffset _lastBufferStateSavedAt = DateTimeOffset.MinValue;

    /// <summary>Raised on a background thread whenever an OCR attempt finishes.</summary>
    public event Action<OcrWorkResult>? Completed;

    public OcrWorker(OcrEngineManager ocrEngineManager, ILogger<OcrWorker> logger)
    {
        _ocrEngineManager = ocrEngineManager;
        _logger = logger;
        _worker = Task.Run(WorkerLoopAsync);
    }

    public bool IsBusy => _isBusy;
    public long? PendingFrameNumber { get; private set; }
    public DateTimeOffset? LastOcrStartedAt { get; private set; }
    public DateTimeOffset? LastOcrFinishedAt { get; private set; }
    public long LastOcrDurationMs { get; private set; }
    public long LastProcessedFrameNumber { get; private set; } = -1;
    public string LastProcessedCropHash { get; private set; } = string.Empty;
    public long OcrStartedCount { get; private set; }
    public long OcrCompletedCount { get; private set; }
    public long PendingFrameReplacedCount { get; private set; }
    public int BufferedFrameCount { get { lock (_gate) return _buffer.Count; } }
    public long DroppedFrameCount { get; private set; }
    public long ExpiredFrameCount { get; private set; }
    public long DuplicateFrameSkippedCount { get; private set; }

    /// <param name="ocrTimeoutMs">Per-request OCR timeout.</param>
    /// <param name="processLatestPendingAfterOcr">
    /// When true (default), immediately drains the frame buffer after finishing
    /// the current frame instead of waiting for the next signal.
    /// </param>
    /// <param name="maxBufferSize">
    /// 1 = legacy latest-frame-only behavior; larger keeps an ordered queue of
    /// changed frames so fast subtitles are not skipped (Part D).
    /// </param>
    public void Configure(
        int ocrTimeoutMs,
        int minOcrIntervalMs,
        bool processLatestPendingAfterOcr,
        int maxBufferSize = 6,
        int maxBufferedFrameAgeMs = 2500)
    {
        _ocrTimeoutMs = Math.Max(500, ocrTimeoutMs);
        _minOcrIntervalMs = Math.Max(0, minOcrIntervalMs);
        _processLatestPendingAfterOcr = processLatestPendingAfterOcr;
        _maxBufferSize = Math.Max(1, maxBufferSize);
        _maxBufferedFrameAgeMs = Math.Max(250, maxBufferedFrameAgeMs);
    }

    /// <summary>
    /// Submits a changed frame for OCR. Frames are buffered in arrival order;
    /// visually identical consecutive frames (same crop hash) are kept once.
    /// When the buffer is full, expired frames are dropped first, then the
    /// oldest — the newest changed subtitle is never the one lost.
    /// </summary>
    public void SubmitFrame(PendingOcrFrame frame)
    {
        lock (_gate)
        {
            // Identical to the newest buffered/last processed crop → nothing new to read.
            if (frame.CropHash.Length > 0 &&
                (frame.CropHash == _lastEnqueuedCropHash || frame.CropHash == LastProcessedCropHash) &&
                !frame.IsForced)
            {
                DuplicateFrameSkippedCount++;
                return;
            }

            _buffer.Enqueue(frame);
            _lastEnqueuedCropHash = frame.CropHash;
            PendingFrameNumber = frame.FrameNumber;

            while (_buffer.Count > _maxBufferSize)
            {
                var survivors = _buffer
                    .Where(f => (DateTimeOffset.Now - f.CapturedAt).TotalMilliseconds <= _maxBufferedFrameAgeMs)
                    .ToList();
                var expiredDropped = _buffer.Count - survivors.Count;
                if (expiredDropped > 0)
                {
                    ExpiredFrameCount += expiredDropped;
                }
                else
                {
                    survivors.RemoveAt(0); // last resort: drop the oldest
                    DroppedFrameCount++;
                    PendingFrameReplacedCount++;
                }

                _buffer.Clear();
                foreach (var survivor in survivors) _buffer.Enqueue(survivor);
                if (_buffer.Count <= _maxBufferSize) break;
            }

            if (!_isBusy && _signal.CurrentCount == 0)
            {
                try { _signal.Release(); }
                catch (SemaphoreFullException) { /* worker already signaled */ }
            }
        }

        SaveBufferStateSnapshot();
    }

    private async Task WorkerLoopAsync()
    {
        var cancellationToken = _cts.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            while (true)
            {
                PendingOcrFrame? frame;
                lock (_gate)
                {
                    // Drain in arrival order; skip frames that sat in the buffer too
                    // long — their subtitle is almost certainly gone from screen.
                    frame = null;
                    while (_buffer.Count > 0)
                    {
                        var candidate = _buffer.Dequeue();
                        if (!candidate.IsForced &&
                            (DateTimeOffset.Now - candidate.CapturedAt).TotalMilliseconds > _maxBufferedFrameAgeMs)
                        {
                            ExpiredFrameCount++;
                            continue;
                        }
                        frame = candidate;
                        break;
                    }
                    PendingFrameNumber = _buffer.Count > 0 ? _buffer.Peek().FrameNumber : null;
                }
                if (frame is null) break;

                try
                {
                    await ProcessFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                SaveBufferStateSnapshot();

                // Catch up immediately to whatever arrived while OCR was busy,
                // instead of waiting for the next capture-loop signal.
                if (!_processLatestPendingAfterOcr)
                {
                    lock (_gate)
                    {
                        if (_buffer.Count > 0 && _signal.CurrentCount == 0)
                            _signal.Release();
                    }
                    break;
                }
            }
        }
    }

    private async Task ProcessFrameAsync(PendingOcrFrame frame, CancellationToken cancellationToken)
    {
        _isBusy = true;
        if (LastOcrStartedAt is { } previousStart)
        {
            var elapsedMs = (DateTimeOffset.Now - previousStart).TotalMilliseconds;
            var delayMs = _minOcrIntervalMs - elapsedMs;
            if (delayMs > 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    _isBusy = false;
                    throw;
                }
            }
        }

        OcrStartedCount++;
        var startedAt = DateTimeOffset.Now;
        LastOcrStartedAt = startedAt;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_ocrTimeoutMs);
        var timer = Stopwatch.StartNew();

        OcrWorkResult workResult;
        try
        {
            SaveLatestOcrImages(frame.ImageBytes);
            var result = await _ocrEngineManager.RecognizeAsync(
                new OcrRequest
                {
                    ImageBytes = frame.ImageBytes,
                    Language = "en",
                    RegionId = "live-subtitle",
                    PreprocessingSettings = new PreprocessingSettings
                    {
                        Preset = PreprocessingPreset.FastSubtitle,
                    },
                },
                timeout.Token).ConfigureAwait(false);
            timer.Stop();
            workResult = new OcrWorkResult
            {
                Frame = frame,
                Result = result,
                Success = true,
                DurationMs = timer.ElapsedMilliseconds,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.Now,
            };
            SaveOcrRequestSnapshot(frame, result, workResult);
            SaveOcrFailureReasonIfNeeded(frame, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timer.Stop();
            workResult = new OcrWorkResult
            {
                Frame = frame,
                Success = false,
                ErrorMessage = $"OCR timed out after {_ocrTimeoutMs} ms.",
                DurationMs = timer.ElapsedMilliseconds,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.Now,
            };
            _logger.LogWarning("ocr_worker_timeout - frame={Frame}", frame.FrameNumber);
            SaveWorkerFailureSnapshot(frame, workResult.ErrorMessage);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            timer.Stop();
            workResult = new OcrWorkResult
            {
                Frame = frame,
                Success = false,
                ErrorMessage = exception.Message,
                DurationMs = timer.ElapsedMilliseconds,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.Now,
            };
            _logger.LogError(exception, "ocr_worker_failed - frame={Frame}", frame.FrameNumber);
            SaveWorkerFailureSnapshot(frame, workResult.ErrorMessage);
        }
        finally
        {
            _isBusy = false;
        }

        LastOcrFinishedAt = workResult.FinishedAt;
        LastOcrDurationMs = workResult.DurationMs;
        LastProcessedFrameNumber = frame.FrameNumber;
        LastProcessedCropHash = frame.CropHash;
        OcrCompletedCount++;

        _logger.LogInformation(
            "stage_latency - stage=ocr frame={Frame} ocr_ms={OcrMs} capture_to_ocr_done_ms={TotalMs:F0} success={Success}",
            frame.FrameNumber,
            workResult.DurationMs,
            (workResult.FinishedAt - frame.CapturedAt).TotalMilliseconds,
            workResult.Success);

        try { Completed?.Invoke(workResult); }
        catch (Exception exception) { _logger.LogWarning(exception, "OcrWorker.Completed handler failed"); }
    }

    private void SaveBufferStateSnapshot()
    {
        var now = DateTimeOffset.Now;
        if ((now - _lastBufferStateSavedAt).TotalMilliseconds < 500) return;
        _lastBufferStateSavedAt = now;

        try
        {
            object snapshot;
            lock (_gate)
            {
                snapshot = new
                {
                    Timestamp = now,
                    BufferedFrameCount = _buffer.Count,
                    MaxBufferSize = _maxBufferSize,
                    MaxBufferedFrameAgeMs = _maxBufferedFrameAgeMs,
                    DroppedFrameCount,
                    ExpiredFrameCount,
                    DuplicateFrameSkippedCount,
                    OcrStartedCount,
                    OcrCompletedCount,
                    LastProcessedFrameNumber,
                    BufferedFrames = _buffer.Select(f => new
                    {
                        f.FrameNumber,
                        f.CapturedAt,
                        AgeMs = (long)(now - f.CapturedAt).TotalMilliseconds,
                        f.Reason,
                    }).ToArray(),
                };
            }

            Directory.CreateDirectory(Path.GetDirectoryName(BufferStatePath)!);
            DebugFileWriter.QueueText(
                BufferStatePath,
                System.Text.Json.JsonSerializer.Serialize(
                    snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save OCR frame buffer diagnostics");
        }
    }

    private static void SaveLatestOcrImages(byte[] imageBytes)
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            File.WriteAllBytes(Path.Combine(DebugDirectory, "latest_ocr_region_raw.png"), imageBytes);
            File.WriteAllBytes(Path.Combine(DebugDirectory, "latest_ocr_region_processed.png"), imageBytes);
            File.WriteAllBytes(Path.Combine(DebugDirectory, "latest_ocr_sent_to_provider.png"), imageBytes);
        }
        catch
        {
            // Diagnostics must never break live OCR.
        }
    }

    private static void SaveOcrRequestSnapshot(PendingOcrFrame frame, PsGameTranslator.Core.Models.OcrResult result, OcrWorkResult workResult)
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            var snapshot = new
            {
                Timestamp = DateTimeOffset.Now,
                Frame = frame.FrameNumber,
                frame.Reason,
                SelectedWindowTitle = "active capture window",
                CaptureRect = new
                {
                    frame.WindowLeft,
                    frame.WindowTop,
                    frame.WindowWidth,
                    frame.WindowHeight,
                },
                OcrRegionRect = new
                {
                    frame.SavedRegionX,
                    frame.SavedRegionY,
                    frame.SavedRegionWidth,
                    frame.SavedRegionHeight,
                },
                CropSize = new
                {
                    frame.FinalCropWidth,
                    frame.FinalCropHeight,
                    frame.OcrImageWidth,
                    frame.OcrImageHeight,
                },
                PreprocessingSettings = "FastSubtitle",
                SentImagePath = Path.Combine(DebugDirectory, "latest_ocr_sent_to_provider.png"),
                ProviderUsed = result.ProviderName,
                ProviderDuration = result.DurationMs,
                ProviderRawOutput = result.RawOutput,
                ParsedLineCount = result.Lines.Count,
                ParsedText = result.Text,
                result.Confidence,
                result.Success,
                result.ErrorMessage,
                WorkerDuration = workResult.DurationMs,
                Lines = result.Lines.Select(line => new
                {
                    line.Text,
                    line.Confidence,
                    line.BoundingBox,
                }).ToArray(),
            };
            DebugFileWriter.QueueText(
                Path.Combine(DebugDirectory, "last_ocr_request.json"),
                System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // Diagnostics must never break live OCR.
        }
    }

    private static void SaveOcrFailureReasonIfNeeded(PendingOcrFrame frame, PsGameTranslator.Core.Models.OcrResult result)
    {
        if (result.Success && (result.Lines.Count > 0 || !string.IsNullOrWhiteSpace(result.Text)))
            return;

        var finalReason = !result.Success
            ? "OCR provider failed"
            : "OCR provider returned no usable result";
        SaveFailureReason(frame, finalReason, !result.Success, result.ErrorMessage, result);
    }

    private static void SaveWorkerFailureSnapshot(PendingOcrFrame frame, string error)
    {
        SaveFailureReason(frame, "OCR worker failed", providerFailed: true, providerError: error, result: null);
    }

    private static void SaveFailureReason(
        PendingOcrFrame frame,
        string finalReason,
        bool providerFailed,
        string providerError,
        PsGameTranslator.Core.Models.OcrResult? result)
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            var snapshot = new
            {
                Timestamp = DateTimeOffset.Now,
                Frame = frame.FrameNumber,
                ProviderFailed = providerFailed,
                ProviderError = providerError,
                RawProviderOutput = result?.RawOutput ?? string.Empty,
                RawLinesCount = result?.Lines.Count ?? 0,
                ParsedLinesCount = result?.Lines.Count ?? 0,
                Confidence = result?.Confidence ?? 0,
                ConfiguredConfidenceThreshold = "see AppSettings.MinimumConfidenceThreshold",
                RejectedByThreshold = false,
                RejectedByCandidateValidator = false,
                RejectedAsDuplicateOrUnchanged = false,
                FinalReason = finalReason,
            };
            DebugFileWriter.QueueText(
                Path.Combine(DebugDirectory, "last_ocr_failure_reason.json"),
                System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // Diagnostics must never break live OCR.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _worker.Wait(2000); } catch { /* shutting down */ }
        _cts.Dispose();
        _signal.Dispose();
    }
}
