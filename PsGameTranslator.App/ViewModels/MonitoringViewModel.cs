using System.Diagnostics;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using PsGameTranslator.App.Commands;
using PsGameTranslator.Capture;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Ocr;
using PsGameTranslator.Infrastructure.Region;
using PsGameTranslator.Ocr;
using PsGameTranslator.Overlay;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Core.Subtitles;
using PsGameTranslator.Infrastructure.Monitoring;
using PsGameTranslator.Infrastructure.Subtitles;
using PsGameTranslator.Infrastructure.Translation;

namespace PsGameTranslator.App.ViewModels;

public sealed class MonitoringViewModel : ObservableObject
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private readonly IWindowCaptureService _captureService;
    private readonly IImageCropService _cropService;
    private readonly IRegionPersistenceService _regionService;
    private readonly HttpOcrService _ocrService;
    private readonly OcrTextCleaner _textCleaner;
    private readonly ISubtitleFormatter _subtitleFormatter;
    private readonly SubtitleFormatterSettings _subtitleFormatterSettings;
    private readonly SpeakerNameDetector _speakerNameDetector;
    private readonly Services.UserSettingsPersistenceService _settingsPersistence;
    private readonly IOverlayService _overlayService;
    private readonly IOverlaySettingsService _overlaySettingsService;
    private readonly Services.OverlayMonitorCoordinator _overlayMonitorCoordinator;
    private readonly CaptureViewModel _captureViewModel;
    private readonly ILogger<MonitoringViewModel> _logger;
    private readonly SynchronizationContext _uiContext;
    private readonly OcrWorker _ocrWorker;
    private readonly FastFrameDifferenceService _fastFrameDifferenceService;
    private readonly SemaphoreSlim _realtimeStateGate = new(1, 1);
    private readonly object _monitoringLifetimeLock = new();

    private readonly TranslationQueue _translationQueue;
    private readonly SubtitleTranslationQueue _subtitleTranslationQueue;
    private readonly TranslationProviderSelector _translationProviderSelector;
    private readonly TranslationSettings _translationSettings;
    private readonly PipelineDiagnostics _pipelineDiagnostics;
    private readonly PipelineDiagnosticsStore _pipelineDiagnosticsStore;
    private readonly SubtitleDisplayStateManager _subtitleDisplayStateManager;
    private readonly SubtitleLineClassifier _subtitleLineClassifier;
    private readonly SubtitleFilterSettings _subtitleFilterSettings;
    private readonly GameProfileRepository _gameProfileRepository;
    private readonly RefinementOrchestrator _refinementOrchestrator;
    private readonly OrderedSubtitlePipeline _orderedSubtitlePipeline;
    private readonly Services.RuntimePipelineHealthService _pipelineHealthService;
    private readonly IOcrServerService _ocrServerService;
    private readonly OcrEngineSettings _ocrEngineSettings;
    private readonly OcrEngineInstallService _ocrEngineInstallService;
    private readonly OcrProviderFactory _ocrProviderFactory;
    private string _pipelineDoctorReportText = "Not run yet.";

    private readonly AsyncRelayCommand _startCommand;
    private readonly AsyncRelayCommand _stopCommand;
    private CancellationTokenSource? _monitoringCancellation;
    private Task? _monitoringTask;

    private byte[]? _previousOcrImage;
    private byte[]? _latestFullCapture;
    private byte[]? _latestRegionCrop;
    private byte[]? _latestFinalCrop;
    private byte[]? _latestOcrImage;
    private string? _lastImageHash;
    private string _lastOverlayText = string.Empty;
    private bool _previousOcrSucceeded;
    private bool _previousOcrWasEmpty = true;
    private bool _previousOcrFailed;

    private bool _isMonitoring;
    private long _frameNumber;
    private long _lastOcrFrameNumber;
    private DateTimeOffset? _lastOcrStartedAt;
    private DateTimeOffset? _lastOcrFinishedAt;
    private long _skippedCount;
    private int _emptyOcrCount;
    private long _lastOcrDurationMs;
    private long _lastTotalLoopDurationMs;
    private double? _lastRegionDifferencePercent;
    private string _lastDecision = "not_started";
    private string _lastSkipReason = "-";
    private string _lastOcrRunReason = "-";
    private string _lastOcrRawText = string.Empty;
    private string _lastOcrCleanedText = string.Empty;
    private FormattedSubtitle _lastFormattedSubtitle = new() { IsEmpty = true };
    private double _lastOcrConfidence;
    private DateTimeOffset? _lastOverlayUpdateTime;
    private SubtitleLineSelectionResult _lastLineSelection = new();

    // Subtitle line filtering manual test (Part I).
    private string _filterTestLine1 = "Shit, no!";
    private string _filterTestLine2 = "Press R near the O'Driscoll to hogtie them";
    private string _filterTestResultText = "-";
    private string _selectedSubtitleLinesText = "-";
    private string _rejectedHudLinesText = "-";

    private string _monitoringStatusText = "Stopped";
    private string _targetWindowText = "No window selected";
    private string _lastCaptureTimeText = "-";
    private string _lastOcrTimeText = "-";
    private string _lastDetectedText = string.Empty;
    private bool _hasError;
    private string _errorText = string.Empty;
    private ImageSource? _cropPreview;
    private ImageSource? _ocrImagePreview;

    // Reliability-first defaults.
    private bool _neverSkipOcr;
    private bool _enableImageHashSkip;
    private bool _enableRegionDifferenceSkip;
    private int _ocrRequestTimeoutMs = 10000;
    private int _forceOcrEveryNFrames = 1;
    private int _forceOcrEveryMilliseconds = 750;
    private double _regionChangeThresholdPercent = 0.10;
    private int _clearOverlayAfterEmptyOcrCount = 3;
    private bool _saveDebugFrames = true;
    private bool _enableOcrPreprocessing = true;
    private double _upscaleFactor = 1.25;
    private bool _convertToGrayscale = true;
    private bool _increaseContrast = true;
    private bool _sharpenImage;
    private string _thresholdMode = "none";
    private bool _enableCharWhitelist;
    private bool _autoStartOverlay = true;
    private bool _pauseWhenWindowNotActive;
    private int _cropPaddingLeft;
    private int _cropPaddingTop;
    private int _cropPaddingRight = 80;
    private int _cropPaddingBottom;
    private double _maxLineRightPercent = 0.88;

    // ── Fast capture + OCR worker pipeline settings (Part B/F/G) ─────────────────
    private int _captureIntervalMs = 100;
    private int _maxOcrFrameBufferSize = 6;
    private int _maxBufferedFrameAgeMs = 2500;
    private bool _doNotDropUnprocessedChangedFrames = true;
    private int _minOcrIntervalMs = 250;
    private int _maxOcrIntervalMs = 1000;
    private bool _processLatestPendingFrameAfterOcr = true;
    private bool _keepOnlyLatestPendingOcrFrame = true;
    private bool _enableFastFrameDifference = true;
    private double _frameDifferenceThresholdPercent = 0.25;
    private int _forceOcrEveryMs = 1000;
    private bool _runOcrOnEveryChangedFrame = true;
    private int _minOverlayDisplayMs = 900;
    private bool _keepPreviousSubtitleOnEmptyOcr = true;
    private int _previousSubtitleHoldMs = 800;
    private bool _enableFastOcrMode = true;

    private byte[]? _previousFastDiffImage;
    private DateTimeOffset? _lastEmptyOcrAt;
    private DateTimeOffset? _lastOverlayShownAt;

    // Part I — realtime performance diagnostics.
    private double _captureFps;
    private long _captureDurationMs;
    private long _cropDurationMs;
    private long _differenceDurationMs;
    private long _ocrSkippedNoChangeCount;
    private long _ocrForcedCount;
    private long _timeFromCaptureToOverlayMs;
    private long _timeFromOcrToOverlayMs;
    private long _translationPendingCount;
    private DateTimeOffset _lastTickAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDebugFrameSavedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMonitoringStateSavedAt = DateTimeOffset.MinValue;
    private int _finalOcrCropWidth;
    private int _finalOcrCropHeight;
    private string _cropSizeWarningText = string.Empty;
    private bool _synchronizingSecondaryRegions;

    private static readonly string DebugDirectory =
        Path.Combine(AppContext.BaseDirectory, "debug");

    public MonitoringViewModel(
        IWindowCaptureService captureService,
        IImageCropService cropService,
        IRegionPersistenceService regionService,
        HttpOcrService ocrService,
        OcrTextCleaner textCleaner,
        ISubtitleFormatter subtitleFormatter,
        SubtitleFormatterSettings subtitleFormatterSettings,
        SpeakerNameDetector speakerNameDetector,
        IOverlayService overlayService,
        IOverlaySettingsService overlaySettingsService,
        Services.OverlayMonitorCoordinator overlayMonitorCoordinator,
        CaptureViewModel captureViewModel,
        ILogger<MonitoringViewModel> logger,
        TranslationQueue translationQueue,
        SubtitleTranslationQueue subtitleTranslationQueue,
        TranslationProviderSelector translationProviderSelector,
        TranslationSettings translationSettings,
        Services.UserSettingsPersistenceService settingsPersistence,
        PipelineDiagnostics pipelineDiagnostics,
        PipelineDiagnosticsStore pipelineDiagnosticsStore,
        SubtitleDisplayStateManager subtitleDisplayStateManager,
        SubtitleLineClassifier subtitleLineClassifier,
        SubtitleFilterSettings subtitleFilterSettings,
        GameProfileRepository gameProfileRepository,
        OcrWorker ocrWorker,
        FastFrameDifferenceService fastFrameDifferenceService,
        RefinementOrchestrator refinementOrchestrator,
        OrderedSubtitlePipeline orderedSubtitlePipeline,
        Services.RuntimePipelineHealthService pipelineHealthService,
        IOcrServerService ocrServerService,
        OcrEngineSettings ocrEngineSettings,
        OcrEngineInstallService ocrEngineInstallService,
        OcrProviderFactory ocrProviderFactory)
    {
        _captureService = captureService;
        _cropService = cropService;
        _regionService = regionService;
        _ocrService = ocrService;
        _textCleaner = textCleaner;
        _subtitleFormatter = subtitleFormatter;
        _subtitleFormatterSettings = subtitleFormatterSettings;
        _speakerNameDetector = speakerNameDetector;
        _overlayService = overlayService;
        _overlaySettingsService = overlaySettingsService;
        _overlayMonitorCoordinator = overlayMonitorCoordinator;
        _captureViewModel = captureViewModel;
        _logger = logger;
        _translationQueue = translationQueue;
        _subtitleTranslationQueue = subtitleTranslationQueue;
        _translationProviderSelector = translationProviderSelector;
        _translationSettings = translationSettings;
        _settingsPersistence = settingsPersistence;
        _pipelineDiagnostics = pipelineDiagnostics;
        _pipelineDiagnosticsStore = pipelineDiagnosticsStore;
        _subtitleDisplayStateManager = subtitleDisplayStateManager;
        _subtitleLineClassifier = subtitleLineClassifier;
        _subtitleFilterSettings = subtitleFilterSettings;
        _gameProfileRepository = gameProfileRepository;
        _ocrWorker = ocrWorker;
        _fastFrameDifferenceService = fastFrameDifferenceService;
        _refinementOrchestrator = refinementOrchestrator;
        _orderedSubtitlePipeline = orderedSubtitlePipeline;
        _pipelineHealthService = pipelineHealthService;
        _ocrServerService = ocrServerService;
        _ocrEngineSettings = ocrEngineSettings;
        _ocrEngineInstallService = ocrEngineInstallService;
        _ocrProviderFactory = ocrProviderFactory;
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("MonitoringViewModel must be created on the UI thread.");


        _startCommand = new AsyncRelayCommand(StartMonitoringAsync, () => !IsMonitoring);
        _stopCommand = new AsyncRelayCommand(StopMonitoringAsync, () => IsMonitoring);
        RunSingleTickCommand = new AsyncRelayCommand(RunSingleTickAsync);
        RunOcrOnCurrentPreviewCommand = new AsyncRelayCommand(RunOcrOnCurrentPreviewAsync);
        ToggleNeverSkipOcrCommand = new AsyncRelayCommand(ToggleNeverSkipOcrAsync);
        ShowTestOverlayCommand = new AsyncRelayCommand(ShowTestOverlayAsync);
        ShowTestOverlayLongTextCommand = new AsyncRelayCommand(ShowTestOverlayLongTextAsync);
        TestSubtitleFilteringCommand = new AsyncRelayCommand(TestSubtitleFilteringAsync);
        StartFastDialogueStressTestCommand = new AsyncRelayCommand(StartFastDialogueStressTestAsync);
        RunPipelineDoctorCommand = new AsyncRelayCommand(RunPipelineDoctorAsync);
        AddSecondaryOcrRegionCommand = new AsyncRelayCommand(() =>
        {
            SecondaryOcrRegions.Add(new SecondaryOcrRegionEntry
            {
                Label = $"Bölge {SecondaryOcrRegions.Count + 1}",
                UseForSpeakerName = !SecondaryOcrRegions.Any(region => region.UseForSpeakerName),
            });
            return Task.CompletedTask;
        });
        RemoveSecondaryOcrRegionCommand = new Commands.ParameterizedAsyncRelayCommand(p =>
        {
            if (p is SecondaryOcrRegionEntry entry) SecondaryOcrRegions.Remove(entry);
            return Task.CompletedTask;
        });

        _subtitleFormatterSettings.ShowSpeakerName = _translationSettings.ShowSpeakerNameInOverlay;
        foreach (var region in _translationSettings.SecondaryOcrRegions)
            SecondaryOcrRegions.Add(SecondaryOcrRegionEntry.FromSettings(region));
        SecondaryOcrRegions.CollectionChanged += OnSecondaryOcrRegionsChanged;
        foreach (var region in SecondaryOcrRegions) region.PropertyChanged += OnSecondaryOcrRegionPropertyChanged;

        _subtitleTranslationQueue.Completed += OnSubtitleTranslationCompleted;
        _ocrWorker.Completed += OnOcrWorkerCompleted;
        _refinementOrchestrator.RefinementCompleted += OnRefinementCompleted;
        SyncOverlayDiagnostics();
    }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (SetProperty(ref _isMonitoring, value))
            {
                RaisePropertyChanged(nameof(IsMonitoringRunning));
                _startCommand.NotifyCanExecuteChanged();
                _stopCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsMonitoringRunning => _isMonitoring;
    public bool IsOcrBusy => _ocrWorker.IsBusy;
    public long FrameNumber => _frameNumber;
    public long LastOcrFrameNumber => _lastOcrFrameNumber;
    public DateTimeOffset? LastOcrStartedAt => _lastOcrStartedAt;
    public DateTimeOffset? LastOcrFinishedAt => _lastOcrFinishedAt;
    public long FramesSinceLastOcr => Math.Max(0, _frameNumber - _lastOcrFrameNumber);
    public double MillisecondsSinceLastOcr => _lastOcrFinishedAt is { } finished
        ? Math.Max(0, (DateTimeOffset.Now - finished).TotalMilliseconds)
        : double.PositiveInfinity;

    public string MonitoringStatusText { get => _monitoringStatusText; private set => SetProperty(ref _monitoringStatusText, value); }
    public string TargetWindowText { get => _targetWindowText; private set => SetProperty(ref _targetWindowText, value); }
    public string LastCaptureTimeText { get => _lastCaptureTimeText; private set => SetProperty(ref _lastCaptureTimeText, value); }
    public string LastOcrTimeText { get => _lastOcrTimeText; private set => SetProperty(ref _lastOcrTimeText, value); }
    public string LastDetectedText { get => _lastDetectedText; private set => SetProperty(ref _lastDetectedText, value); }
    public bool HasError { get => _hasError; private set => SetProperty(ref _hasError, value); }
    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
    public ImageSource? CropPreview { get => _cropPreview; private set => SetProperty(ref _cropPreview, value); }
    public ImageSource? OcrImagePreview { get => _ocrImagePreview; private set => SetProperty(ref _ocrImagePreview, value); }

    public string DiagnosticsText => string.Join(Environment.NewLine,
    [
        $"Monitoring status: {MonitoringStatusText}",
        $"NeverSkipOcr: {NeverSkipOcr}",
        $"EnableImageHashSkip: {EnableImageHashSkip}",
        $"EnableRegionDifferenceSkip: {EnableRegionDifferenceSkip}",
        $"FrameNumber: {_frameNumber}",
        $"OCR started/completed count: {_ocrWorker.OcrStartedCount}/{_ocrWorker.OcrCompletedCount}",
        $"Skipped count: {_skippedCount}",
        $"Pending frame replaced count: {_ocrWorker.PendingFrameReplacedCount}",
        $"Last decision: {_lastDecision}",
        $"Last skip reason: {_lastSkipReason}",
        $"Last OCR run reason: {_lastOcrRunReason}",
        $"Last OCR duration ms: {_lastOcrDurationMs}",
        $"Last total loop duration ms: {_lastTotalLoopDurationMs}",
        $"Last region difference percent: {(_lastRegionDifferencePercent?.ToString("F4") ?? "n/a")}",
        $"Last OCR raw text: {_lastOcrRawText}",
        $"Last OCR cleaned text: {_lastOcrCleanedText}",
        $"Formatter enabled: {_subtitleFormatterSettings.EnableSubtitleFormatter}",
        $"Speaker name: {_lastFormattedSubtitle.SpeakerName}",
        $"Main text: {_lastFormattedSubtitle.MainText}",
        $"Display text: {_lastFormattedSubtitle.DisplayText}",
        $"Last OCR confidence: {_lastOcrConfidence:P1}",
        $"Empty OCR count: {_emptyOcrCount}",
        $"Last overlay update: {(_lastOverlayUpdateTime?.ToString("HH:mm:ss.fff") ?? "-")}",
        $"Translation queue status: {_pipelineDiagnostics.LastTranslationQueueStatus}",
        $"Translation source: {_pipelineDiagnostics.LastTranslationSourceText}",
        $"Translation error: {_pipelineDiagnostics.LastTranslationError}",
        $"Translation counts (enqueued/started/completed/failed/dropped/cache): " +
            $"{_pipelineDiagnostics.TranslationEnqueueCount}/{_pipelineDiagnostics.TranslationStartedCount}/" +
            $"{_pipelineDiagnostics.TranslationCompletedCount}/{_pipelineDiagnostics.TranslationFailedCount}/" +
            $"{_pipelineDiagnostics.TranslationDroppedCount}/{_pipelineDiagnostics.TranslationCacheHitCount}",
    ]);

    // Backward-compatible debug bindings.
    public string DbgFrameCount => _frameNumber.ToString();
    public string DbgSkippedCount => _skippedCount.ToString();
    public string DbgOcrCount => _ocrWorker.OcrStartedCount.ToString();
    public string DbgLastOcrReason => _lastOcrRunReason;
    public string DbgLastSkipReason => _lastSkipReason;
    public string DbgFramesSinceOcr => FramesSinceLastOcr.ToString();
    public string DbgMsSinceOcr => double.IsPositiveInfinity(MillisecondsSinceLastOcr) ? "-" : $"{MillisecondsSinceLastOcr:F0} ms";
    public string DbgLastOcrMs => _lastOcrDurationMs == 0 ? "-" : $"{_lastOcrDurationMs} ms";
    public string DbgCurrentHash => _lastImageHash is { Length: >= 8 } hash ? hash[..8] : "-";
    public string DbgPrevHash => "See monitoring_state.json";
    public string DbgRegionDiff => _lastRegionDifferencePercent is { } diff ? $"{diff:F4}%" : "n/a";

    public bool NeverSkipOcr { get => _neverSkipOcr; set { if (SetProperty(ref _neverSkipOcr, value)) RefreshDiagnostics(); } }
    public bool RunOcrOnEveryTick { get => NeverSkipOcr; set => NeverSkipOcr = value; }
    public bool EnableSubtitleFormatter
    {
        get => _subtitleFormatterSettings.EnableSubtitleFormatter;
        set
        {
            if (_subtitleFormatterSettings.EnableSubtitleFormatter == value) return;
            _subtitleFormatterSettings.EnableSubtitleFormatter = value;
            OnPropertyChanged();
            RefreshDiagnostics();
        }
    }
    public bool ShowSpeakerName
    {
        get => _subtitleFormatterSettings.ShowSpeakerName;
        set
        {
            if (_subtitleFormatterSettings.ShowSpeakerName == value) return;
            _subtitleFormatterSettings.ShowSpeakerName = value;
            _translationSettings.ShowSpeakerNameInOverlay = value;
            _settingsPersistence.Save();
            OnPropertyChanged();
        }
    }

    public bool EnableSecondarySpeakerOcr
    {
        get => _translationSettings.EnableSecondarySpeakerOcr;
        set
        {
            if (_translationSettings.EnableSecondarySpeakerOcr == value) return;
            _translationSettings.EnableSecondarySpeakerOcr = value;
            _settingsPersistence.Save();
            OnPropertyChanged();
        }
    }
    public bool RemoveHudNoise
    {
        get => _subtitleFormatterSettings.RemoveHudNoise;
        set
        {
            if (_subtitleFormatterSettings.RemoveHudNoise == value) return;
            _subtitleFormatterSettings.RemoveHudNoise = value;
            OnPropertyChanged();
        }
    }
    public int MaxSubtitleLines
    {
        get => _subtitleFormatterSettings.MaxSubtitleLines;
        set
        {
            var clamped = Math.Max(1, value);
            if (_subtitleFormatterSettings.MaxSubtitleLines == clamped) return;
            _subtitleFormatterSettings.MaxSubtitleLines = clamped;
            OnPropertyChanged();
        }
    }
    public int MaxCharactersPerLine
    {
        get => _subtitleFormatterSettings.MaxCharactersPerLine;
        set
        {
            var clamped = Math.Max(8, value);
            if (_subtitleFormatterSettings.MaxCharactersPerLine == clamped) return;
            _subtitleFormatterSettings.MaxCharactersPerLine = clamped;
            OnPropertyChanged();
        }
    }

    public string FormatterRawOcrText => _lastOcrRawText;
    public string FormatterCleanedOcrText => _lastOcrCleanedText;
    public string FormatterSpeakerName => _lastFormattedSubtitle.SpeakerName;
    public string FormatterMainText => _lastFormattedSubtitle.MainText;
    public string FormatterDisplayText => _lastFormattedSubtitle.DisplayText;
    public string FormatterEnabledText => _subtitleFormatterSettings.EnableSubtitleFormatter ? "true" : "false";
    public bool EnableImageHashSkip { get => _enableImageHashSkip; set { if (SetProperty(ref _enableImageHashSkip, value)) RefreshDiagnostics(); } }
    public bool EnableRegionDifferenceSkip { get => _enableRegionDifferenceSkip; set { if (SetProperty(ref _enableRegionDifferenceSkip, value)) RefreshDiagnostics(); } }
    /// <summary>Legacy alias — now controls <see cref="CaptureIntervalMs"/> directly.</summary>
    public int IntervalMs { get => CaptureIntervalMs; set => CaptureIntervalMs = value; }
    public int CaptureIntervalMs { get => _captureIntervalMs; set => SetProperty(ref _captureIntervalMs, Math.Max(20, value)); }
    public int MinOcrIntervalMs { get => _minOcrIntervalMs; set => SetProperty(ref _minOcrIntervalMs, Math.Max(0, value)); }
    public int MaxOcrIntervalMs { get => _maxOcrIntervalMs; set => SetProperty(ref _maxOcrIntervalMs, Math.Max(100, value)); }
    public bool ProcessLatestPendingFrameAfterOcr { get => _processLatestPendingFrameAfterOcr; set => SetProperty(ref _processLatestPendingFrameAfterOcr, value); }
    public int MaxOcrFrameBufferSize { get => _maxOcrFrameBufferSize; set => SetProperty(ref _maxOcrFrameBufferSize, Math.Clamp(value, 1, 20)); }
    public int MaxBufferedFrameAgeMs { get => _maxBufferedFrameAgeMs; set => SetProperty(ref _maxBufferedFrameAgeMs, Math.Max(250, value)); }
    /// <summary>Part D: when true, changed subtitle frames are queued in order instead
    /// of being replaced by the latest frame — fast dialogue is never skipped.</summary>
    public bool DoNotDropUnprocessedChangedFrames { get => _doNotDropUnprocessedChangedFrames; set => SetProperty(ref _doNotDropUnprocessedChangedFrames, value); }
    private int EffectiveOcrFrameBufferSize => _doNotDropUnprocessedChangedFrames ? _maxOcrFrameBufferSize : 1;
    public bool KeepOnlyLatestPendingOcrFrame { get => _keepOnlyLatestPendingOcrFrame; set => SetProperty(ref _keepOnlyLatestPendingOcrFrame, value); }
    public bool EnableFastFrameDifference { get => _enableFastFrameDifference; set => SetProperty(ref _enableFastFrameDifference, value); }
    public double FrameDifferenceThresholdPercent { get => _frameDifferenceThresholdPercent; set => SetProperty(ref _frameDifferenceThresholdPercent, Math.Clamp(value, 0, 100)); }
    public int ForceOcrEveryMs { get => _forceOcrEveryMs; set => SetProperty(ref _forceOcrEveryMs, Math.Max(100, value)); }
    public bool RunOcrOnEveryChangedFrame { get => _runOcrOnEveryChangedFrame; set => SetProperty(ref _runOcrOnEveryChangedFrame, value); }
    public int MinOverlayDisplayMs { get => _minOverlayDisplayMs; set => SetProperty(ref _minOverlayDisplayMs, Math.Max(0, value)); }
    /// <summary>Forwards to the shared <see cref="TranslationSettings.ShowSourceWhileTranslating"/>.</summary>
    public bool ShowSourceWhileTranslatingSetting
    {
        get => _translationSettings.ShowSourceWhileTranslating;
        set
        {
            if (_translationSettings.ShowSourceWhileTranslating == value) return;
            _translationSettings.ShowSourceWhileTranslating = value;
            OnPropertyChanged();
        }
    }
    public bool KeepPreviousSubtitleOnEmptyOcr { get => _keepPreviousSubtitleOnEmptyOcr; set => SetProperty(ref _keepPreviousSubtitleOnEmptyOcr, value); }
    public int PreviousSubtitleHoldMs { get => _previousSubtitleHoldMs; set => SetProperty(ref _previousSubtitleHoldMs, Math.Max(0, value)); }
    public bool EnableFastOcrMode
    {
        get => _enableFastOcrMode;
        set
        {
            if (!SetProperty(ref _enableFastOcrMode, value)) return;
            if (value)
            {
                // Live subtitle OCR: orientation detection is unnecessary overhead.
                UpscaleFactor = Math.Min(UpscaleFactor, 1.25);
            }
        }
    }

    // Part I — realtime performance diagnostics (read-only, worker-driven).
    public double CaptureFps { get => _captureFps; private set => SetProperty(ref _captureFps, value); }
    public long CaptureDurationMs { get => _captureDurationMs; private set => SetProperty(ref _captureDurationMs, value); }
    public long CropDurationMs { get => _cropDurationMs; private set => SetProperty(ref _cropDurationMs, value); }
    public long DifferenceDurationMs { get => _differenceDurationMs; private set => SetProperty(ref _differenceDurationMs, value); }
    public string OcrQueueState => _ocrWorker.IsBusy ? "busy" : (_ocrWorker.PendingFrameNumber is not null ? "pending" : "idle");
    public long? PendingOcrFrameNumber => _ocrWorker.PendingFrameNumber;
    public long LastProcessedOcrFrameNumber => _ocrWorker.LastProcessedFrameNumber;
    public long PendingFrameReplacedCount => _ocrWorker.PendingFrameReplacedCount;
    public long OcrStartedCount => _ocrWorker.OcrStartedCount;
    public long OcrCompletedCount => _ocrWorker.OcrCompletedCount;
    public long OcrSkippedNoChangeCount { get => _ocrSkippedNoChangeCount; private set => SetProperty(ref _ocrSkippedNoChangeCount, value); }
    public long OcrForcedCount { get => _ocrForcedCount; private set => SetProperty(ref _ocrForcedCount, value); }
    public long OcrDurationMs => _ocrWorker.LastOcrDurationMs;
    public long TimeFromCaptureToOverlayMs { get => _timeFromCaptureToOverlayMs; private set => SetProperty(ref _timeFromCaptureToOverlayMs, value); }
    public long TimeFromOcrToOverlayMs { get => _timeFromOcrToOverlayMs; private set => SetProperty(ref _timeFromOcrToOverlayMs, value); }
    public long TranslationPendingCount { get => _translationPendingCount; private set => SetProperty(ref _translationPendingCount, value); }
    public long TranslationDurationMs => _pipelineDiagnostics.LastTranslationDurationMs;
    public int FinalOcrCropWidth { get => _finalOcrCropWidth; private set => SetProperty(ref _finalOcrCropWidth, value); }
    public int FinalOcrCropHeight { get => _finalOcrCropHeight; private set => SetProperty(ref _finalOcrCropHeight, value); }
    public int FinalOcrCropArea => _finalOcrCropWidth * _finalOcrCropHeight;
    public string CropSizeWarningText { get => _cropSizeWarningText; private set => SetProperty(ref _cropSizeWarningText, value); }
    public string RealtimeDiagnosticsText => string.Join(Environment.NewLine,
    [
        $"Capture FPS: {CaptureFps:F1}",
        $"Capture interval: {CaptureIntervalMs} ms",
        $"Capture duration: {CaptureDurationMs} ms",
        $"Crop duration: {CropDurationMs} ms",
        $"Difference duration: {DifferenceDurationMs} ms",
        $"OCR queue: {OcrQueueState}",
        $"OCR busy: {IsOcrBusy}",
        $"Pending frame: {PendingOcrFrameNumber?.ToString() ?? "-"}",
        $"Last processed frame: {LastProcessedOcrFrameNumber}",
        $"Pending replacements: {PendingFrameReplacedCount}",
        $"OCR started/completed: {OcrStartedCount}/{OcrCompletedCount}",
        $"OCR skipped no change: {OcrSkippedNoChangeCount}",
        $"OCR forced: {OcrForcedCount}",
        $"OCR duration: {OcrDurationMs} ms",
        $"Capture to overlay: {TimeFromCaptureToOverlayMs} ms",
        $"OCR to overlay: {TimeFromOcrToOverlayMs} ms",
        $"Translation pending: {_pipelineDiagnostics.TranslationQueueCount}",
        $"Translation duration: {TranslationDurationMs} ms",
        $"Final crop: {FinalOcrCropWidth}x{FinalOcrCropHeight} ({FinalOcrCropArea:N0} px)",
    ]);
    // ── Ordered subtitle pipeline diagnostics (Part K) / Turkish-only mode toggle (Part A) ──

    public bool TurkishOnlyMode
    {
        get => _translationSettings.TurkishOnlyMode;
        set
        {
            if (_translationSettings.TurkishOnlyMode == value) return;
            _translationSettings.TurkishOnlyMode = value;
            OnPropertyChanged();
        }
    }

    public string OrderedPipelineDiagnosticsText => string.Join(Environment.NewLine,
    [
        $"TurkishOnlyMode: {_translationSettings.TurkishOnlyMode}",
        $"Captured queue count: {_pipelineDiagnostics.CapturedQueueCount}",
        $"Translation queue count: {_pipelineDiagnostics.OrderedTranslationQueueCount}",
        $"Playback queue count: {_pipelineDiagnostics.PlaybackQueueCount}",
        $"Last captured source: {_pipelineDiagnostics.LastCapturedSourceText}",
        $"Last translated Turkish: {_pipelineDiagnostics.LastTranslatedTurkishText}",
        $"Last displayed Turkish: {_pipelineDiagnostics.LastDisplayedTurkishText}",
        $"Duplicate ignored count: {_pipelineDiagnostics.DuplicateSubtitleIgnoredCount} (last reason: {_pipelineDiagnostics.LastDedupReason})",
        $"Invalid rejected count: {_pipelineDiagnostics.RejectedBeforeQueueCount} " +
            $"(last: '{_pipelineDiagnostics.LastRejectedSubtitleCandidate}', reason: {_pipelineDiagnostics.LastRejectedReason})",
        $"Accepted candidate count: {_pipelineDiagnostics.AcceptedSubtitleCandidateCount}",
        $"Memory hits: {_pipelineDiagnostics.MemoryHitCount}",
        $"Cache hits: {_pipelineDiagnostics.CacheHitCount}",
        $"In-flight hits: {_pipelineDiagnostics.InFlightHitCount}",
        $"Actual OPUS calls: {_pipelineDiagnostics.ActualOpusCallCount}",
        $"Average OPUS duration: {_pipelineDiagnostics.AverageOpusDurationMs:F0} ms",
        $"Average capture-to-display latency: {_pipelineDiagnostics.AverageCaptureToDisplayLatencyMs:F0} ms",
        $"Late translation count: {_pipelineDiagnostics.TranslationLateCompletedCount}",
        $"Expired skipped count: {_pipelineDiagnostics.ExpiredSkippedCount}",
        $"Last overlay update source: {_pipelineDiagnostics.LastOverlayUpdateSource}",
    ]);

    public int OcrRequestTimeoutMs { get => _ocrRequestTimeoutMs; set => SetProperty(ref _ocrRequestTimeoutMs, Math.Max(500, value)); }
    public int ForceOcrEveryNFrames { get => _forceOcrEveryNFrames; set => SetProperty(ref _forceOcrEveryNFrames, Math.Max(1, value)); }
    public int ForceOcrEveryMilliseconds { get => _forceOcrEveryMilliseconds; set => SetProperty(ref _forceOcrEveryMilliseconds, Math.Max(100, value)); }
    public double RegionChangeThresholdPercent { get => _regionChangeThresholdPercent; set => SetProperty(ref _regionChangeThresholdPercent, Math.Clamp(value, 0, 100)); }
    public int ClearOverlayAfterEmptyOcrCount { get => _clearOverlayAfterEmptyOcrCount; set => SetProperty(ref _clearOverlayAfterEmptyOcrCount, Math.Max(1, value)); }
    /// <summary>Legacy alias — now controls <see cref="KeepPreviousSubtitleOnEmptyOcr"/> directly.</summary>
    public bool KeepPreviousTextOnEmptyOcr { get => KeepPreviousSubtitleOnEmptyOcr; set => KeepPreviousSubtitleOnEmptyOcr = value; }
    public bool SaveDebugFrames { get => _saveDebugFrames; set => SetProperty(ref _saveDebugFrames, value); }
    public bool EnableOcrPreprocessing { get => _enableOcrPreprocessing; set => SetProperty(ref _enableOcrPreprocessing, value); }
    public double UpscaleFactor { get => _upscaleFactor; set => SetProperty(ref _upscaleFactor, Math.Clamp(value, 1, 4)); }
    public bool ConvertToGrayscale { get => _convertToGrayscale; set => SetProperty(ref _convertToGrayscale, value); }
    public bool IncreaseContrast { get => _increaseContrast; set => SetProperty(ref _increaseContrast, value); }
    public bool SharpenImage { get => _sharpenImage; set => SetProperty(ref _sharpenImage, value); }
    public string ThresholdMode { get => _thresholdMode; set => SetProperty(ref _thresholdMode, string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().ToLowerInvariant()); }
    public int ThresholdModeIndex
    {
        get => _thresholdMode switch { "otsu" => 1, "fixed" => 2, _ => 0 };
        set
        {
            ThresholdMode = value switch { 1 => "otsu", 2 => "fixed", _ => "none" };
            OnPropertyChanged();
        }
    }
    public bool EnableCharWhitelist { get => _enableCharWhitelist; set => SetProperty(ref _enableCharWhitelist, value); }
    public bool AutoStartOverlay { get => _autoStartOverlay; set => SetProperty(ref _autoStartOverlay, value); }
    public bool PauseWhenWindowNotActive { get => _pauseWhenWindowNotActive; set => SetProperty(ref _pauseWhenWindowNotActive, value); }
    public int CropPaddingLeft { get => _cropPaddingLeft; set => SetProperty(ref _cropPaddingLeft, Math.Max(0, value)); }
    public int CropPaddingTop { get => _cropPaddingTop; set => SetProperty(ref _cropPaddingTop, Math.Max(0, value)); }
    public int CropPaddingRight { get => _cropPaddingRight; set => SetProperty(ref _cropPaddingRight, Math.Max(0, value)); }
    public int CropPaddingBottom { get => _cropPaddingBottom; set => SetProperty(ref _cropPaddingBottom, Math.Max(0, value)); }
    public double MaxLineRightPercent { get => _maxLineRightPercent; set => SetProperty(ref _maxLineRightPercent, Math.Clamp(value, 0.1, 1)); }

    public ICommand StartMonitoringCommand => _startCommand;
    public ICommand StopMonitoringCommand => _stopCommand;
    public ICommand RunSingleTickCommand { get; }
    public ICommand RunOcrOnCurrentPreviewCommand { get; }
    public ICommand ToggleNeverSkipOcrCommand { get; }
    public ICommand ShowTestOverlayCommand { get; }
    public ICommand ShowTestOverlayLongTextCommand { get; }
    public ICommand TestSubtitleFilteringCommand { get; }
    public ICommand StartFastDialogueStressTestCommand { get; }
    public ICommand RunPipelineDoctorCommand { get; }

    public string PipelineDoctorReportText
    {
        get => _pipelineDoctorReportText;
        private set => SetProperty(ref _pipelineDoctorReportText, value);
    }

    /// <summary>
    /// One-click staged diagnosis of the whole pipeline:
    /// OCR engine → OCR server → translation provider → overlay → subtitle
    /// replace → end-to-end. Every stage reports PASS / FAIL / WARNING, and
    /// every FAIL comes with a concrete fix suggestion.
    /// </summary>
    private async Task RunPipelineDoctorAsync()
    {
        PipelineDoctorReportText = "Running pipeline doctor…";
        var lines = new List<string>();

        void Stage(string name, string verdict, string detail, string? fix = null)
        {
            lines.Add($"{verdict,-10} {name,-22} {detail}");
            if (fix is not null) lines.Add($"           ↳ Fix: {fix}");
        }

        try
        {
            // 1 — OCR engine (selected engine installed + provider usable)
            var selectedEngine = _ocrEngineSettings.PreferredProvider;
            var installState = await _ocrEngineInstallService.RefreshStateAsync(selectedEngine);
            var (ocrProvider, ocrReason) = _ocrProviderFactory.GetBest(selectedEngine);
            if (installState is OcrEngineInstallState.NotInstalled or OcrEngineInstallState.Failed)
                Stage("OCR engine", "❌ FAIL", $"{selectedEngine} is not installed.",
                    $"Install {selectedEngine} on the OCR Server tab (Install button), or select another engine.");
            else if (ocrProvider is null)
                Stage("OCR engine", "❌ FAIL", ocrReason,
                    "Start the OCR server or select an installed engine on the OCR tab.");
            else
                Stage("OCR engine", "✅ PASS", $"{selectedEngine} → {ocrProvider.Name}");

            // 2 — OCR server (only relevant for server-backed engines)
            var needsServer = OcrServerService.IsServerBackedProvider(selectedEngine);
            if (!needsServer)
                Stage("OCR server", "⚠ WARN", $"{selectedEngine} does not use the local OCR server (native engine).");
            else if (_ocrServerService.IsRunning)
                Stage("OCR server", "✅ PASS", $"Running at {_ocrServerService.ServerBaseUrl}");
            else
            {
                var (started, startMessage) = await _ocrServerService.EnsureRunningAsync();
                if (started)
                    Stage("OCR server", "✅ PASS", "Was stopped — auto-started successfully.");
                else
                    Stage("OCR server", "❌ FAIL", startMessage,
                        "Check Python installation (Settings tab) and debug\\last_ocr_server_state.json.");
            }

            // 3-5 — translation provider / overlay / mask region via the health service
            var health = await _pipelineHealthService.RunHealthCheckAsync();

            Stage("Translation provider", health.TranslationProviderOk ? "✅ PASS" : "❌ FAIL",
                health.TranslationProviderOk
                    ? $"Selected: {_translationSettings.ProviderType}"
                    : $"{_translationSettings.ProviderType} is not operational.",
                health.TranslationProviderOk ? null
                    : "Run 'Check Providers' on the Translation tab; start the translation server or the selected provider's app.");

            Stage("Overlay", health.ReplacementOverlayOk ? "✅ PASS" : "❌ FAIL",
                health.ReplacementOverlayOk ? "Replacement overlay configured." : "Overlay is not configured for replacement mode.",
                health.ReplacementOverlayOk ? null
                    : "Overlay tab → set display mode to 'Subtitle Replacement Overlay' and open the overlay.");

            Stage("Subtitle replace", health.ManualReplacementRegionOk ? "✅ PASS" : "❌ FAIL",
                health.ManualReplacementRegionOk ? "Manual mask region is valid." : "Manual mask region is not selected/too small.",
                health.ManualReplacementRegionOk ? null
                    : "Overlay tab → select the subtitle mask region over the game's subtitle area (min 100×30).");

            if (_translationSettings.EnableTranslationProviderFallback &&
                _pipelineDiagnostics.LastTranslationWasFallbackUsed)
                Stage("Fallback", "⚠ WARN",
                    $"Last translation used a FALLBACK provider: {_pipelineDiagnostics.LastTranslationFallbackReason}");

            // 6 — end-to-end
            var endToEnd = await _pipelineHealthService.RunEndToEndPipelineTestAsync();
            Stage("End-to-end", endToEnd.Success ? "✅ PASS" : "❌ FAIL",
                endToEnd.Success
                    ? $"\"{endToEnd.DialogueText}\" → \"{endToEnd.PostProcessedTranslation}\" via {endToEnd.ProviderUsed}"
                    : $"Failed at {endToEnd.FailureStage}: {endToEnd.FailureReason}",
                endToEnd.Success ? null : endToEnd.FailureStage switch
                {
                    "manual_replacement_region" => "Select the mask region on the Overlay tab.",
                    "translation_provider" => "Start/repair the selected translation provider (Translation tab → Check Providers).",
                    "display_routing" => "Open the overlay window and ensure Turkish-only replacement mode is active.",
                    "candidate_validation" => "The test line was rejected by the subtitle filter — check filter settings.",
                    _ => "See debug\\last_pipeline_trace.json for the full trace.",
                });

            if (!health.TurkishDisplayedOk && !endToEnd.Success)
                Stage("Turkish displayed", "⚠ WARN", "No Turkish text has been displayed yet in this session.");

            lines.Add("Details: debug\\end_to_end_pipeline_test.json, debug\\last_pipeline_trace.json");
            PipelineDoctorReportText = string.Join(Environment.NewLine, lines);
        }
        catch (Exception exception)
        {
            PipelineDoctorReportText = $"Pipeline doctor failed: {exception.Message}";
            _logger.LogError(exception, "pipeline_doctor_failed");
        }
    }

    // ── Subtitle line filtering settings (Part C) ────────────────────────────────

    public bool EnableSubtitleLineFiltering
    {
        get => _subtitleFilterSettings.EnableSubtitleLineFiltering;
        set
        {
            if (_subtitleFilterSettings.EnableSubtitleLineFiltering == value) return;
            _subtitleFilterSettings.EnableSubtitleLineFiltering = value;
            OnPropertyChanged();
        }
    }

    /// <summary>0=FullCrop, 1=UpperBand, 2=CenterBand, 3=Custom (ComboBox index).</summary>
    public int SubtitleBandModeIndex
    {
        get => (int)_subtitleFilterSettings.SubtitleBandMode;
        set
        {
            var mode = (SubtitleBandMode)Math.Clamp(value, 0, 3);
            if (_subtitleFilterSettings.SubtitleBandMode == mode) return;
            _subtitleFilterSettings.SubtitleBandMode = mode;
            OnPropertyChanged();
        }
    }

    public double SubtitleBandTopPercent
    {
        get => _subtitleFilterSettings.SubtitleBandTopPercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(_subtitleFilterSettings.SubtitleBandTopPercent - clamped) < 0.0001) return;
            _subtitleFilterSettings.SubtitleBandTopPercent = clamped;
            OnPropertyChanged();
        }
    }

    public double SubtitleBandBottomPercent
    {
        get => _subtitleFilterSettings.SubtitleBandBottomPercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(_subtitleFilterSettings.SubtitleBandBottomPercent - clamped) < 0.0001) return;
            _subtitleFilterSettings.SubtitleBandBottomPercent = clamped;
            OnPropertyChanged();
        }
    }

    public bool ShowRejectedHudLines
    {
        get => _subtitleFilterSettings.ShowRejectedHudLines;
        set
        {
            if (_subtitleFilterSettings.ShowRejectedHudLines == value) return;
            _subtitleFilterSettings.ShowRejectedHudLines = value;
            OnPropertyChanged();
        }
    }

    public bool ShowSelectedSubtitleLines
    {
        get => _subtitleFilterSettings.ShowSelectedSubtitleLines;
        set
        {
            if (_subtitleFilterSettings.ShowSelectedSubtitleLines == value) return;
            _subtitleFilterSettings.ShowSelectedSubtitleLines = value;
            OnPropertyChanged();
        }
    }

    /// <summary>0=Default English Game, 1=Red Dead Redemption 2 (ComboBox index).</summary>
    public int ActiveGameProfileIndex
    {
        get
        {
            var profiles = _gameProfileRepository.Profiles;
            for (var i = 0; i < profiles.Count; i++)
                if (string.Equals(profiles[i].Name, _subtitleFilterSettings.ActiveGameProfileName,
                        StringComparison.OrdinalIgnoreCase))
                    return i;
            return 0;
        }
        set
        {
            var profiles = _gameProfileRepository.Profiles;
            var index = Math.Clamp(value, 0, profiles.Count - 1);
            var name = profiles[index].Name;
            if (string.Equals(_subtitleFilterSettings.ActiveGameProfileName, name, StringComparison.OrdinalIgnoreCase))
                return;
            _subtitleFilterSettings.ActiveGameProfileName = name;
            OnPropertyChanged();
            _logger.LogInformation("subtitle_filter_profile_selected - {Profile}", name);
        }
    }

    public string FilterTestLine1 { get => _filterTestLine1; set => SetProperty(ref _filterTestLine1, value); }
    public string FilterTestLine2 { get => _filterTestLine2; set => SetProperty(ref _filterTestLine2, value); }
    public string FilterTestResultText { get => _filterTestResultText; private set => SetProperty(ref _filterTestResultText, value); }
    public string SelectedSubtitleLinesText { get => _selectedSubtitleLinesText; private set => SetProperty(ref _selectedSubtitleLinesText, value); }
    public string RejectedHudLinesText { get => _rejectedHudLinesText; private set => SetProperty(ref _rejectedHudLinesText, value); }

    private async Task StartMonitoringAsync()
    {
        var window = _captureViewModel.SelectedWindow;
        if (window is null)
        {
            SetError("No window selected. Select a window in the Capture tab first.");
            return;
        }

        if (IsMonitoring) return;

        ResetState();
        ClearError();
        IsMonitoring = true;
        MonitoringStatusText = "Running";
        TargetWindowText = $"{window.Title} (PID {window.ProcessId})";

        var isReplacementMode =
            _overlayService.CurrentSettings.DisplayMode == SubtitleDisplayMode.SubtitleReplacementOverlay;

        // Replacement mode is unusable without the overlay window and requires
        // TurkishOnlyMode — force both so persisted settings can never desync
        // into a state where every translation is silently suppressed.
        if (isReplacementMode && !_translationSettings.TurkishOnlyMode)
        {
            _translationSettings.TurkishOnlyMode = true;
            _logger.LogWarning("turkish_only_mode_forced_on - replacement display mode requires it");
        }

        if ((AutoStartOverlay || isReplacementMode) && !_overlayService.IsOpen)
        {
            try { _overlayService.Open(await _overlayMonitorCoordinator.LoadAndValidateAsync()); }
            catch (Exception exception) { _logger.LogWarning(exception, "Failed to auto-start overlay"); }
        }

        var monitoringCancellation = new CancellationTokenSource();
        var monitoringToken = monitoringCancellation.Token;
        var monitoringTask = Task.Run(
            () => MonitoringLoopAsync(window, monitoringToken),
            monitoringToken);

        lock (_monitoringLifetimeLock)
        {
            _monitoringCancellation = monitoringCancellation;
            _monitoringTask = monitoringTask;
        }

        _logger.LogInformation("Monitoring started for {WindowTitle}", window.Title);
    }

    private async Task StopMonitoringAsync()
    {
        if (!IsMonitoring) return;

        CancellationTokenSource? monitoringCancellation;
        Task? monitoringTask;
        lock (_monitoringLifetimeLock)
        {
            monitoringCancellation = _monitoringCancellation;
            monitoringTask = _monitoringTask;
            _monitoringCancellation = null;
            _monitoringTask = null;
        }

        try
        {
            monitoringCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Stop can race with shutdown/disposal; a disposed CTS already means
            // the loop cannot be signalled through this source anymore.
        }

        if (monitoringTask is not null)
        {
            try { await monitoringTask; }
            catch (OperationCanceledException) { }
        }

        try
        {
            monitoringCancellation?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Idempotent shutdown path.
        }

        IsMonitoring = false;
        MonitoringStatusText = "Stopped";
        RefreshDiagnostics();
    }

    public void StopIfRunning()
    {
        if (IsMonitoring) StopMonitoringAsync().GetAwaiter().GetResult();
    }

    private async Task MonitoringLoopAsync(CapturedWindow window, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var loopTimer = Stopwatch.StartNew();
                try
                {
                    await RunPipelineTickAsync(window, manualForce: false, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Unexpected monitoring loop failure");
                    _lastDecision = "error_capture_failed";
                    Post(() => SetError(exception.Message));
                }

                loopTimer.Stop();
                _lastTotalLoopDurationMs = loopTimer.ElapsedMilliseconds;
                RefreshDiagnostics();
                await SaveMonitoringStateAsync(CancellationToken.None);

                try { await Task.Delay(IntervalMs, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            Post(() =>
            {
                IsMonitoring = false;
                MonitoringStatusText = "Stopped";
            });
        }
    }

    private async Task RunPipelineTickAsync(
        CapturedWindow window,
        bool manualForce,
        CancellationToken cancellationToken)
    {
        _frameNumber++;
        _pipelineDiagnostics.LastFrameNumber = _frameNumber;
        _pipelineDiagnostics.LastCaptureSucceeded = false;
        _pipelineDiagnostics.LastCropSucceeded = false;
        _pipelineDiagnostics.TranslationEnabled = _translationSettings.EnableTranslation;
        _pipelineDiagnostics.TranslationDisplayMode = _translationSettings.DisplayMode;

        var now = DateTimeOffset.Now;
        if (_lastTickAt != DateTimeOffset.MinValue)
        {
            var deltaMs = (now - _lastTickAt).TotalMilliseconds;
            if (deltaMs > 0) CaptureFps = 1000.0 / deltaMs;
        }
        _lastTickAt = now;

        if (!IsWindow(window.Handle) || IsIconic(window.Handle))
        {
            _lastDecision = "error_capture_failed";
            Post(() => MonitoringStatusText = IsIconic(window.Handle)
                ? "Running (window minimized)"
                : "Error - window closed");
            RefreshDiagnostics();
            return;
        }

        if (_pauseWhenWindowNotActive && GetForegroundWindow() != window.Handle)
        {
            _lastDecision = "paused_window_not_active";
            Post(() => MonitoringStatusText = "Duraklatıldı (pencere arka planda)");
            return;
        }

        // 1. Capture selected window. This is the ONLY step the fast capture
        // loop waits on — OCR itself runs independently in OcrWorker.
        byte[] fullCapture;
        var captureTimer = Stopwatch.StartNew();
        try
        {
            fullCapture = await _captureService.CaptureAsync(window, cancellationToken: cancellationToken);
            captureTimer.Stop();
            CaptureDurationMs = captureTimer.ElapsedMilliseconds;
            _latestFullCapture = fullCapture;
            _pipelineDiagnostics.LastCaptureSucceeded = true;
            Post(() =>
            {
                LastCaptureTimeText = DateTimeOffset.Now.ToString("HH:mm:ss.fff");
                MonitoringStatusText = "Running";
                ClearError();
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _lastDecision = "error_capture_failed";
            _previousOcrFailed = true;
            Post(() => SetError($"Capture failed: {exception.Message}"));
            _logger.LogError(exception, "Capture failed");
            RefreshDiagnostics();
            return;
        }

        var savedRegion = await _regionService.LoadAsync(cancellationToken);
        if (savedRegion is null)
        {
            _lastDecision = "error_capture_failed";
            Post(() => SetError("No OCR region has been saved."));
            RefreshDiagnostics();
            return;
        }

        // 2-4. Crop the saved OCR region + padding.
        var cropTimer = Stopwatch.StartNew();
        var regionCrop = await _cropService.CropAsync(fullCapture, savedRegion, cancellationToken);
        _latestRegionCrop = regionCrop;

        var relativePaddedRegion = CreateRelativePaddedRegion(savedRegion);
        var finalCrop = await _cropService.CropAsync(regionCrop, relativePaddedRegion, cancellationToken);
        _latestFinalCrop = finalCrop;
        cropTimer.Stop();
        CropDurationMs = cropTimer.ElapsedMilliseconds;
        _pipelineDiagnostics.LastCropSucceeded = true;

        // 5. Preview final crop, then preprocess and preview the exact OCR image.
        var ocrImage = EnableOcrPreprocessing ? PreprocessImage(finalCrop) : finalCrop;
        _latestOcrImage = ocrImage;
        UpdatePreview(finalCrop, isOcrImage: false);
        UpdatePreview(ocrImage, isOcrImage: true);
        UpdateCropSizeDiagnostics(ocrImage);

        if (SaveDebugFrames &&
            (now - _lastDebugFrameSavedAt).TotalMilliseconds >= 1000)
        {
            await SaveDebugImagesAsync(fullCapture, regionCrop, finalCrop, cancellationToken);
            _lastDebugFrameSavedAt = now;
        }

        // 6. Decide whether the crop changed enough to warrant OCR. The legacy
        // hash/full-resolution-diff decision (DecideOcr) still runs so existing
        // toggles keep working; the new fast downscaled diff is an additional,
        // much cheaper signal used to react to short-lived dialogue quickly.
        var imageHash = OcrResultCache.ComputeImageHash(ocrImage);
        var isFirstFrame = _previousOcrImage is null;
        var hashChanged = !string.Equals(imageHash, _lastImageHash, StringComparison.Ordinal);
        var legacyDifference = isFirstFrame || EnableFastFrameDifference
            ? null
            : TryComputePixelDifferencePercent(_previousOcrImage!, ocrImage);
        _lastRegionDifferencePercent = legacyDifference;

        var differenceTimer = Stopwatch.StartNew();
        double? fastDiffPercent = null;
        if (EnableFastFrameDifference && !isFirstFrame && _previousFastDiffImage is not null)
        {
            fastDiffPercent = _fastFrameDifferenceService.ComputeDifferencePercent(
                _previousFastDiffImage, ocrImage);
        }
        differenceTimer.Stop();
        DifferenceDurationMs = differenceTimer.ElapsedMilliseconds;

        var legacyDecision = DecideOcr(manualForce, isFirstFrame, hashChanged, legacyDifference);

        _lastImageHash = imageHash;
        _previousOcrImage = ocrImage;
        _previousFastDiffImage = ocrImage;

        var msSinceLastOcrStart = _ocrWorker.LastOcrStartedAt is { } lastStart
            ? (now - lastStart).TotalMilliseconds
            : double.PositiveInfinity;
        var effectiveForceIntervalMs = Math.Min(MaxOcrIntervalMs, ForceOcrEveryMs);
        var forcedByInterval = !isFirstFrame && msSinceLastOcrStart >= effectiveForceIntervalMs;

        var fastDiffChanged = RunOcrOnEveryChangedFrame && EnableFastFrameDifference &&
            (fastDiffPercent is null || fastDiffPercent >= FrameDifferenceThresholdPercent);

        var legacyRequestsOcr = !EnableFastFrameDifference &&
            legacyDecision.StartsWith("run_", StringComparison.Ordinal);
        var shouldSubmit =
            manualForce ||
            isFirstFrame ||
            legacyRequestsOcr ||
            forcedByInterval ||
            fastDiffChanged;

        _lastDecision = shouldSubmit
            ? manualForce ? "run_manual_force"
                : isFirstFrame ? "run_first_frame"
                : forcedByInterval ? "run_forced_by_interval"
                : fastDiffChanged ? "run_fast_diff_changed"
                : legacyDecision
            : "skip_no_change";

        if (!shouldSubmit)
        {
            _skippedCount++;
            _lastSkipReason = _lastDecision;
            OcrSkippedNoChangeCount++;
            RefreshDiagnostics();
            await SaveRealtimePipelineStateAsync(cancellationToken);
            return;
        }

        if (forcedByInterval && !manualForce && !isFirstFrame) OcrForcedCount++;

        var (secondarySpeakerImage, secondarySpeakerLabel) = await CreateSecondarySpeakerCropAsync(fullCapture, cancellationToken);
        var frame = new PendingOcrFrame
        {
            ImageBytes = ocrImage,
            SecondarySpeakerImageBytes = secondarySpeakerImage,
            SecondarySpeakerRegionLabel = secondarySpeakerLabel,
            FrameNumber = _frameNumber,
            Reason = _lastDecision,
            CapturedAt = now,
            IsForced = forcedByInterval,
            CropHash = imageHash,
            WindowLeft = window.Left,
            WindowTop = window.Top,
            WindowWidth = window.Width,
            WindowHeight = window.Height,
            SavedRegionX = savedRegion.X,
            SavedRegionY = savedRegion.Y,
            SavedRegionWidth = savedRegion.Width,
            SavedRegionHeight = savedRegion.Height,
            FinalCropOffsetX = savedRegion.X + relativePaddedRegion.X,
            FinalCropOffsetY = savedRegion.Y + relativePaddedRegion.Y,
            FinalCropWidth = Math.Max(1, savedRegion.Width - CropPaddingLeft - CropPaddingRight),
            FinalCropHeight = Math.Max(1, savedRegion.Height - CropPaddingTop - CropPaddingBottom),
            OcrImageWidth = FinalOcrCropWidth,
            OcrImageHeight = FinalOcrCropHeight,
        };

        _ocrWorker.Configure(
            OcrRequestTimeoutMs,
            MinOcrIntervalMs,
            ProcessLatestPendingFrameAfterOcr,
            EffectiveOcrFrameBufferSize,
            MaxBufferedFrameAgeMs);

        if (manualForce)
        {
            // Debug buttons feel synchronous: wait for exactly this frame's result.
            await SubmitAndWaitAsync(frame, cancellationToken);
        }
        else
        {
            // Live monitoring: never block the capture loop on OCR — the worker
            // processes this in the background and raises Completed when done.
            _ocrWorker.SubmitFrame(frame);
            _logger.LogInformation(
                "stage_latency - stage=capture frame={Frame} capture_ms={CaptureMs} crop_ms={CropMs} diff_ms={DiffMs}",
                frame.FrameNumber, CaptureDurationMs, CropDurationMs, DifferenceDurationMs);
        }

        RefreshDiagnostics();
        await SaveRealtimePipelineStateAsync(cancellationToken);
    }

    private async Task<OcrWorkResult> SubmitAndWaitAsync(
        PendingOcrFrame frame, CancellationToken cancellationToken)
    {
        var completionSource = new TaskCompletionSource<OcrWorkResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(OcrWorkResult result)
        {
            if (result.Frame.FrameNumber == frame.FrameNumber)
                completionSource.TrySetResult(result);
        }

        _ocrWorker.Completed += OnCompleted;
        try
        {
            _ocrWorker.SubmitFrame(frame);
            using var registration = cancellationToken.Register(
                () => completionSource.TrySetCanceled(cancellationToken));
            return await completionSource.Task.ConfigureAwait(false);
        }
        finally
        {
            _ocrWorker.Completed -= OnCompleted;
        }
    }

    private void UpdateCropSizeDiagnostics(byte[] ocrImage)
    {
        try
        {
            using var stream = new MemoryStream(ocrImage);
            using var bitmap = new Bitmap(stream);
            FinalOcrCropWidth = bitmap.Width;
            FinalOcrCropHeight = bitmap.Height;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not read final OCR crop dimensions");
            return;
        }

        const int recommendedMaxWidth = 1300;
        const int recommendedMaxHeight = 260;

        CropSizeWarningText = FinalOcrCropWidth > recommendedMaxWidth || FinalOcrCropHeight > recommendedMaxHeight
            ? "OCR crop is large. Smaller subtitle-only region improves speed."
            : string.Empty;
    }

    // ── OCR worker completion (background thread) ────────────────────────────────

    private void OnOcrWorkerCompleted(OcrWorkResult workResult)
    {
        var frame = workResult.Frame;
        _lastOcrFrameNumber = frame.FrameNumber;
        _lastOcrStartedAt = workResult.StartedAt;
        _lastOcrFinishedAt = workResult.FinishedAt;
        _lastOcrDurationMs = workResult.DurationMs;
        _pipelineDiagnostics.LastOcrStartedAt = workResult.StartedAt;
        _pipelineDiagnostics.LastOcrFinishedAt = workResult.FinishedAt;
        _pipelineDiagnostics.LastOcrDurationMs = workResult.DurationMs;
        _lastOcrRunReason = frame.Reason;

        if (!workResult.Success || workResult.Result is null)
        {
            _pipelineDiagnostics.LastOcrError = workResult.ErrorMessage ?? "OCR failed.";
            _previousOcrSucceeded = false;
            _previousOcrFailed = true;
            _lastDecision = "error_ocr_failed";
            Post(() => SetError(workResult.ErrorMessage ?? "OCR failed."));
            _logger.LogWarning("OCR failed for frame {Frame}: {Error}", frame.FrameNumber, workResult.ErrorMessage);
            RefreshDiagnostics();
            _ = SaveRealtimePipelineStateAsync(CancellationToken.None);
            return;
        }

        var result = workResult.Result;
        _lastOcrRawText = result.Text;
        _lastOcrConfidence = result.Confidence;
        _pipelineDiagnostics.LastOcrError = string.Empty;

        // OCR Line Classifier + SubtitleCandidateSelector: keep dialogue,
        // drop tutorial/HUD prompt lines before cleaning and translating.
        _lastLineSelection = ClassifyOcrLines(result, frame.ImageBytes);
        var subtitleSourceText = _lastLineSelection.FilteringApplied
            ? _lastLineSelection.SelectedText     // empty when only HUD text was found
            : result.Text;
        _ = SaveLineFilteringDiagnosticsAsync(result, _lastLineSelection, CancellationToken.None);

        _lastOcrCleanedText = _textCleaner.Clean(subtitleSourceText);
        if (_enableCharWhitelist && !string.IsNullOrEmpty(_lastOcrCleanedText))
            _lastOcrCleanedText = System.Text.RegularExpressions.Regex.Replace(
                _lastOcrCleanedText,
                @"[^A-Za-z0-9 .,!?;:'""\(\)\[\]\-\–\—\…\n]",
                string.Empty);
        _lastFormattedSubtitle = _subtitleFormatter
            .FormatAsync(_lastOcrCleanedText, _lastOcrConfidence, CancellationToken.None)
            .GetAwaiter().GetResult();
        var secondarySpeaker = ResolveSecondarySpeakerNameAsync(workResult.Frame, _lastFormattedSubtitle.MainText)
            .GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(secondarySpeaker))
            _lastFormattedSubtitle = WithSpeakerName(_lastFormattedSubtitle, secondarySpeaker);
        _ = SaveFormattedSubtitleAsync(_lastFormattedSubtitle, CancellationToken.None);
        _ = SaveSpeakerDetectionAsync(_lastFormattedSubtitle, CancellationToken.None);
        _pipelineDiagnostics.LastOcrRawText = _lastOcrRawText;
        _pipelineDiagnostics.LastOcrCleanedText = _lastOcrCleanedText;
        _pipelineDiagnostics.LastOcrConfidence = _lastOcrConfidence;
        _pipelineDiagnostics.LastOcrWasEmpty = _lastFormattedSubtitle.IsEmpty;
        _previousOcrSucceeded = true;
        _previousOcrFailed = false;

        Post(() => LastOcrTimeText = workResult.FinishedAt.ToString("HH:mm:ss.fff"));

        if (_lastFormattedSubtitle.IsEmpty)
        {
            // Diagnostic: distinguish "the line classifier/band dropped every
            // real line" (selectedRaw empty) from "a real line survived but the
            // formatter/speaker detector emptied it" (selectedRaw non-empty).
            _logger.LogInformation(
                "empty_after_format - selectedRaw=\"{Selected}\" cleaned=\"{Cleaned}\" speaker=\"{Speaker}\" filteringApplied={Applied}",
                Truncate(subtitleSourceText), Truncate(_lastOcrCleanedText),
                _lastFormattedSubtitle.SpeakerName, _lastLineSelection.FilteringApplied);
            HandleEmptyOcrResult(frame);
            RefreshDiagnostics();
            return;
        }

        _emptyOcrCount = 0;
        _previousOcrWasEmpty = false;
        _lastEmptyOcrAt = null;

        Post(() => LastDetectedText = _lastFormattedSubtitle.DisplayText);
        _pipelineDiagnostics.CurrentSubtitleSourceText = _lastFormattedSubtitle.MainText;
        var replacementContext = BuildReplacementContext(frame, _lastLineSelection);

        if (_translationSettings.TurkishOnlyMode)
        {
            // Ordered pipeline owns validation, queueing, translation and the
            // overlay exclusively. OCR must never write English to the overlay
            // directly in this mode (Part G).
            _orderedSubtitlePipeline.Submit(_lastFormattedSubtitle, _frameNumber, replacementContext);
            _pipelineDiagnostics.CapturedQueueCount = _orderedSubtitlePipeline.GetCaptureSnapshot().Count;
            _pipelineDiagnostics.OrderedTranslationQueueCount = _orderedSubtitlePipeline.DispatchQueueCount;
        }
        else
        {
            var overlayUpdate = _subtitleDisplayStateManager
                .HandleSourceAsync(_lastFormattedSubtitle, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            ApplyDisplayUpdate(overlayUpdate);

            if (overlayUpdate.ShouldEnqueueTranslation &&
                !string.IsNullOrWhiteSpace(_lastFormattedSubtitle.MainText))
            {
                _subtitleTranslationQueue.Enqueue(_lastFormattedSubtitle);
            }
            else
            {
                _pipelineDiagnostics.LastTranslationQueueStatus = "disabled";
                _pipelineDiagnosticsStore.Save();
            }
        }

        TimeFromCaptureToOverlayMs = Math.Max(0, (long)(DateTimeOffset.Now - frame.CapturedAt).TotalMilliseconds);
        TimeFromOcrToOverlayMs = Math.Max(0, (long)(DateTimeOffset.Now - workResult.FinishedAt).TotalMilliseconds);

        TranslationPendingCount = _pipelineDiagnostics.TranslationQueueCount;
        RefreshDiagnostics();
        _ = SaveRealtimePipelineStateAsync(CancellationToken.None);
        _ = SaveOrderedPipelineDiagnosticsAsync(CancellationToken.None);
    }

    private async Task<(byte[]? Image, string Label)> CreateSecondarySpeakerCropAsync(byte[] fullCapture, CancellationToken cancellationToken)
    {
        if (!_translationSettings.EnableSecondarySpeakerOcr)
        {
            _logger.LogDebug("secondary_speaker_crop_skipped - reason=feature_disabled");
            return (null, string.Empty);
        }

        var region = _translationSettings.SecondaryOcrRegions.FirstOrDefault(item => item.IsEnabled && item.UseForSpeakerName);
        if (region is null)
        {
            // The most common real-world cause of "secondary OCR never gives a
            // speaker name": no region has both IsEnabled=true and
            // UseForSpeakerName=true — e.g. none was ever marked as the speaker
            // region in Settings, or the only configured region is disabled.
            _logger.LogInformation(
                "secondary_speaker_crop_skipped - reason=no_matching_region, regionCount={Count}",
                _translationSettings.SecondaryOcrRegions.Count);
            return (null, string.Empty);
        }
        try
        {
            using var stream = new MemoryStream(fullCapture);
            using var bitmap = new Bitmap(stream);
            var crop = new CaptureRegion
            {
                X = (int)Math.Round(Math.Clamp(region.XPercent, 0, 1) * bitmap.Width),
                Y = (int)Math.Round(Math.Clamp(region.YPercent, 0, 1) * bitmap.Height),
                Width = Math.Max(1, (int)Math.Round(Math.Clamp(region.WidthPercent, 0.01, 1) * bitmap.Width)),
                Height = Math.Max(1, (int)Math.Round(Math.Clamp(region.HeightPercent, 0.01, 1) * bitmap.Height)),
            };
            var cropped = await _cropService.CropAsync(fullCapture, crop, cancellationToken);
            _logger.LogDebug(
                "secondary_speaker_crop_ok - region={Region}, x={X}, y={Y}, w={W}, h={H}, bytes={Bytes}",
                region.Label, crop.X, crop.Y, crop.Width, crop.Height, cropped?.Length ?? 0);
            return (cropped, region.Label);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "secondary_speaker_crop_failed - region={Region}", region.Label);
            return (null, string.Empty);
        }
    }

    private async Task<string> ResolveSecondarySpeakerNameAsync(PendingOcrFrame frame, string dialogue)
    {
        if (!_translationSettings.EnableSecondarySpeakerOcr) return string.Empty;
        if (frame.SecondarySpeakerImageBytes is null)
        {
            _logger.LogDebug("secondary_speaker_ocr_skipped - reason=no_crop_for_frame");
            return string.Empty;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Clamp(OcrRequestTimeoutMs, 750, 3000)));
            var result = await _ocrService.RecognizeAsync(frame.SecondarySpeakerImageBytes, "paddle", timeout.Token);
            var candidate = string.Join(" ", result.Text.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd(':');
            if (candidate.Length == 0)
            {
                _logger.LogInformation("secondary_speaker_ocr_empty - region={Region}", frame.SecondarySpeakerRegionLabel);
                return string.Empty;
            }
            // The secondary crop often captures the whole caption row ("EDWARD KENWAY
            // You see, as a privateer...") rather than an isolated name plate, because
            // the source renders name + dialogue on one line. Try the leading run of
            // ALL-CAPS words as the name candidate first — falls back to the raw text
            // (unchanged prior behavior) when it doesn't look like a name prefix.
            var namePrefix = ExtractLeadingCapsName(candidate);
            var speakerCandidate = namePrefix ?? candidate;
            var (isSpeaker, rejectionReason) = _speakerNameDetector.IsSpeakerNameLine(speakerCandidate, dialogue);
            _logger.LogInformation(
                "secondary_speaker_ocr_result - region={Region}, text=\"{Text}\", accepted={Accepted}, reason={Reason}",
                frame.SecondarySpeakerRegionLabel, speakerCandidate, isSpeaker, rejectionReason ?? "-");
            return isSpeaker ? speakerCandidate : string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "secondary_speaker_ocr_failed - region={Region}", frame.SecondarySpeakerRegionLabel);
            return string.Empty;
        }
    }

    /// <summary>
    /// Pulls the leading run of ALL-CAPS words (1-3) off a candidate string, e.g.
    /// "EDWARD KENWAY You see, as a privateer..." → "EDWARD KENWAY". Returns null
    /// when the text doesn't start with such a run or the run is the entire text
    /// (nothing to prefix — let the caller fall back to raw-text validation).
    /// </summary>
    private static string? ExtractLeadingCapsName(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var nameWords = new List<string>();
        foreach (var word in words)
        {
            var letters = word.Where(char.IsLetter).ToArray();
            if (letters.Length < 2 || !letters.All(char.IsUpper)) break;
            nameWords.Add(word);
            if (nameWords.Count >= 3) break;
        }
        return nameWords.Count == 0 || nameWords.Count >= words.Length
            ? null
            : string.Join(' ', nameWords);
    }

    private FormattedSubtitle WithSpeakerName(FormattedSubtitle subtitle, string speaker)
    {
        var lines = subtitle.Lines.Where(line => !string.Equals(line.Trim().TrimEnd(':'), subtitle.SpeakerName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (_subtitleFormatterSettings.ShowSpeakerName) lines.Insert(0, speaker + ":");
        return new FormattedSubtitle { RawText = subtitle.RawText, CleanedText = subtitle.CleanedText, SpeakerName = speaker, MainText = subtitle.MainText, Lines = lines, DisplayText = string.Join(Environment.NewLine, lines), IsEmpty = subtitle.IsEmpty, Confidence = subtitle.Confidence, CreatedAt = subtitle.CreatedAt };
    }

    private static string Truncate(string? value, int max = 80)
    {
        value ??= string.Empty;
        value = value.Replace("\n", " / ").Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }

    private void HandleEmptyOcrResult(PendingOcrFrame frame)
    {
        _emptyOcrCount++;
        _previousOcrWasEmpty = true;
        _lastEmptyOcrAt ??= DateTimeOffset.Now;

        _ = File.WriteAllBytesAsync(
            Path.Combine(DebugDirectory, "last_empty_ocr_image.png"), frame.ImageBytes);

        _pipelineDiagnostics.LastTranslationQueueStatus =
            _translationSettings.EnableTranslation ? "not_enqueued_empty_ocr" : "disabled";
        _logger.LogInformation("translation_not_enqueued_empty_ocr");
        _pipelineDiagnosticsStore.Save();

        if (_translationSettings.TurkishOnlyMode)
        {
            // TranslationPlaybackQueue owns hold/clear timing autonomously via its
            // own silence timer (Part G) — OCR must not touch the overlay here.
            return;
        }

        // Part F: never clear the overlay on a single empty OCR result — only
        // after multiple empty results AND the hold/min-display durations have
        // elapsed. This protects one-frame subtitles from flickering away.
        if (!KeepPreviousSubtitleOnEmptyOcr)
        {
            ApplyDisplayUpdate(_subtitleDisplayStateManager.Clear("clear_overlay_empty_ocr"));
            return;
        }

        var holdElapsedMs = (DateTimeOffset.Now - _lastEmptyOcrAt!.Value).TotalMilliseconds;
        var overlayShownLongEnough = _lastOverlayShownAt is null ||
            (DateTimeOffset.Now - _lastOverlayShownAt.Value).TotalMilliseconds >= MinOverlayDisplayMs;
        var overlayState = _subtitleDisplayStateManager.GetSnapshot();
        var translatedShownLongEnough = overlayState.CurrentDisplayState != SubtitleDisplayState.ShowingTranslated ||
            overlayState.LastOverlayUpdatedAt is null ||
            (DateTimeOffset.Now - overlayState.LastOverlayUpdatedAt.Value).TotalMilliseconds >=
            _translationSettings.MinTranslatedDisplayMs;
        var clearAfterNoSubtitleMs = Math.Max(
            PreviousSubtitleHoldMs,
            _translationSettings.ClearOverlayAfterNoSubtitleMs);

        if (_emptyOcrCount >= ClearOverlayAfterEmptyOcrCount &&
            holdElapsedMs >= clearAfterNoSubtitleMs &&
            overlayShownLongEnough &&
            translatedShownLongEnough)
        {
            ApplyDisplayUpdate(_subtitleDisplayStateManager.Clear("clear_overlay_after_no_subtitle_timeout"));
        }
        else
        {
            ApplyDisplayUpdate(_subtitleDisplayStateManager.HoldCurrent("holding_previous_after_empty_ocr"));
        }
    }

    private string DecideOcr(
        bool manualForce,
        bool isFirstFrame,
        bool hashChanged,
        double? differencePercent)
    {
        if (RunOcrOnEveryTick) return "run_every_tick_enabled";
        if (manualForce) return "run_manual_force";
        if (isFirstFrame) return "run_first_frame";
        if (FramesSinceLastOcr >= ForceOcrEveryNFrames) return "run_force_frame_count";
        if (MillisecondsSinceLastOcr >= ForceOcrEveryMilliseconds) return "run_force_time";
        if (_previousOcrWasEmpty) return "run_previous_empty";
        if (_previousOcrFailed) return "run_previous_failed";
        if (string.IsNullOrWhiteSpace(_lastOverlayText)) return "run_previous_empty";
        if (differencePercent is null) return "run_region_changed";
        if (differencePercent >= RegionChangeThresholdPercent) return "run_region_changed";
        if (EnableImageHashSkip && hashChanged) return "run_region_changed";

        var skipEnabled = EnableImageHashSkip || EnableRegionDifferenceSkip;
        var hashUnchanged = !EnableImageHashSkip || !hashChanged;
        var regionUnchanged = !EnableRegionDifferenceSkip ||
            differencePercent < RegionChangeThresholdPercent;

        if (skipEnabled && hashUnchanged && regionUnchanged &&
            _previousOcrSucceeded && !_previousOcrWasEmpty)
        {
            return EnableRegionDifferenceSkip
                ? "skip_region_unchanged"
                : "skip_hash_unchanged";
        }

        return "run_region_changed";
    }

    // ── Subtitle line classification ─────────────────────────────────────────────

    private SubtitleLineSelectionResult ClassifyOcrLines(OcrResult result, byte[] ocrImage)
    {
        var cropWidth = 0;
        var cropHeight = 0;
        try
        {
            using var stream = new MemoryStream(ocrImage);
            using var bitmap = new Bitmap(stream);
            cropWidth = bitmap.Width;
            cropHeight = bitmap.Height;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not read OCR crop dimensions for line filtering");
        }

        var profile = _gameProfileRepository.GetByName(_subtitleFilterSettings.ActiveGameProfileName);
        var replacementSettings = _overlayService.CurrentSettings.Replacement;
        var selection = _subtitleLineClassifier.Classify(
            result.Lines,
            cropWidth,
            cropHeight,
            profile,
            _subtitleFilterSettings,
            replacementSettings.RejectHudControlText,
            replacementSettings.UseSubtitleCandidateScoring);

        _pipelineDiagnostics.CurrentSubtitleSelectedLines =
            string.Join(" | ", selection.SelectedSubtitleLines.Select(l => l.Text.Trim()));
        _pipelineDiagnostics.RejectedHudLines =
            string.Join(" | ", selection.RejectedHudLines.Select(l => l.Text.Trim()));

        Post(() =>
        {
            SelectedSubtitleLinesText = selection.SelectedSubtitleLines.Count > 0
                ? string.Join(Environment.NewLine, selection.SelectedSubtitleLines.Select(l => l.Text.Trim()))
                : "-";
            RejectedHudLinesText = selection.RejectedHudLines.Count > 0
                ? string.Join(Environment.NewLine, selection.RejectedHudLines.Select(l => l.Text.Trim()))
                : "-";
        });

        return selection;
    }

    private async Task SaveLineFilteringDiagnosticsAsync(
        OcrResult result,
        SubtitleLineSelectionResult selection,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            var snapshot = new
            {
                Timestamp = DateTimeOffset.Now,
                AllLines = result.Lines.Select(l => new
                {
                    l.Text, l.Confidence, l.BoundingBox,
                    l.RelativeY, l.RelativeCenterY,
                }),
                SelectedLines = selection.SelectedSubtitleLines.Select(l => l.Text),
                RejectedLines = selection.RejectedHudLines.Select(l => l.Text),
                selection.RejectionReasons,
                selection.SelectedText,
                selection.HasSubtitleCandidate,
                selection.FilteringApplied,
                BandSettings = new
                {
                    _subtitleFilterSettings.EnableSubtitleLineFiltering,
                    Mode = _subtitleFilterSettings.SubtitleBandMode.ToString(),
                    _subtitleFilterSettings.SubtitleBandTopPercent,
                    _subtitleFilterSettings.SubtitleBandBottomPercent,
                    _subtitleFilterSettings.ActiveGameProfileName,
                },
            };
            var json = JsonSerializer.Serialize(
                snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(
                Path.Combine(DebugDirectory, "last_ocr_line_filtering.json"),
                json,
                Encoding.UTF8,
                cancellationToken);

            var candidateValidationJson = JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                RawOcrLines = result.Lines.Select(line => line.Text),
                AcceptedLines = selection.SelectedSubtitleLines.Select(line => new
                {
                    line.Text,
                    line.Confidence,
                    line.BoundingBox,
                }),
                RejectedLines = selection.RejectedHudLines.Select(line => new
                {
                    line.Text,
                    line.Confidence,
                    line.BoundingBox,
                }),
                RejectionReasons = selection.RejectionReasons,
                SelectedSubtitleCandidate = selection.SelectedText,
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(
                Path.Combine(DebugDirectory, "last_subtitle_candidate_validation.json"),
                candidateValidationJson,
                Encoding.UTF8,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save line filtering diagnostics");
        }
    }

    // ── Subtitle translation completion (background thread) ─────────────────────

    private void OnSubtitleTranslationCompleted(TranslationResult result, SubtitleQueueItem item)
    {
        var overlayTextBefore = _lastOverlayText;
        var overlayUpdate = _subtitleDisplayStateManager
            .HandleTranslationAsync(result, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (overlayUpdate.WasTranslationLate)
        {
            _pipelineDiagnostics.TranslationLateCompletedCount++;
            _logger.LogInformation(
                "subtitle_translation_late - ageMs={AgeMs}, text={Text}",
                item.AgeMs, result.SourceText.Length <= 60 ? result.SourceText : result.SourceText[..60]);
        }

        ApplyDisplayUpdate(overlayUpdate);

        // Trigger lightweight Ollama refinement in the background if configured.
        // Never blocks overlay, OCR, or machine translation.
        if (result.Success && !string.IsNullOrWhiteSpace(result.TranslatedText))
        {
            var sourceKey = _pipelineDiagnostics.CurrentNormalizedSourceKey;
            _refinementOrchestrator.CurrentSourceKey = sourceKey;
            _refinementOrchestrator.TriggerBackgroundIfEnabled(
                result.SourceText,
                result.TranslatedText,
                sourceKey,
                _translationSettings.GameProfile);
        }

        var wasFallbackUsed = !result.Success &&
            overlayUpdate.Reason == "translation_failed_show_source_fallback";
        var fallbackReason = wasFallbackUsed
            ? result.ErrorMessage ?? "translation_failed"
            : string.Empty;

        _pipelineDiagnostics.LastOverlayReplaceReason = overlayUpdate.Reason;
        _pipelineDiagnostics.LastTranslationWasFallbackUsed = wasFallbackUsed;
        _pipelineDiagnostics.LastTranslationFallbackReason = fallbackReason;
        _pipelineDiagnostics.NotifyChanged();
        _pipelineDiagnosticsStore.Save();

        _ = SaveTranslationAttemptAsync(
            result, overlayTextBefore, _lastOverlayText, wasFallbackUsed, fallbackReason, overlayUpdate.Reason);
        _ = SaveTranslationResultAsync(result, CancellationToken.None);
        RefreshDiagnostics();
    }

    private void OnRefinementCompleted(TranslationRefinementResult result, bool shouldReplaceOverlay)
    {
        if (!result.Success || string.IsNullOrWhiteSpace(result.RefinedText)) return;

        _pipelineDiagnostics.LastRefinementDurationMs = result.DurationMs;
        _pipelineDiagnostics.LastRefinedText = result.RefinedText;
        _pipelineDiagnostics.LastRefinementError = result.ErrorMessage ?? string.Empty;
        _pipelineDiagnostics.LastRefinementOverlayReplaced = shouldReplaceOverlay;

        if (shouldReplaceOverlay)
        {
            Post(() => TryUpdateOverlay(result.RefinedText));
        }

        RefreshDiagnostics();
    }

    private void ApplyDisplayUpdate(SubtitleDisplayUpdateResult overlayUpdate)
    {
        SyncOverlayDiagnostics();
        if (!overlayUpdate.ShouldUpdateOverlay)
            return;

        TryUpdateOverlay(overlayUpdate.DisplayText);
        _lastOverlayShownAt = DateTimeOffset.Now;
        SyncOverlayDiagnostics();
    }

    private void SyncOverlayDiagnostics()
    {
        var overlayState = _subtitleDisplayStateManager.GetSnapshot();
        _pipelineDiagnostics.CurrentSubtitleSourceText = overlayState.CurrentSourceText;
        _pipelineDiagnostics.CurrentNormalizedSourceKey = overlayState.CurrentNormalizedSourceKey;
        _pipelineDiagnostics.CurrentOverlayDisplayText = overlayState.CurrentDisplayText;
        _pipelineDiagnostics.CurrentOverlayDisplayLanguage = overlayState.CurrentDisplayLanguage.ToString();
        _pipelineDiagnostics.CurrentOverlayDisplayState = overlayState.CurrentDisplayState.ToString();
        _pipelineDiagnostics.CurrentOverlayTranslationText = overlayState.CurrentTranslationText;
        _pipelineDiagnostics.LastOverlayReplaceReason = overlayState.LastOverlayUpdateReason;
        _pipelineDiagnostics.LastOverlaySourceIgnoredBecauseTranslationExists = overlayState.WasSourceIgnoredBecauseTranslationExists;
        _pipelineDiagnostics.LastOverlayTranslationWasLate = overlayState.WasTranslationLate;
        _pipelineDiagnostics.LastOverlayCacheHit = overlayState.WasCacheHit;
    }

    private void TryUpdateOverlay(string text)
    {
        if (!_overlayService.IsOpen) return;
        try
        {
            _overlayService.UpdateText(text);
            _lastOverlayText = text;
            _lastOverlayUpdateTime = DateTimeOffset.Now;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to update overlay text");
        }
    }
    private async Task SaveTranslationAttemptAsync(
        TranslationResult result,
        string overlayTextBefore,
        string overlayTextAfter,
        bool wasFallbackUsed,
        string fallbackReason,
        string overlayReplaceReason)
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            var attempt = new
            {
                Timestamp = DateTimeOffset.Now,
                FormattedSubtitleMainText = result.SourceText,
                EnableTranslation = _translationSettings.EnableTranslation,
                SelectedProviderType = _translationSettings.ProviderType.ToString(),
                ActualProviderUsed = _pipelineDiagnostics.ActualProviderUsed,
                UseFakeTranslationProviderForDebug = _translationSettings.UseFakeTranslationProviderForDebug,
                MachineTranslationBaseUrl = _translationSettings.MachineTranslationBaseUrl,
                RequestJson = JsonSerializer.Serialize(new
                {
                    text = result.SourceText,
                    sourceLanguage = _translationSettings.SourceLanguage,
                    targetLanguage = _translationSettings.TargetLanguage,
                }),
                RawHttpResponse = result.RawOutput ?? string.Empty,
                ParsedTranslation = result.TranslatedText,
                Success = result.Success,
                ErrorMessage = result.ErrorMessage ?? string.Empty,
                DurationMs = result.DurationMs,
                OverlayTextBefore = overlayTextBefore,
                OverlayTextAfter = overlayTextAfter,
                WasFallbackUsed = wasFallbackUsed,
                FallbackReason = fallbackReason,
                OverlayReplaceReason = overlayReplaceReason,
            };
            var json = JsonSerializer.Serialize(
                attempt, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(
                Path.Combine(DebugDirectory, "last_translation_attempt.json"),
                json,
                Encoding.UTF8);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save translation attempt diagnostics");
        }
    }

    private async Task StartFastDialogueStressTestAsync()
    {
        string[] subtitles =
        [
            "Wait!",
            "No!",
            "Come here!",
            "We have to move.",
            "They are coming!",
        ];

        if (!_overlayService.IsOpen)
            _overlayService.Open(await _overlayMonitorCoordinator.LoadAndValidateAsync());

        var previousStatus = MonitoringStatusText;
        MonitoringStatusText = "Fast dialogue stress test";
        try
        {
            foreach (var text in subtitles)
            {
                var subtitle = await _subtitleFormatter.FormatAsync(text, 1.0, CancellationToken.None);
                _lastFormattedSubtitle = subtitle;
                _pipelineDiagnostics.CurrentSubtitleSourceText = subtitle.MainText;
                var overlayUpdate = await _subtitleDisplayStateManager.HandleSourceAsync(subtitle, CancellationToken.None);
                ApplyDisplayUpdate(overlayUpdate);

                if (overlayUpdate.ShouldEnqueueTranslation)
                    _subtitleTranslationQueue.Enqueue(subtitle);

                await SaveRealtimePipelineStateAsync(CancellationToken.None);
                RefreshDiagnostics();
                await Task.Delay(500);
            }
        }
        finally
        {
            MonitoringStatusText = previousStatus;
        }
    }

    // ── Manual subtitle filtering test (Part I) ──────────────────────────────────

    private Task TestSubtitleFilteringAsync()
    {
        // Synthetic geometry on a 1000x400 crop: line 1 near the top band,
        // line 2 near the bottom where tutorial prompts usually appear.
        var lines = new List<OcrLine>();
        if (!string.IsNullOrWhiteSpace(FilterTestLine1))
            lines.Add(new OcrLine { Text = FilterTestLine1.Trim(), Confidence = 0.95, X = 300, Y = 60, Right = 700, Bottom = 100 });
        if (!string.IsNullOrWhiteSpace(FilterTestLine2))
            lines.Add(new OcrLine { Text = FilterTestLine2.Trim(), Confidence = 0.95, X = 200, Y = 320, Right = 800, Bottom = 360 });

        var profile = _gameProfileRepository.GetByName(_subtitleFilterSettings.ActiveGameProfileName);
        var selection = _subtitleLineClassifier.Classify(
            lines, 1000, 400, profile, _subtitleFilterSettings);

        var selected = selection.SelectedSubtitleLines.Count > 0
            ? string.Join(Environment.NewLine, selection.SelectedSubtitleLines.Select(l => "  " + l.Text))
            : "  (none)";
        var rejected = selection.RejectedHudLines.Count > 0
            ? string.Join(Environment.NewLine, selection.RejectedHudLines.Select(l => "  " + l.Text))
            : "  (none)";

        FilterTestResultText =
            $"Selected subtitle lines:{Environment.NewLine}{selected}{Environment.NewLine}" +
            $"Rejected HUD lines:{Environment.NewLine}{rejected}{Environment.NewLine}" +
            $"Reasons: {(selection.RejectionReasons.Length > 0 ? selection.RejectionReasons : "-")}";
        return Task.CompletedTask;
    }

    private async Task RunSingleTickAsync()
    {
        var window = _captureViewModel.SelectedWindow;
        if (window is null)
        {
            SetError("Select a window before running a monitoring tick.");
            return;
        }

        var timer = Stopwatch.StartNew();
        await RunPipelineTickAsync(window, manualForce: true, CancellationToken.None);
        timer.Stop();
        _lastTotalLoopDurationMs = timer.ElapsedMilliseconds;
        RefreshDiagnostics();
        await SaveMonitoringStateAsync(CancellationToken.None);
    }

    private async Task RunOcrOnCurrentPreviewAsync()
    {
        var image = _latestOcrImage;
        if (image is null)
        {
            SetError("No current OCR preview. Run a monitoring tick first.");
            return;
        }

        Directory.CreateDirectory(DebugDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(DebugDirectory, "last_manual_ocr_crop.png"),
            image,
            CancellationToken.None);
        _frameNumber++;
        var frame = new PendingOcrFrame
        {
            ImageBytes = image,
            FrameNumber = _frameNumber,
            Reason = "run_manual_force",
            CapturedAt = DateTimeOffset.Now,
            IsForced = true,
            CropHash = OcrResultCache.ComputeImageHash(image),
            FinalCropWidth = 1,
            FinalCropHeight = 1,
            OcrImageWidth = 1,
            OcrImageHeight = 1,
        };
        _ocrWorker.Configure(
            OcrRequestTimeoutMs, MinOcrIntervalMs, ProcessLatestPendingFrameAfterOcr,
            EffectiveOcrFrameBufferSize, MaxBufferedFrameAgeMs);
        await SubmitAndWaitAsync(frame, CancellationToken.None);
        await SaveMonitoringStateAsync(CancellationToken.None);
    }

    private Task ToggleNeverSkipOcrAsync()
    {
        NeverSkipOcr = !NeverSkipOcr;
        return Task.CompletedTask;
    }

    private CaptureRegion CreateRelativePaddedRegion(CaptureRegion savedRegion) => new()
    {
        X = Math.Min(CropPaddingLeft, Math.Max(0, savedRegion.Width - 1)),
        Y = Math.Min(CropPaddingTop, Math.Max(0, savedRegion.Height - 1)),
        Width = Math.Max(1, savedRegion.Width - CropPaddingLeft - CropPaddingRight),
        Height = Math.Max(1, savedRegion.Height - CropPaddingTop - CropPaddingBottom),
    };

    private SubtitleReplacementContext? BuildReplacementContext(
        PendingOcrFrame frame,
        SubtitleLineSelectionResult selection)
    {
        if (frame.WindowWidth <= 0 || frame.WindowHeight <= 0)
            return null;

        var replacementSettings = _overlayService.CurrentSettings.Replacement;

        var hasLineBoxes = selection.SelectedSubtitleLines.Any(line => line.HasBoundingBox);
        var rawOcrLineRect = hasLineBoxes
            ? CreateUnionRect(selection.SelectedSubtitleLines.Where(line => line.HasBoundingBox))
            : new OverlayRectangle();
        var scaleX = frame.OcrImageWidth > 0 ? frame.FinalCropWidth / (double)frame.OcrImageWidth : 1.0;
        var scaleY = frame.OcrImageHeight > 0 ? frame.FinalCropHeight / (double)frame.OcrImageHeight : 1.0;
        var ocrLineRect = hasLineBoxes
            ? new OverlayRectangle
            {
                X = rawOcrLineRect.X * scaleX,
                Y = rawOcrLineRect.Y * scaleY,
                Width = rawOcrLineRect.Width * scaleX,
                Height = rawOcrLineRect.Height * scaleY,
            }
            : new OverlayRectangle();

        var cropRect = new OverlayRectangle
        {
            X = frame.WindowLeft + frame.FinalCropOffsetX,
            Y = frame.WindowTop + frame.FinalCropOffsetY,
            Width = Math.Max(1, frame.FinalCropWidth),
            Height = Math.Max(1, frame.FinalCropHeight),
        };

        var fallbackBandTop = frame.SavedRegionY + (frame.SavedRegionHeight * 0.55);
        var fallbackBandHeight = Math.Max(1, frame.SavedRegionHeight * 0.45);
        var originalScreenRect = hasLineBoxes
            ? new OverlayRectangle
            {
                X = cropRect.X + ocrLineRect.X,
                Y = cropRect.Y + ocrLineRect.Y,
                Width = ocrLineRect.Width,
                Height = ocrLineRect.Height,
            }
            : new OverlayRectangle
            {
                X = frame.WindowLeft + frame.SavedRegionX,
                Y = frame.WindowTop + fallbackBandTop,
                Width = Math.Max(1, frame.SavedRegionWidth),
                Height = fallbackBandHeight,
            };

        // Part A: the manual replacement region is the primary positioning method.
        // It is used exactly as selected — no padding, no shrink-to-text, no
        // min/max-width adjustment — so the mask always covers the same area the
        // user drew over the original English subtitle.
        var manualRect = ManualReplacementRegionHelper.TryGetScreenRect(
            replacementSettings,
            frame.WindowLeft,
            frame.WindowTop,
            frame.WindowWidth,
            frame.WindowHeight);

        var maskRect = manualRect ?? ApplyReplacementBounds(
            originalScreenRect,
            frame.WindowLeft,
            frame.WindowTop,
            frame.WindowWidth,
            frame.WindowHeight,
            replacementSettings);

        if (manualRect is not null)
            originalScreenRect = manualRect.Clone();

        var allOcrLines = selection.SelectedSubtitleLines.Concat(selection.RejectedHudLines).ToArray();
        var selectedBoxes = selection.SelectedSubtitleLines
            .Where(line => line.HasBoundingBox)
            .Select(line => new OverlayRectangle
            {
                X = line.X,
                Y = line.Y,
                Width = line.Width,
                Height = line.Height,
            })
            .ToArray();

        return new SubtitleReplacementContext
        {
            OcrLineRect = ocrLineRect,
            CropRect = cropRect,
            WindowRect = new OverlayRectangle
            {
                X = frame.WindowLeft,
                Y = frame.WindowTop,
                Width = frame.WindowWidth,
                Height = frame.WindowHeight,
            },
            ScreenRect = originalScreenRect.Clone(),
            OverlayRect = maskRect.Clone(),
            UsedFallbackRegion = !hasLineBoxes,
            SelectedLinesText = string.Join(" | ", selection.SelectedSubtitleLines.Select(line => line.Text.Trim())),
            OcrLineBoxes = allOcrLines
                .Where(line => line.HasBoundingBox)
                .Select(line => new OverlayRectangle
                {
                    X = line.X,
                    Y = line.Y,
                    Width = line.Width,
                    Height = line.Height,
                })
                .ToArray(),
            SelectedLineBoxes = selectedBoxes,
            UnionSubtitleRectInCrop = ocrLineRect.Clone(),
            CropRectInWindow = new OverlayRectangle
            {
                X = frame.FinalCropOffsetX,
                Y = frame.FinalCropOffsetY,
                Width = Math.Max(1, frame.FinalCropWidth),
                Height = Math.Max(1, frame.FinalCropHeight),
            },
        };
    }

    private static OverlayRectangle CreateUnionRect(IEnumerable<OcrLine> lines)
    {
        var materialized = lines.ToArray();
        if (materialized.Length == 0) return new OverlayRectangle();

        var minX = materialized.Min(line => line.X);
        var minY = materialized.Min(line => line.Y);
        var maxRight = materialized.Max(line => line.Right);
        var maxBottom = materialized.Max(line => line.Bottom);

        return new OverlayRectangle
        {
            X = minX,
            Y = minY,
            Width = Math.Max(1, maxRight - minX),
            Height = Math.Max(1, maxBottom - minY),
        };
    }

    private static OverlayRectangle ApplyReplacementBounds(
        OverlayRectangle rect,
        int windowLeft,
        int windowTop,
        int windowWidth,
        int windowHeight,
        SubtitleReplacementOverlaySettings settings)
    {
        var padded = new OverlayRectangle
        {
            X = rect.X - settings.ReplacementMaskPaddingLeft,
            Y = rect.Y - settings.ReplacementMaskPaddingTop,
            Width = rect.Width + settings.ReplacementMaskPaddingLeft + settings.ReplacementMaskPaddingRight,
            Height = rect.Height + settings.ReplacementMaskPaddingTop + settings.ReplacementMaskPaddingBottom,
        };

        padded.Width = Math.Max(settings.ReplacementMinWidth, padded.Width);
        padded.Height = Math.Max(settings.ReplacementMinHeight, padded.Height);

        var maxWidth = Math.Max(settings.ReplacementMinWidth, windowWidth * settings.ReplacementMaxWidthPercent);
        padded.Width = Math.Min(maxWidth, padded.Width);

        var windowRight = windowLeft + windowWidth;
        if (padded.X < windowLeft)
            padded.X = windowLeft;

        if (padded.Right > windowRight)
            padded.X = Math.Max(windowLeft, windowRight - padded.Width);

        var windowBottom = windowTop + windowHeight;
        if (padded.Y < windowTop)
            padded.Y = windowTop;

        if (padded.Bottom > windowBottom)
            padded.Y = Math.Max(windowTop, windowBottom - padded.Height);

        return padded;
    }

    private byte[] PreprocessImage(byte[] sourceBytes)
    {
        using var inputStream = new MemoryStream(sourceBytes);
        using var source = new Bitmap(inputStream);
        var width = Math.Max(1, (int)Math.Round(source.Width * UpscaleFactor));
        var height = Math.Max(1, (int)Math.Round(source.Height * UpscaleFactor));
        using var output = new Bitmap(width, height, DrawingPixelFormat.Format24bppRgb);

        using (var graphics = Graphics.FromImage(output))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, 0, 0, width, height);
        }

        ApplyPixelPreprocessing(output);

        using var resultStream = new MemoryStream();
        output.Save(resultStream, ImageFormat.Png);
        return resultStream.ToArray();
    }

    private void ApplyPixelPreprocessing(Bitmap bitmap)
    {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadWrite, DrawingPixelFormat.Format24bppRgb);
        try
        {
            var length = Math.Abs(data.Stride) * bitmap.Height;
            var pixels = new byte[length];
            Marshal.Copy(data.Scan0, pixels, 0, length);

            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var index = y * data.Stride + x * 3;
                    var blue = pixels[index];
                    var green = pixels[index + 1];
                    var red = pixels[index + 2];

                    if (ConvertToGrayscale)
                    {
                        var gray = ClampToByte(red * 0.299 + green * 0.587 + blue * 0.114);
                        red = green = blue = gray;
                    }

                    if (IncreaseContrast)
                    {
                        red = ApplyContrast(red);
                        green = ApplyContrast(green);
                        blue = ApplyContrast(blue);
                    }

                    if (ThresholdMode == "binary")
                    {
                        var value = (red + green + blue) / 3 >= 160 ? (byte)255 : (byte)0;
                        red = green = blue = value;
                    }

                    pixels[index] = blue;
                    pixels[index + 1] = green;
                    pixels[index + 2] = red;
                }
            }

            if (SharpenImage && bitmap.Width > 2 && bitmap.Height > 2)
            {
                var sharpenSource = (byte[])pixels.Clone();
                ApplySharpenKernel(pixels, sharpenSource, data.Stride, bitmap.Width, bitmap.Height);
            }

            Marshal.Copy(pixels, 0, data.Scan0, length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void ApplySharpenKernel(byte[] target, byte[] source, int stride, int width, int height)
    {
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var center = y * stride + x * 3;
                foreach (var channel in new[] { 0, 1, 2 })
                {
                    var value = 5 * source[center + channel]
                        - source[center - 3 + channel]
                        - source[center + 3 + channel]
                        - source[center - stride + channel]
                        - source[center + stride + channel];
                    target[center + channel] = (byte)Math.Clamp(value, 0, 255);
                }
            }
        }
    }

    private static byte ApplyContrast(byte value) =>
        ClampToByte((value - 128) * 1.25 + 128);

    private static byte ClampToByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private void UpdatePreview(byte[] pngBytes, bool isOcrImage)
    {
        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(pngBytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            Post(() =>
            {
                if (isOcrImage) OcrImagePreview = image;
                else CropPreview = image;
            });
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not create monitoring preview");
        }
    }

    private async Task SaveDebugImagesAsync(
        byte[] fullCapture,
        byte[] regionCrop,
        byte[] finalCrop,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DebugDirectory);
        await File.WriteAllBytesAsync(Path.Combine(DebugDirectory, "latest_full_capture.png"), fullCapture, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(DebugDirectory, "latest_region_crop.png"), regionCrop, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(DebugDirectory, "latest_final_ocr_crop.png"), finalCrop, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(DebugDirectory, "latest_capture_full.png"), fullCapture, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(DebugDirectory, "latest_ocr_region_raw.png"), regionCrop, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(DebugDirectory, "latest_ocr_region_processed.png"), finalCrop, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(DebugDirectory, "latest_ocr_sent_to_provider.png"), finalCrop, cancellationToken);
    }

    private async Task SaveRealtimePipelineStateAsync(CancellationToken cancellationToken)
    {
        if (!await _realtimeStateGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            var state = new
            {
                Timestamp = DateTimeOffset.Now,
                CaptureFps,
                CaptureIntervalMs,
                CaptureDurationMs,
                CropDurationMs,
                DifferenceDurationMs,
                OcrQueueState,
                IsOcrBusy,
                PendingOcrFrameNumber,
                LastProcessedOcrFrameNumber,
                PendingFrameReplacedCount,
                OcrStartedCount,
                OcrCompletedCount,
                OcrSkippedNoChangeCount,
                OcrForcedCount,
                OcrDurationMs,
                TimeFromCaptureToOverlayMs,
                TimeFromOcrToOverlayMs,
                TranslationPendingCount = _pipelineDiagnostics.TranslationQueueCount,
                TranslationDurationMs,
                FinalOcrCropWidth,
                FinalOcrCropHeight,
                FinalOcrCropArea,
                CropSizeWarningText,
                LastDecision = _lastDecision,
                LastProcessedCropHash = _ocrWorker.LastProcessedCropHash,
            };
            // Runs on every capture tick (~10/s) — must not touch the disk here.
            DebugFileWriter.Queue(
                Path.Combine(DebugDirectory, "last_realtime_pipeline_state.json"), state);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save realtime pipeline diagnostics");
        }
        finally
        {
            _realtimeStateGate.Release();
        }
    }

    private Task SaveOrderedPipelineDiagnosticsAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            var captured = _orderedSubtitlePipeline.GetCaptureSnapshot();
            var playback = _orderedSubtitlePipeline.GetPlaybackSnapshot();
            var snapshot = new
            {
                Timestamp = DateTimeOffset.Now,
                _translationSettings.TurkishOnlyMode,
                CapturedQueueItems = captured.Select(i => new
                {
                    i.Id,
                    i.SourceText,
                    i.NormalizedSourceKey,
                    Status = i.Status.ToString(),
                    i.FrameNumber,
                    i.FromMemory,
                    i.FromCache,
                    i.AgeMs,
                }).ToArray(),
                TranslationQueueCount = _orderedSubtitlePipeline.DispatchQueueCount,
                PlaybackQueueItems = playback.Select(p => new
                {
                    p.SourceText,
                    p.TranslatedText,
                    p.NormalizedSourceKey,
                    p.FromMemory,
                    p.FromCache,
                    p.DisplayedAt,
                    p.ReadyAt,
                }).ToArray(),
                CurrentOverlayText = _lastOverlayText,
                Counters = new
                {
                    _pipelineDiagnostics.CapturedQueueCount,
                    _pipelineDiagnostics.PlaybackQueueCount,
                    _pipelineDiagnostics.RejectedBeforeQueueCount,
                    _pipelineDiagnostics.AcceptedSubtitleCandidateCount,
                    _pipelineDiagnostics.DuplicateSubtitleIgnoredCount,
                    _pipelineDiagnostics.MemoryHitCount,
                    _pipelineDiagnostics.CacheHitCount,
                    _pipelineDiagnostics.InFlightHitCount,
                    _pipelineDiagnostics.ActualOpusCallCount,
                    _pipelineDiagnostics.ExpiredSkippedCount,
                    _pipelineDiagnostics.AverageOpusDurationMs,
                    _pipelineDiagnostics.AverageCaptureToDisplayLatencyMs,
                    _pipelineDiagnostics.TranslationLateCompletedCount,
                },
                LastReasons = new
                {
                    _pipelineDiagnostics.LastRejectedSubtitleCandidate,
                    _pipelineDiagnostics.LastRejectedReason,
                    _pipelineDiagnostics.LastDedupReason,
                    _pipelineDiagnostics.LastOverlayUpdateSource,
                },
            };
            // Snapshot materialized eagerly (ToArray) so the background writer
            // never enumerates live queues.
            DebugFileWriter.Queue(
                Path.Combine(DebugDirectory, "last_ordered_subtitle_pipeline.json"),
                snapshot);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save ordered subtitle pipeline diagnostics");
        }
        return Task.CompletedTask;
    }

    private async Task SaveMonitoringStateAsync(CancellationToken cancellationToken)
    {
        if (!SaveDebugFrames) return;
        var now = DateTimeOffset.Now;
        if ((now - _lastMonitoringStateSavedAt).TotalMilliseconds < 500) return;
        _lastMonitoringStateSavedAt = now;

        try
        {
            Directory.CreateDirectory(DebugDirectory);
            var state = new
            {
                IsMonitoringRunning,
                IsOcrBusy,
                FrameNumber = _frameNumber,
                LastOcrFrameNumber = _lastOcrFrameNumber,
                LastOcrStartedAt = _lastOcrStartedAt,
                LastOcrFinishedAt = _lastOcrFinishedAt,
                FramesSinceLastOcr,
                MillisecondsSinceLastOcr,
                OcrExecutionCount = _ocrWorker.OcrStartedCount,
                SkippedCount = _skippedCount,
                OcrBusyCount = _ocrWorker.PendingFrameReplacedCount,
                LastDecision = _lastDecision,
                LastSkipReason = _lastSkipReason,
                LastOcrRunReason = _lastOcrRunReason,
                LastOcrDurationMs = _lastOcrDurationMs,
                LastTotalLoopDurationMs = _lastTotalLoopDurationMs,
                LastRegionDifferencePercent = _lastRegionDifferencePercent,
                LastOcrRawText = _lastOcrRawText,
                LastOcrCleanedText = _lastOcrCleanedText,
                LastOcrConfidence = _lastOcrConfidence,
                EmptyOcrCount = _emptyOcrCount,
                LastOverlayUpdateTime = _lastOverlayUpdateTime,
            };

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(
                Path.Combine(DebugDirectory, "monitoring_state.json"),
                json,
                Encoding.UTF8,
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save monitoring diagnostics");
        }
    }

    private async Task SaveFormattedSubtitleAsync(
        FormattedSubtitle subtitle,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            var json = JsonSerializer.Serialize(
                subtitle,
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(
                Path.Combine(DebugDirectory, "last_subtitle_formatted.json"),
                json,
                Encoding.UTF8,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save formatted subtitle diagnostics");
        }
    }

    private async Task SaveSpeakerDetectionAsync(
        FormattedSubtitle subtitle,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            var snapshot = new
            {
                Timestamp = DateTimeOffset.Now,
                subtitle.RawText,
                subtitle.CleanedText,
                subtitle.SpeakerName,
                DialogueText = subtitle.MainText,
                CandidateAccepted = !subtitle.IsEmpty && !string.IsNullOrWhiteSpace(subtitle.MainText),
                RejectionReason = subtitle.IsEmpty
                    ? "empty_or_speaker_only"
                    : string.Empty,
                SpeakerExcludedFromTranslation = string.IsNullOrWhiteSpace(subtitle.SpeakerName) ||
                    !subtitle.MainText.Contains(subtitle.SpeakerName, StringComparison.OrdinalIgnoreCase),
            };
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(
                Path.Combine(DebugDirectory, "last_speaker_detection.json"),
                json,
                Encoding.UTF8,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save speaker detection diagnostics");
        }
    }

    private async Task SaveTranslationResultAsync(
        TranslationResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            var json = JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(
                Path.Combine(DebugDirectory, "last_translation_result.json"),
                json,
                Encoding.UTF8,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save translation result diagnostics");
        }
    }

    private static double? TryComputePixelDifferencePercent(byte[] firstImage, byte[] secondImage)
    {
        try
        {
            using var firstStream = new MemoryStream(firstImage);
            using var secondStream = new MemoryStream(secondImage);
            using var first = new Bitmap(firstStream);
            using var second = new Bitmap(secondStream);
            if (first.Width != second.Width || first.Height != second.Height) return 100;

            var rectangle = new Rectangle(0, 0, first.Width, first.Height);
            var firstData = first.LockBits(rectangle, ImageLockMode.ReadOnly, DrawingPixelFormat.Format24bppRgb);
            var secondData = second.LockBits(rectangle, ImageLockMode.ReadOnly, DrawingPixelFormat.Format24bppRgb);
            try
            {
                var rowLength = first.Width * 3;
                var firstRow = new byte[rowLength];
                var secondRow = new byte[rowLength];
                long differentPixels = 0;

                for (var y = 0; y < first.Height; y++)
                {
                    Marshal.Copy(firstData.Scan0 + y * firstData.Stride, firstRow, 0, rowLength);
                    Marshal.Copy(secondData.Scan0 + y * secondData.Stride, secondRow, 0, rowLength);
                    for (var x = 0; x < rowLength; x += 3)
                    {
                        if (Math.Abs(firstRow[x] - secondRow[x]) > 20 ||
                            Math.Abs(firstRow[x + 1] - secondRow[x + 1]) > 20 ||
                            Math.Abs(firstRow[x + 2] - secondRow[x + 2]) > 20)
                        {
                            differentPixels++;
                        }
                    }
                }

                return (double)differentPixels / (first.Width * first.Height) * 100;
            }
            finally
            {
                first.UnlockBits(firstData);
                second.UnlockBits(secondData);
            }
        }
        catch
        {
            return null;
        }
    }

    private async Task ShowTestOverlayAsync()
    {
        if (!_overlayService.IsOpen) _overlayService.Open(await _overlayMonitorCoordinator.LoadAndValidateAsync());
        _overlayService.UpdateText("Overlay position test\nLine 2: The quick brown fox");
    }

    private async Task ShowTestOverlayLongTextAsync()
    {
        if (!_overlayService.IsOpen) _overlayService.Open(await _overlayMonitorCoordinator.LoadAndValidateAsync());
        _overlayService.UpdateText("This is a longer overlay test line.\nSecond line for wrapping.\nThird line.");
    }

    private void ResetState()
    {
        _previousOcrImage = null;
        _lastImageHash = null;
        _lastOverlayText = string.Empty;
        _previousOcrSucceeded = false;
        _previousOcrWasEmpty = true;
        _previousOcrFailed = false;
        _frameNumber = 0;
        _lastOcrFrameNumber = 0;
        _lastOcrStartedAt = null;
        _lastOcrFinishedAt = null;
        _skippedCount = 0;
        _emptyOcrCount = 0;
        _lastDecision = "not_started";
        _lastSkipReason = "-";
        _lastOcrRunReason = "-";
        _lastOcrRawText = string.Empty;
        _lastOcrCleanedText = string.Empty;
        _lastFormattedSubtitle = new FormattedSubtitle { IsEmpty = true };
        _lastOcrConfidence = 0;
        _lastOverlayUpdateTime = null;
        _subtitleDisplayStateManager.Reset();
        SyncOverlayDiagnostics();
        RefreshDiagnostics();
    }

    private void RefreshDiagnostics()
    {
        _pipelineDiagnostics.TranslationEnabled = _translationSettings.EnableTranslation;
        _pipelineDiagnostics.TranslationDisplayMode = _translationSettings.DisplayMode;
        _pipelineDiagnosticsStore.Save();
        Post(() =>
        {
            RaisePropertyChanged(nameof(IsOcrBusy));
            RaisePropertyChanged(nameof(FrameNumber));
            RaisePropertyChanged(nameof(LastOcrFrameNumber));
            RaisePropertyChanged(nameof(LastOcrStartedAt));
            RaisePropertyChanged(nameof(LastOcrFinishedAt));
            RaisePropertyChanged(nameof(FramesSinceLastOcr));
            RaisePropertyChanged(nameof(MillisecondsSinceLastOcr));
            RaisePropertyChanged(nameof(DiagnosticsText));
            RaisePropertyChanged(nameof(FormatterRawOcrText));
            RaisePropertyChanged(nameof(FormatterCleanedOcrText));
            RaisePropertyChanged(nameof(FormatterSpeakerName));
            RaisePropertyChanged(nameof(FormatterMainText));
            RaisePropertyChanged(nameof(FormatterDisplayText));
            RaisePropertyChanged(nameof(FormatterEnabledText));
            RaisePropertyChanged(nameof(DbgFrameCount));
            RaisePropertyChanged(nameof(DbgSkippedCount));
            RaisePropertyChanged(nameof(DbgOcrCount));
            RaisePropertyChanged(nameof(DbgLastOcrReason));
            RaisePropertyChanged(nameof(DbgLastSkipReason));
            RaisePropertyChanged(nameof(DbgFramesSinceOcr));
            RaisePropertyChanged(nameof(DbgMsSinceOcr));
            RaisePropertyChanged(nameof(DbgLastOcrMs));
            RaisePropertyChanged(nameof(DbgCurrentHash));
            RaisePropertyChanged(nameof(DbgPrevHash));
            RaisePropertyChanged(nameof(DbgRegionDiff));
            RaisePropertyChanged(nameof(OcrQueueState));
            RaisePropertyChanged(nameof(PendingOcrFrameNumber));
            RaisePropertyChanged(nameof(LastProcessedOcrFrameNumber));
            RaisePropertyChanged(nameof(PendingFrameReplacedCount));
            RaisePropertyChanged(nameof(OcrStartedCount));
            RaisePropertyChanged(nameof(OcrCompletedCount));
            RaisePropertyChanged(nameof(OcrDurationMs));
            RaisePropertyChanged(nameof(TranslationDurationMs));
            RaisePropertyChanged(nameof(FinalOcrCropArea));
            RaisePropertyChanged(nameof(RealtimeDiagnosticsText));
            RaisePropertyChanged(nameof(OrderedPipelineDiagnosticsText));
        });
    }

    private void Post(Action action) => _uiContext.Post(_ => action(), null);

    private void OnSecondaryOcrRegionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (SecondaryOcrRegionEntry region in e.OldItems) region.PropertyChanged -= OnSecondaryOcrRegionPropertyChanged;
        if (e.NewItems is not null)
            foreach (SecondaryOcrRegionEntry region in e.NewItems) region.PropertyChanged += OnSecondaryOcrRegionPropertyChanged;
        PersistSecondaryOcrRegions();
    }

    private void OnSecondaryOcrRegionPropertyChanged(object? sender, PropertyChangedEventArgs e) => PersistSecondaryOcrRegions();

    private void PersistSecondaryOcrRegions()
    {
        if (_synchronizingSecondaryRegions) return;
        _translationSettings.SecondaryOcrRegions = SecondaryOcrRegions.Select(region => region.ToSettings()).ToList();
        _settingsPersistence.Save();
    }

    private void SetError(string message)
    {
        ErrorText = message;
        HasError = true;
    }

    private void ClearError()
    {
        ErrorText = string.Empty;
        HasError = false;
    }

    public System.Collections.ObjectModel.ObservableCollection<SecondaryOcrRegionEntry> SecondaryOcrRegions { get; } = [];

    public ICommand AddSecondaryOcrRegionCommand { get; private set; } = null!;
    public Commands.ParameterizedAsyncRelayCommand RemoveSecondaryOcrRegionCommand { get; private set; } = null!;
}

public sealed class SecondaryOcrRegionEntry : ObservableObject
{
    private string _label = "Bölge";
    private double _xPercent;
    private double _yPercent = 0.8;
    private double _widthPercent = 1.0;
    private double _heightPercent = 0.15;
    private bool _isEnabled = true;
    private bool _useForSpeakerName;

    public string Label { get => _label; set => SetProperty(ref _label, value); }
    public double XPercent { get => _xPercent; set => SetProperty(ref _xPercent, Math.Clamp(value, 0, 1)); }
    public double YPercent { get => _yPercent; set => SetProperty(ref _yPercent, Math.Clamp(value, 0, 1)); }
    public double WidthPercent { get => _widthPercent; set => SetProperty(ref _widthPercent, Math.Clamp(value, 0.01, 1)); }
    public double HeightPercent { get => _heightPercent; set => SetProperty(ref _heightPercent, Math.Clamp(value, 0.01, 1)); }
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public bool UseForSpeakerName { get => _useForSpeakerName; set => SetProperty(ref _useForSpeakerName, value); }

    public static SecondaryOcrRegionEntry FromSettings(SecondaryOcrRegionSettings settings) => new()
    {
        Label = settings.Label,
        IsEnabled = settings.IsEnabled,
        UseForSpeakerName = settings.UseForSpeakerName,
        XPercent = settings.XPercent,
        YPercent = settings.YPercent,
        WidthPercent = settings.WidthPercent,
        HeightPercent = settings.HeightPercent,
    };

    public SecondaryOcrRegionSettings ToSettings() => new()
    {
        Label = Label,
        IsEnabled = IsEnabled,
        UseForSpeakerName = UseForSpeakerName,
        XPercent = XPercent,
        YPercent = YPercent,
        WidthPercent = WidthPercent,
        HeightPercent = HeightPercent,
    };
}
