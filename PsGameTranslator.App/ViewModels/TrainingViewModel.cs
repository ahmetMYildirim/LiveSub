using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PsGameTranslator.App.Commands;
using PsGameTranslator.App.Services;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Translation;

namespace PsGameTranslator.App.ViewModels;

/// <summary>
/// Hidden "Egitim" page: fine-tunes the local OPUS-MT model on the
/// DeepL/Google Translate dataset collected in the background by
/// TranslationDatasetCollector. Gated behind Developer Mode in the sidebar
/// plus its own PIN (see TrainingAccessService) — this ViewModel itself does
/// not enforce access, the page only becomes reachable once both are satisfied.
/// </summary>
public sealed class TrainingViewModel : ObservableObject
{
    private static readonly string DatasetDir = Path.Combine(AppContext.BaseDirectory, "data", "training");
    private static readonly string DefaultOutputDir = Path.Combine(AppContext.BaseDirectory, "models", "opus-mt-finetuned");

    private readonly TrainingService _trainingService;
    private readonly TrainingAccessService _accessService;
    private readonly TranslationDatasetCollector _datasetCollector;
    private readonly TranslationSettings _translationSettings;
    private readonly ITranslationLearningService _learningService;
    private readonly MachineTranslationProvider _machineTranslation;
    private readonly IReadOnlyList<ITranslationProvider> _optionalProviders;
    private readonly ILogger<TrainingViewModel> _logger;
    private readonly SynchronizationContext _uiContext;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _compareCts;

    // ── Access gate ───────────────────────────────────────────────────────────
    private bool _isUnlocked;
    private string _pinAttempt = string.Empty;
    private string _pinErrorText = string.Empty;

    // ── Dataset ───────────────────────────────────────────────────────────────
    private int _deepLCount;
    private int _googleCount;
    private bool _useDeepLDataset = true;
    private bool _useGoogleDataset = true;

    // ── Reviewed stats (Item 12) ─────────────────────────────────────────────
    private int _acceptedCount;
    private int _editedCount;
    private int _rejectedCount;
    private int _exportableCount;

    // ── Quick retrain (Item 6) ───────────────────────────────────────────────
    private bool _isQuickRetraining;

    // ── Model comparison (Item 15) ───────────────────────────────────────────
    private string _compareSourceText = string.Empty;
    private bool _isComparing;
    private string _comparisonResultsText = string.Empty;

    // ── Hyperparameters ──────────────────────────────────────────────────────
    private int _epochs = 5;
    private int _batchSize = 8;
    private double _learningRate = 0.0002;
    private int _maxLength = 128;
    private int _valSplitPercent = 10;
    private int _loraR = 16;
    private int _loraAlpha = 32;

    // ── Live run state ───────────────────────────────────────────────────────
    private bool _isTraining;
    private bool _isEstimating;
    private string _statusText = "Hazir";
    private double _currentEpoch;
    private int _totalEpochs;
    private long _currentStep;
    private long _totalSteps;
    private double _trainLoss;
    private double? _evalLoss;
    private double? _bleuScore;
    private double? _precision;
    private double? _recall;
    private double? _f1Score;
    private double? _testBleuScore;
    private double? _testPrecision;
    private double? _testRecall;
    private double? _testF1Score;
    private long _testPairCount;
    private long _estimatedVramMb;
    private long _currentVramAllocatedMb;
    private long _currentVramReservedMb;
    private long _totalVramMb;
    private long _currentRamMb;
    private long _totalRamMb;
    private string _outputDirText = string.Empty;

    public ObservableCollection<string> LogLines { get; } = [];

    public TrainingViewModel(
        TrainingService trainingService,
        TrainingAccessService accessService,
        TranslationDatasetCollector datasetCollector,
        TranslationSettings translationSettings,
        ITranslationLearningService learningService,
        MachineTranslationProvider machineTranslationProvider,
        IEnumerable<ITranslationProvider> optionalProviders,
        ILogger<TrainingViewModel> logger)
    {
        _trainingService = trainingService;
        _accessService = accessService;
        _datasetCollector = datasetCollector;
        _translationSettings = translationSettings;
        _learningService = learningService;
        _machineTranslation = machineTranslationProvider;
        _optionalProviders = optionalProviders.ToList();
        _logger = logger;
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TrainingViewModel must be created on the UI thread.");

        _trainingService.LogLineReceived += OnLogLine;
        _trainingService.ProgressReceived += OnProgress;

        UnlockCommand = new AsyncRelayCommand(UnlockAsync);
        RefreshDatasetCountsCommand = new AsyncRelayCommand(RefreshDatasetCountsAsync);
        RefreshReviewedStatsCommand = new AsyncRelayCommand(RefreshReviewedStatsAsync);
        EstimateVramCommand = new AsyncRelayCommand(EstimateVramAsync, () => !IsTraining && !IsEstimating && !IsQuickRetraining);
        StartTrainingCommand = new AsyncRelayCommand(StartTrainingAsync, () => !IsTraining && !IsEstimating && !IsQuickRetraining);
        StopTrainingCommand = new AsyncRelayCommand(StopTrainingAsync, () => IsTraining || IsQuickRetraining);
        QuickRetrainCommand = new AsyncRelayCommand(QuickRetrainAsync, () => !IsTraining && !IsEstimating && !IsQuickRetraining && ExportableCount > 0);
        RunComparisonCommand = new AsyncRelayCommand(RunComparisonAsync, () => !IsComparing && !string.IsNullOrWhiteSpace(CompareSourceText));

        RefreshDatasetCountsAsync().ConfigureAwait(false);
        RefreshReviewedStatsAsync().ConfigureAwait(false);
    }

    // ── Access gate ───────────────────────────────────────────────────────────

    public bool IsUnlocked { get => _isUnlocked; private set => SetProperty(ref _isUnlocked, value); }
    public string PinAttempt { get => _pinAttempt; set => SetProperty(ref _pinAttempt, value); }
    public string PinErrorText { get => _pinErrorText; private set => SetProperty(ref _pinErrorText, value); }
    public ICommand UnlockCommand { get; }

    private Task UnlockAsync()
    {
        if (_accessService.ValidatePin(PinAttempt))
        {
            IsUnlocked = true;
            PinErrorText = string.Empty;
        }
        else
        {
            PinErrorText = "Yanlis PIN.";
        }
        PinAttempt = string.Empty;
        return Task.CompletedTask;
    }

    // ── Dataset ───────────────────────────────────────────────────────────────

    public int DeepLCount { get => _deepLCount; private set => SetProperty(ref _deepLCount, value); }
    public int GoogleCount { get => _googleCount; private set => SetProperty(ref _googleCount, value); }
    public int TotalDatasetCount => (UseDeepLDataset ? DeepLCount : 0) + (UseGoogleDataset ? GoogleCount : 0);

    public bool UseDeepLDataset
    {
        get => _useDeepLDataset;
        set { if (SetProperty(ref _useDeepLDataset, value)) RaisePropertyChanged(nameof(TotalDatasetCount)); }
    }

    public bool UseGoogleDataset
    {
        get => _useGoogleDataset;
        set { if (SetProperty(ref _useGoogleDataset, value)) RaisePropertyChanged(nameof(TotalDatasetCount)); }
    }

    public ICommand RefreshDatasetCountsCommand { get; }

    private Task RefreshDatasetCountsAsync()
    {
        var (deepL, google) = _datasetCollector.GetCounts();
        DeepLCount = deepL;
        GoogleCount = google;
        RaisePropertyChanged(nameof(TotalDatasetCount));
        return Task.CompletedTask;
    }

    // ── Reviewed stats (Item 12) ─────────────────────────────────────────────

    public int AcceptedCount { get => _acceptedCount; private set => SetProperty(ref _acceptedCount, value); }
    public int EditedCount { get => _editedCount; private set => SetProperty(ref _editedCount, value); }
    public int RejectedCount { get => _rejectedCount; private set => SetProperty(ref _rejectedCount, value); }

    public int ExportableCount
    {
        get => _exportableCount;
        private set
        {
            if (SetProperty(ref _exportableCount, value))
                ((AsyncRelayCommand)QuickRetrainCommand).NotifyCanExecuteChanged();
        }
    }

    public ICommand RefreshReviewedStatsCommand { get; }

    private async Task RefreshReviewedStatsAsync()
    {
        try
        {
            var accepted = await _learningService.GetCountByStatusAsync(TranslationRecordStatus.AcceptedByUser).ConfigureAwait(false);
            var edited   = await _learningService.GetCountByStatusAsync(TranslationRecordStatus.EditedByUser).ConfigureAwait(false);
            var rejected = await _learningService.GetCountByStatusAsync(TranslationRecordStatus.RejectedByUser).ConfigureAwait(false);
            _uiContext.Post(_ =>
            {
                AcceptedCount   = accepted;
                EditedCount     = edited;
                RejectedCount   = rejected;
                ExportableCount = accepted + edited;
            }, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "reviewed_stats_refresh_failed");
        }
    }

    // ── Quick retrain (Item 6) ───────────────────────────────────────────────

    public bool IsQuickRetraining
    {
        get => _isQuickRetraining;
        private set
        {
            if (!SetProperty(ref _isQuickRetraining, value)) return;
            ((AsyncRelayCommand)EstimateVramCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)StartTrainingCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)StopTrainingCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)QuickRetrainCommand).NotifyCanExecuteChanged();
        }
    }

    public ICommand QuickRetrainCommand { get; }

    private async Task QuickRetrainAsync()
    {
        IsQuickRetraining = true;
        StatusText = "Incelenen kayitlar disa aktariliyor...";
        LogLines.Clear();
        try
        {
            var reviewedPath = Path.Combine(DatasetDir, "dataset_reviewed.jsonl");
            Directory.CreateDirectory(DatasetDir);
            var (exported, skipped, _) = await _learningService
                .ExportJsonlAsync(reviewedPath)
                .ConfigureAwait(false);

            if (exported == 0)
            {
                _uiContext.Post(_ => StatusText = "Egitim icin yeterli kabul/duzenleme kaydı bulunamadi.", null);
                return;
            }

            _uiContext.Post(_ => StatusText = $"{exported} cift aktarildi ({skipped} atlandi) — egitim baslatiliyor...", null);

            _runCts = new CancellationTokenSource();
            var options = new TrainingRunOptions(
                DatasetFiles: [reviewedPath],
                TestFiles: TestFiles(),
                OutputDir: DefaultOutputDir,
                Epochs: Epochs,
                BatchSize: BatchSize,
                LearningRate: LearningRate,
                MaxLength: MaxLength,
                ValSplit: ValSplitPercent / 100.0,
                LoraR: LoraR,
                LoraAlpha: LoraAlpha,
                EstimateOnly: false);

            OutputDirText = DefaultOutputDir;
            var exitCode = await _trainingService.RunAsync(options, _runCts.Token).ConfigureAwait(false);
            _uiContext.Post(_ =>
            {
                StatusText = exitCode == 0
                    ? "Hizli yeniden egitim tamamlandi."
                    : "Hizli egitim hata ile sona erdi.";
            }, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "quick_retrain_failed");
            _uiContext.Post(_ => StatusText = $"Hizli egitim baslatilamadi: {ex.Message}", null);
        }
        finally
        {
            _uiContext.Post(_ => IsQuickRetraining = false, null);
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    // ── Model comparison (Item 15) ───────────────────────────────────────────

    public string CompareSourceText
    {
        get => _compareSourceText;
        set
        {
            if (SetProperty(ref _compareSourceText, value))
                ((AsyncRelayCommand)RunComparisonCommand).NotifyCanExecuteChanged();
        }
    }

    public bool IsComparing
    {
        get => _isComparing;
        private set
        {
            if (SetProperty(ref _isComparing, value))
                ((AsyncRelayCommand)RunComparisonCommand).NotifyCanExecuteChanged();
        }
    }

    public string ComparisonResultsText
    {
        get => _comparisonResultsText;
        private set
        {
            if (SetProperty(ref _comparisonResultsText, value))
                RaisePropertyChanged(nameof(HasComparisonResults));
        }
    }

    public bool HasComparisonResults => !string.IsNullOrEmpty(_comparisonResultsText);
    public ICommand RunComparisonCommand { get; }

    private async Task RunComparisonAsync()
    {
        var text = CompareSourceText.Trim();
        if (string.IsNullOrEmpty(text)) return;

        IsComparing = true;
        ComparisonResultsText = "Karsilastirma calistiriliyor...";
        _compareCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var request = new TranslationRequest
        {
            SourceText = text,
            SourceLanguage = "en",
            TargetLanguage = "tr",
        };

        var sb = new StringBuilder();
        var allProviders = new List<ITranslationProvider> { _machineTranslation };
        allProviders.AddRange(_optionalProviders);

        try
        {
            foreach (var provider in allProviders)
            {
                try
                {
                    var result = await provider.TranslateAsync(request, _compareCts.Token).ConfigureAwait(false);
                    sb.AppendLine($"[{provider.ProviderName}]");
                    sb.AppendLine(result.TranslatedText);
                    sb.AppendLine();
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"[{provider.ProviderName}] HATA: {ex.Message}");
                    sb.AppendLine();
                }
            }
        }
        finally
        {
            _compareCts?.Dispose();
            _compareCts = null;
            var output = sb.ToString().TrimEnd();
            _uiContext.Post(_ =>
            {
                ComparisonResultsText = string.IsNullOrEmpty(output) ? "Sonuc alinamadi." : output;
                IsComparing = false;
            }, null);
        }
    }

    // ── Hyperparameters ──────────────────────────────────────────────────────

    public int Epochs { get => _epochs; set => SetProperty(ref _epochs, Math.Max(1, value)); }
    public int BatchSize { get => _batchSize; set => SetProperty(ref _batchSize, Math.Max(1, value)); }
    public double LearningRate { get => _learningRate; set => SetProperty(ref _learningRate, value); }
    public int MaxLength { get => _maxLength; set => SetProperty(ref _maxLength, Math.Max(16, value)); }
    public int ValSplitPercent { get => _valSplitPercent; set => SetProperty(ref _valSplitPercent, Math.Clamp(value, 1, 50)); }
    public int LoraR { get => _loraR; set => SetProperty(ref _loraR, Math.Max(1, value)); }
    public int LoraAlpha { get => _loraAlpha; set => SetProperty(ref _loraAlpha, Math.Max(1, value)); }

    // ── Live run state ───────────────────────────────────────────────────────

    public bool IsTraining
    {
        get => _isTraining;
        private set
        {
            if (!SetProperty(ref _isTraining, value)) return;
            ((AsyncRelayCommand)EstimateVramCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)StartTrainingCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)StopTrainingCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)QuickRetrainCommand).NotifyCanExecuteChanged();
        }
    }

    public bool IsEstimating
    {
        get => _isEstimating;
        private set
        {
            if (!SetProperty(ref _isEstimating, value)) return;
            ((AsyncRelayCommand)EstimateVramCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)StartTrainingCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)QuickRetrainCommand).NotifyCanExecuteChanged();
        }
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public double CurrentEpoch { get => _currentEpoch; private set => SetProperty(ref _currentEpoch, value); }
    public int TotalEpochs { get => _totalEpochs; private set => SetProperty(ref _totalEpochs, value); }
    public long CurrentStep { get => _currentStep; private set => SetProperty(ref _currentStep, value); }
    public long TotalSteps { get => _totalSteps; private set => SetProperty(ref _totalSteps, value); }
    public double TrainLoss { get => _trainLoss; private set => SetProperty(ref _trainLoss, value); }
    public double? EvalLoss { get => _evalLoss; private set => SetProperty(ref _evalLoss, value); }
    public double? BleuScore { get => _bleuScore; private set => SetProperty(ref _bleuScore, value); }
    public double? Precision { get => _precision; private set => SetProperty(ref _precision, value); }
    public double? Recall { get => _recall; private set => SetProperty(ref _recall, value); }
    public double? F1Score { get => _f1Score; private set => SetProperty(ref _f1Score, value); }

    public double? TestBleuScore { get => _testBleuScore; private set => SetProperty(ref _testBleuScore, value); }
    public double? TestPrecision { get => _testPrecision; private set => SetProperty(ref _testPrecision, value); }
    public double? TestRecall { get => _testRecall; private set => SetProperty(ref _testRecall, value); }
    public double? TestF1Score { get => _testF1Score; private set => SetProperty(ref _testF1Score, value); }
    public long TestPairCount { get => _testPairCount; private set => SetProperty(ref _testPairCount, value); }

    public long EstimatedVramMb { get => _estimatedVramMb; private set => SetProperty(ref _estimatedVramMb, value); }
    public long CurrentVramAllocatedMb { get => _currentVramAllocatedMb; private set => SetProperty(ref _currentVramAllocatedMb, value); }
    public long CurrentVramReservedMb { get => _currentVramReservedMb; private set => SetProperty(ref _currentVramReservedMb, value); }
    public long TotalVramMb { get => _totalVramMb; private set => SetProperty(ref _totalVramMb, value); }
    public long CurrentRamMb { get => _currentRamMb; private set => SetProperty(ref _currentRamMb, value); }
    public long TotalRamMb { get => _totalRamMb; private set => SetProperty(ref _totalRamMb, value); }
    public string OutputDirText { get => _outputDirText; private set => SetProperty(ref _outputDirText, value); }

    public ICommand EstimateVramCommand { get; }
    public ICommand StartTrainingCommand { get; }
    public ICommand StopTrainingCommand { get; }

    private async Task EstimateVramAsync()
    {
        IsEstimating = true;
        StatusText = "VRAM tahmini hesaplaniyor...";
        LogLines.Clear();
        try
        {
            var options = BuildOptions(estimateOnly: true);
            await _trainingService.RunAsync(options, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _uiContext.Post(_ =>
            {
                IsEstimating = false;
                StatusText = "Hazir";
            }, null);
        }
    }

    private async Task StartTrainingAsync()
    {
        var files = SelectedDatasetFiles();
        if (files.Count == 0)
        {
            StatusText = "En az bir veri seti kaynagi secin (DeepL veya Google).";
            return;
        }

        LogLines.Clear();
        CurrentEpoch = 0;
        CurrentStep = 0;
        TotalSteps = 0;
        TrainLoss = 0;
        EvalLoss = null;
        BleuScore = null;
        Precision = null;
        Recall = null;
        F1Score = null;
        IsTraining = true;
        StatusText = "Egitim basliyor...";

        _runCts = new CancellationTokenSource();
        try
        {
            var options = BuildOptions(estimateOnly: false);
            var exitCode = await _trainingService.RunAsync(options, _runCts.Token).ConfigureAwait(false);
            _uiContext.Post(_ =>
            {
                StatusText = exitCode == 0 ? "Egitim tamamlandi." : "Egitim hata ile sona erdi (yukaridaki loga bakin).";
            }, null);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "training_start_failed");
            _uiContext.Post(_ => StatusText = $"Egitim baslatilamadi: {exception.Message}", null);
        }
        finally
        {
            _uiContext.Post(_ => IsTraining = false, null);
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private Task StopTrainingAsync()
    {
        try
        {
            _runCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run may have completed while the user clicked Stop.
        }

        _trainingService.Stop();
        StatusText = "Durduruluyor...";
        return Task.CompletedTask;
    }

    private List<string> SelectedDatasetFiles()
    {
        var files = new List<string>();
        if (UseDeepLDataset) files.Add(Path.Combine(DatasetDir, "dataset_deepl.jsonl"));
        // Google is reserved as the held-out test set — never included in training data.
        return files.Where(File.Exists).ToList();
    }

    private List<string> TestFiles()
    {
        var google = Path.Combine(DatasetDir, "dataset_google.jsonl");
        return File.Exists(google) ? [google] : [];
    }

    private TrainingRunOptions BuildOptions(bool estimateOnly)
    {
        OutputDirText = DefaultOutputDir;
        return new TrainingRunOptions(
            DatasetFiles: estimateOnly ? [] : SelectedDatasetFiles(),
            TestFiles: estimateOnly ? [] : TestFiles(),
            OutputDir: DefaultOutputDir,
            Epochs: Epochs,
            BatchSize: BatchSize,
            LearningRate: LearningRate,
            MaxLength: MaxLength,
            ValSplit: ValSplitPercent / 100.0,
            LoraR: LoraR,
            LoraAlpha: LoraAlpha,
            EstimateOnly: estimateOnly);
    }

    // ── Event handlers (background thread — marshal to UI) ──────────────────

    private void OnLogLine(string line)
    {
        _uiContext.Post(_ =>
        {
            LogLines.Add(line);
            if (LogLines.Count > 2000)
                LogLines.RemoveAt(0);
        }, null);
    }

    private void OnProgress(JsonElement payload)
    {
        _uiContext.Post(_ => ApplyProgress(payload), null);
    }

    private void ApplyProgress(JsonElement payload)
    {
        if (!payload.TryGetProperty("type", out var typeProp)) return;
        var type = typeProp.GetString();

        switch (type)
        {
            case "vram_estimate":
                EstimatedVramMb = GetLong(payload, "estimated_total_mb");
                StatusText = $"Tahmini VRAM kullanimi: ~{EstimatedVramMb} MB";
                break;
            case "dataset_loaded":
                var total = GetLong(payload, "total_pairs");
                StatusText = $"{total} cift yuklendi.";
                break;
            case "dataset_split":
                var trainPairs = GetLong(payload, "train_pairs");
                var valPairs = GetLong(payload, "val_pairs");
                StatusText = $"Egitim: {trainPairs} cift, Dogrulama: {valPairs} cift";
                break;
            case "lora_ready":
                var trainable = GetLong(payload, "trainable_params");
                StatusText = $"LoRA hazir — {trainable:N0} egitilebilir parametre";
                break;
            case "training_started":
                TotalEpochs = (int)GetLong(payload, "epochs");
                StatusText = "Egitim devam ediyor...";
                break;
            case "train_progress":
                CurrentEpoch = GetDouble(payload, "epoch");
                TotalEpochs = (int)GetLong(payload, "total_epochs");
                CurrentStep = GetLong(payload, "step");
                TotalSteps = GetLong(payload, "total_steps");
                TrainLoss = GetDouble(payload, "train_loss");
                break;
            case "eval_progress":
                EvalLoss = GetDouble(payload, "eval_loss");
                break;
            case "vram_usage":
                CurrentVramAllocatedMb = GetLong(payload, "allocated_mb");
                CurrentVramReservedMb = GetLong(payload, "reserved_mb");
                TotalVramMb = GetLong(payload, "total_mb");
                break;
            case "ram_usage":
                CurrentRamMb = GetLong(payload, "used_mb");
                TotalRamMb = GetLong(payload, "total_mb");
                break;
            case "eval_metrics":
                BleuScore = GetDouble(payload, "bleu");
                Precision = GetDouble(payload, "precision");
                Recall = GetDouble(payload, "recall");
                F1Score = GetDouble(payload, "f1");
                StatusText = $"Dogrulama tamamlandi — BLEU {BleuScore:F1}";
                break;
            case "test_metrics":
                TestBleuScore = GetDouble(payload, "bleu");
                TestPrecision = GetDouble(payload, "precision");
                TestRecall = GetDouble(payload, "recall");
                TestF1Score = GetDouble(payload, "f1");
                TestPairCount = GetLong(payload, "test_pairs");
                StatusText = $"Test seti degerlendirmesi tamamlandi — BLEU {TestBleuScore:F1}";
                break;
            case "done":
                OutputDirText = payload.TryGetProperty("output_dir", out var dir) ? dir.GetString() ?? string.Empty : OutputDirText;
                StatusText = $"Model kaydedildi: {OutputDirText}";
                break;
            case "error":
                var message = payload.TryGetProperty("message", out var msg) ? msg.GetString() : "Bilinmeyen hata";
                StatusText = $"Hata: {message}";
                break;
        }
    }

    private static long GetLong(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var l) ? l : 0;

    private static double GetDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetDouble(out var d) ? d : 0;
}
