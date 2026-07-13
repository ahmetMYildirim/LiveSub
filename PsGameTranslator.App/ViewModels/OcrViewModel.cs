using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using PsGameTranslator.App.Commands;
using PsGameTranslator.App.Services;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Ocr;
using PsGameTranslator.Ocr;
using PsGameTranslator.Overlay;

namespace PsGameTranslator.App.ViewModels;

public sealed class OcrViewModel : ObservableObject
{
    private readonly OcrEngineManager _ocrEngineManager;
    private readonly OcrEngineSettings _ocrEngineSettings;
    private readonly OcrTextCleaner _textCleaner;
    private readonly OcrResultCache _cache;
    private readonly IOcrProcessingSettings _settings;
    private readonly IOverlayService _overlayService;
    private readonly IOcrServerService _ocrServer;
    private readonly UserSettingsPersistenceService _persistence;
    private readonly SystemRequirementsService _systemRequirements;
    private readonly ILogger<OcrViewModel> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private bool _isCheckingSystemRequirements;

    private BitmapSource? _regionPreview;
    private string _rawOcrText = string.Empty;
    private string _cleanedOcrText = string.Empty;
    private string _confidenceText = string.Empty;
    private string _changeStatus = "Not run";
    private string _lastOcrTimeText = "Never";
    private string _statusText = "Click 'Run OCR' to recognize text from the selected region.";
    private string _errorDetail = string.Empty;
    private bool _hasError;

    private static readonly string OcrRegionPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "samples", "ocr_region.png"));
    private static readonly string DebugDirectory = Path.Combine(AppContext.BaseDirectory, "debug");

    public OcrViewModel(
        OcrEngineManager ocrEngineManager,
        OcrEngineSettings ocrEngineSettings,
        OcrTextCleaner textCleaner,
        OcrResultCache cache,
        IOcrProcessingSettings settings,
        IOverlayService overlayService,
        IOcrServerService ocrServer,
        UserSettingsPersistenceService persistence,
        SystemRequirementsService systemRequirements,
        ILogger<OcrViewModel> logger)
    {
        _ocrEngineManager = ocrEngineManager;
        _ocrEngineSettings = ocrEngineSettings;
        _textCleaner = textCleaner;
        _cache = cache;
        _settings = settings;
        _overlayService = overlayService;
        _ocrServer = ocrServer;
        _persistence = persistence;
        _systemRequirements = systemRequirements;
        _logger = logger;

        RunOcrCommand = new AsyncRelayCommand(() => RunOcrAsync(force: false));
        ForceOcrCommand = new AsyncRelayCommand(() => RunOcrAsync(force: true));
        ForceOcrDebugCommand = new AsyncRelayCommand(() => RunOcrAsync(force: true, debug: true));
        SaveOcrDebugCropCommand = new AsyncRelayCommand(SaveOcrDebugCropAsync);
        TestSelectedOcrProviderCommand = new AsyncRelayCommand(TestSelectedOcrProviderAsync);
        TestAllOcrProvidersCommand = new AsyncRelayCommand(TestAllOcrProvidersAsync);
        OpenOcrDebugFolderCommand = new AsyncRelayCommand(OpenOcrDebugFolderAsync);
        RefreshSystemRequirementsCommand = new AsyncRelayCommand(
            RefreshSystemRequirementsAsync, () => !_isCheckingSystemRequirements);

        _ = RefreshSystemRequirementsAsync();
    }

    public BitmapSource? RegionPreview
    {
        get => _regionPreview;
        private set => SetProperty(ref _regionPreview, value);
    }

    public string RawOcrText
    {
        get => _rawOcrText;
        private set => SetProperty(ref _rawOcrText, value);
    }

    public string CleanedOcrText
    {
        get => _cleanedOcrText;
        private set => SetProperty(ref _cleanedOcrText, value);
    }

    public string ConfidenceText
    {
        get => _confidenceText;
        private set => SetProperty(ref _confidenceText, value);
    }

    public string ChangeStatus
    {
        get => _changeStatus;
        private set => SetProperty(ref _changeStatus, value);
    }

    public string LastOcrTimeText
    {
        get => _lastOcrTimeText;
        private set => SetProperty(ref _lastOcrTimeText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ErrorDetail
    {
        get => _errorDetail;
        private set => SetProperty(ref _errorDetail, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public ObservableCollection<OcrLineItem> Lines { get; } = [];

    // ── System requirements ──────────────────────────────────────────────────

    public ObservableCollection<SystemRequirementCheck> SystemRequirements { get; } = [];
    public ICommand RefreshSystemRequirementsCommand { get; }

    private async Task RefreshSystemRequirementsAsync()
    {
        _isCheckingSystemRequirements = true;
        try
        {
            var results = await _systemRequirements.CheckAllAsync();
            SystemRequirements.Clear();
            foreach (var result in results)
                SystemRequirements.Add(result);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "system_requirements_check_failed");
        }
        finally
        {
            _isCheckingSystemRequirements = false;
        }
    }

    public int OcrProfileIndex
    {
        get => (int)_ocrEngineSettings.Profile;
        set
        {
            var profile = (OcrProfile)Math.Clamp(value, 0, 3);
            if (_ocrEngineSettings.Profile == profile) return;
            _ocrEngineSettings.Profile = profile;
            _ocrEngineSettings.ApplyProfileDefaults();
            OnPropertyChanged();
            OnPropertyChanged(nameof(OcrProviderTypeIndex));
            OnPropertyChanged(nameof(OcrExecutionModeText));
            StatusText = $"OCR profile: {profile}";
        }
    }

    public int OcrProviderTypeIndex
    {
        get => (int)_ocrEngineSettings.PreferredProvider;
        set
        {
            var providerType = (OcrProviderType)Math.Clamp(value, 0, 6);
            if (_ocrEngineSettings.PreferredProvider == providerType) return;
            _ocrEngineSettings.PreferredProvider = providerType;
            _ocrEngineSettings.Profile = OcrProfile.Custom;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OcrProfileIndex));
            StatusText = $"OCR provider: {providerType}";
            AutoStartServerForSelection(providerType);
        }
    }

    /// <summary>
    /// PaddleOCR compute device. Changing this restarts the OCR server (the
    /// device is chosen at Python process startup, so it cannot be swapped on
    /// a running server) — the currently selected OCR engine keeps translating
    /// as soon as the server comes back up.
    /// </summary>
    public int OcrDeviceIndex
    {
        get => (int)_ocrEngineSettings.Device;
        set
        {
            var device = (OcrDeviceMode)Math.Clamp(value, 0, 2);
            if (_ocrEngineSettings.Device == device) return;
            _ocrEngineSettings.Device = device;
            OnPropertyChanged();
            _persistence.Save();
            _ = RestartOcrServerForDeviceChangeAsync(device);
        }
    }

    private async Task RestartOcrServerForDeviceChangeAsync(OcrDeviceMode device)
    {
        if (_ocrServer.IsRunningExternally)
        {
            // Started outside this app (e.g. run manually for testing) — this app
            // never owns that process, so it cannot restart it with the new
            // --device flag. Close it manually and let the app start its own.
            StatusText = $"OCR islemcisi {device} olarak kaydedildi, ancak sunucu harici baslatilmis — " +
                "etkili olmasi icin OCR sunucusunu elle kapatip uygulamayi yeniden baslatin.";
            return;
        }

        StatusText = $"OCR islemcisi: {device} — sunucu yeniden baslatiliyor…";
        try
        {
            await _ocrServer.StopAsync();
            var (success, message) = await _ocrServer.EnsureRunningAsync();
            StatusText = success
                ? $"OCR islemcisi: {device} — sunucu {_ocrServer.ServerBaseUrl} adresinde calisiyor."
                : $"OCR islemcisi: {device} — sunucu baslatilamadi: {message}";
            if (!success)
                _logger.LogWarning("OCR server restart for device change failed: {Message}", message);
        }
        catch (Exception exception)
        {
            StatusText = $"OCR islemcisi degistirilirken hata olustu: {exception.Message}";
            _logger.LogWarning(exception, "ocr_device_change_restart_failed - device={Device}", device);
        }
    }

    // How long a single OCR server request is allowed to run before it's counted
    // as failed. Too low and every request on a normal-sized subtitle crop times
    // out — no text ever comes back, so nothing reaches translation either.
    // Editing this switches the profile to Custom so it survives future profile
    // changes instead of being silently reset by ApplyProfileDefaults().
    public int OcrTimeoutMs
    {
        get => _ocrEngineSettings.OcrTimeoutMs;
        set
        {
            var clamped = Math.Max(500, value);
            if (_ocrEngineSettings.OcrTimeoutMs == clamped) return;
            _ocrEngineSettings.OcrTimeoutMs = clamped;
            _ocrEngineSettings.Profile = OcrProfile.Custom;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OcrProfileIndex));
        }
    }

    /// <summary>
    /// Selecting a server-backed engine starts its server automatically (when enabled),
    /// so "selected provider" and "actually used provider" stay in sync.
    /// </summary>
    private void AutoStartServerForSelection(OcrProviderType providerType)
    {
        if (!_ocrEngineSettings.AutoStartOcrServer) return;
        if (!OcrServerService.IsServerBackedProvider(providerType)) return;
        if (_ocrServer.IsRunning) return;

        StatusText = $"OCR provider: {providerType} — starting OCR server…";
        _ = Task.Run(async () =>
        {
            var (success, message) = await _ocrServer.EnsureRunningAsync();
            StatusText = success
                ? $"OCR provider: {providerType} — server running at {_ocrServer.ServerBaseUrl}."
                : $"OCR provider: {providerType} — server failed to start: {message}";
            if (!success)
                _logger.LogWarning("OCR server auto-start on selection failed: {Message}", message);
        });
    }

    public string OcrExecutionModeText => _ocrEngineSettings.ExecutionMode.ToString();
    public string LastActualOcrProviderText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_ocrEngineManager.LastProviderUsed))
                return "-";
            if (_ocrEngineManager.LastFallbackUsed)
                return $"⚠ {_ocrEngineManager.LastProviderUsed} (fallback)";
            if (!string.IsNullOrEmpty(_ocrEngineManager.LastSelectionNote))
                return $"⚠ {_ocrEngineManager.LastProviderUsed} (subprocess)";
            return _ocrEngineManager.LastProviderUsed;
        }
    }
    public string LastBestOcrScoreText => _ocrEngineManager.LastBestScore.ToString("F1");

    public ICommand RunOcrCommand { get; }

    public ICommand ForceOcrCommand { get; }
    public ICommand ForceOcrDebugCommand { get; }
    public ICommand SaveOcrDebugCropCommand { get; }
    public ICommand TestSelectedOcrProviderCommand { get; }
    public ICommand TestAllOcrProvidersCommand { get; }
    public ICommand OpenOcrDebugFolderCommand { get; }

    private async Task RunOcrAsync(bool force, bool debug = false)
    {
        if (!await _runLock.WaitAsync(0))
        {
            StatusText = "OCR is already running.";
            return;
        }

        try
        {
            await RunOcrCoreAsync(force, debug);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task RunOcrCoreAsync(bool force, bool debug)
    {
        ClearError();

        if (!File.Exists(OcrRegionPath))
        {
            StatusText = "No OCR region image found - crop a region first.";
            ShowError($"File not found:\n  {OcrRegionPath}");
            _logger.LogWarning("OCR region image not found at {Path}", OcrRegionPath);
            await SaveFailureReasonAsync(OcrFailureReason.InvalidImage, providerFailed: false, providerError: string.Empty);
            return;
        }

        byte[] imageBytes;
        try
        {
            imageBytes = await File.ReadAllBytesAsync(OcrRegionPath);
        }
        catch (Exception exception)
        {
            StatusText = "Failed to load region image.";
            ShowError(exception.Message);
            _logger.LogError(exception, "Failed to read OCR region image from {Path}", OcrRegionPath);
            await SaveFailureReasonAsync(OcrFailureReason.InvalidImage, providerFailed: false, providerError: exception.Message);
            return;
        }

        var now = DateTimeOffset.Now;
        var imageHash = OcrResultCache.ComputeImageHash(imageBytes);

        if (!force && _cache.IsInsideInterval(now, _settings.OcrIntervalMilliseconds))
        {
            ChangeStatus = "Unchanged";
            StatusText = "OCR skipped - configured interval has not elapsed.";
            _logger.LogInformation(
                "OCR skipped because interval has not elapsed ({IntervalMilliseconds} ms)",
                _settings.OcrIntervalMilliseconds);
            await SaveFailureReasonAsync(
                OcrFailureReason.DuplicateSkipped,
                providerFailed: false,
                providerError: string.Empty,
                rejectedAsDuplicate: true);
            return;
        }

        if (_settings.EnableOcrCache && !force)
        {
            if (_cache.IsSameImage(imageHash))
            {
                ChangeStatus = "Unchanged";
                StatusText = "OCR skipped - cropped image is unchanged.";
                _logger.LogInformation("OCR skipped because same image");
                await SaveFailureReasonAsync(
                    OcrFailureReason.DuplicateSkipped,
                    providerFailed: false,
                    providerError: string.Empty,
                    rejectedAsDuplicate: true);
                return;
            }
        }

        RegionPreview = ToBitmapSource(imageBytes);
        StatusText = debug ? "Force OCR Debug is running..." : force ? "Force OCR is running..." : "OCR is running...";
        _logger.LogInformation("OCR started on {Path}; force={Force}", OcrRegionPath, force);

        try
        {
            if (debug)
                await SaveDebugImagesAsync(imageBytes);

            var result = await _ocrEngineManager.RecognizeAsync(new OcrRequest
            {
                ImageBytes = imageBytes,
                Language = "en",
                RegionId = "manual-ocr-tab",
                PreprocessingSettings = new PreprocessingSettings
                {
                    Preset = _ocrEngineSettings.PreprocessingPreset,
                },
                ForceOcr = force,
                DebugMode = debug
            });
            var completedAt = DateTimeOffset.Now;
            OnPropertyChanged(nameof(LastActualOcrProviderText));
            OnPropertyChanged(nameof(LastBestOcrScoreText));
            var cleanedText = _textCleaner.Clean(result.Text);
            await SaveOcrRequestAsync(result, imageBytes, debug);

            var selectionWarning = _ocrEngineManager.LastFallbackUsed
                ? $"⚠ Selected {_ocrEngineSettings.PreferredProvider} unavailable — used {result.ProviderName} instead. {_ocrEngineManager.LastFallbackReason}"
                : _ocrEngineManager.LastSelectionNote;

            ConfidenceText = $"{result.Confidence:P1}";
            LastOcrTimeText = completedAt.ToString("yyyy-MM-dd HH:mm:ss");
            _cache.StoreImage(imageHash, completedAt);

            if (!result.Success)
            {
                RawOcrText = result.Text;
                CleanedOcrText = cleanedText;
                UpdateLines(result.Lines);
                ChangeStatus = "Failed";
                StatusText = $"OCR provider failed: {result.ErrorMessage}";
                ShowError(result.ErrorMessage);
                await SaveFailureReasonAsync(
                    OcrFailureReason.RequestFailed,
                    providerFailed: true,
                    providerError: result.ErrorMessage,
                    result: result);
                return;
            }

            if (result.Lines.Count == 0 && string.IsNullOrWhiteSpace(result.Text))
            {
                RawOcrText = result.Text;
                CleanedOcrText = cleanedText;
                UpdateLines(result.Lines);
                ChangeStatus = "No text";
                StatusText = "OCR provider returned no usable lines.";
                ShowError(
                    $"Provider '{result.ProviderName}' succeeded but returned no text " +
                    $"(duration {result.DurationMs} ms). The region may contain no readable text, " +
                    "or preprocessing may be destroying it — check debug\\latest_ocr_sent_to_provider.png." +
                    (string.IsNullOrEmpty(selectionWarning) ? string.Empty : $"\n{selectionWarning}"));
                await SaveFailureReasonAsync(
                    OcrFailureReason.EmptyProviderResult,
                    providerFailed: false,
                    providerError: string.Empty,
                    result: result);
                return;
            }

            if (!_ocrEngineSettings.IgnoreOcrConfidenceThresholdForDebug &&
                result.Confidence < _settings.MinimumConfidenceThreshold)
            {
                RawOcrText = result.Text;
                CleanedOcrText = cleanedText;
                UpdateLines(result.Lines);
                ChangeStatus = "Rejected";
                StatusText =
                    $"OCR confidence {result.Confidence:P1} is below the threshold " +
                    $"({_settings.MinimumConfidenceThreshold:P1}) — text discarded.";
                ShowError(
                    $"Provider '{result.ProviderName}' returned {result.Lines.Count} line(s) with confidence " +
                    $"{result.Confidence:P1}, below the configured threshold {_settings.MinimumConfidenceThreshold:P1}.\n" +
                    "Lower the threshold in Settings or improve the capture region if this text is valid.");
                _logger.LogWarning(
                    "OCR confidence below threshold: confidence={Confidence:F3}, threshold={Threshold:F3}",
                    result.Confidence,
                    _settings.MinimumConfidenceThreshold);
                await SaveFailureReasonAsync(
                    OcrFailureReason.ConfidenceBelowThreshold,
                    providerFailed: false,
                    providerError: string.Empty,
                    result: result,
                    rejectedByThreshold: true);
                return;
            }

            var textChanged = !_settings.EnableOcrCache || !_cache.IsSameText(cleanedText);
            if (!textChanged)
            {
                ChangeStatus = "Unchanged";
                StatusText = "OCR completed - cleaned text is unchanged.";
                _logger.LogInformation("OCR skipped because same text");
                await SaveFailureReasonAsync(
                    OcrFailureReason.DuplicateSkipped,
                    providerFailed: false,
                    providerError: string.Empty,
                    result: result,
                    rejectedAsDuplicate: true);
                return;
            }

            RawOcrText = result.Text;
            CleanedOcrText = cleanedText;
            UpdateLines(result.Lines);
            ChangeStatus = "Changed";
            StatusText = string.IsNullOrEmpty(selectionWarning)
                ? $"OCR complete - {result.Lines.Count} line(s) detected."
                : $"OCR complete - {result.Lines.Count} line(s). {selectionWarning}";
            _cache.StoreText(cleanedText);
            _overlayService.UpdateText(cleanedText);

            _logger.LogInformation("OCR text changed");
        }
        catch (OcrSetupException exception)
        {
            StatusText = "OCR setup error - see details below.";
            ShowError(exception.Message);
            _logger.LogError(exception, "OCR setup error");
            await SaveFailureReasonAsync(OcrFailureReason.ServerStartupFailed, providerFailed: true, providerError: exception.Message);
        }
        catch (OcrRuntimeException exception)
        {
            StatusText = "OCR script error - see details below.";
            ShowError(exception.Message);
            _logger.LogError(exception, "OCR runtime error");
            await SaveFailureReasonAsync(OcrFailureReason.RequestFailed, providerFailed: true, providerError: exception.Message);
        }
        catch (Exception exception)
        {
            StatusText = "OCR failed - see details below.";
            ShowError(exception.Message);
            _logger.LogError(exception, "OCR failed on {Path}", OcrRegionPath);
            await SaveFailureReasonAsync(OcrFailureReason.Unknown, providerFailed: true, providerError: exception.Message);
        }
    }

    /// <summary>
    /// Diagnostic run of the currently selected provider. Unlike normal OCR runs this
    /// bypasses every gate — interval, image/text cache, and the confidence threshold —
    /// so the outcome is always the raw provider answer or the exact failure reason.
    /// </summary>
    private async Task TestSelectedOcrProviderAsync()
    {
        if (!await _runLock.WaitAsync(0))
        {
            StatusText = "OCR is already running.";
            return;
        }

        try
        {
            ClearError();

            if (!File.Exists(OcrRegionPath))
            {
                StatusText = "No OCR region image found - crop a region first.";
                ShowError($"File not found:\n  {OcrRegionPath}");
                return;
            }

            var imageBytes = await File.ReadAllBytesAsync(OcrRegionPath);
            RegionPreview = ToBitmapSource(imageBytes);
            StatusText = $"Testing {_ocrEngineSettings.PreferredProvider}…";

            var stopwatch = Stopwatch.StartNew();
            var result = await _ocrEngineManager.RecognizeAsync(new OcrRequest
            {
                ImageBytes = imageBytes,
                Language = "en",
                RegionId = "test-selected-provider",
                PreprocessingSettings = new PreprocessingSettings
                {
                    Preset = _ocrEngineSettings.PreprocessingPreset,
                },
                ForceOcr = true,
                DebugMode = true
            });
            stopwatch.Stop();

            OnPropertyChanged(nameof(LastActualOcrProviderText));
            OnPropertyChanged(nameof(LastBestOcrScoreText));

            RawOcrText = result.Text;
            CleanedOcrText = _textCleaner.Clean(result.Text);
            ConfidenceText = $"{result.Confidence:P1}";
            LastOcrTimeText = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
            UpdateLines(result.Lines);

            var transport = result.ProviderName.Contains("Server", StringComparison.OrdinalIgnoreCase)
                ? "server" : "subprocess/native";
            var fallbackNote = _ocrEngineManager.LastFallbackUsed
                ? $" FALLBACK: {_ocrEngineManager.LastFallbackReason}"
                : string.IsNullOrEmpty(_ocrEngineManager.LastSelectionNote)
                    ? string.Empty
                    : $" NOTE: {_ocrEngineManager.LastSelectionNote}";

            if (result.Success)
            {
                ChangeStatus = result.Lines.Count > 0 || !string.IsNullOrWhiteSpace(result.Text)
                    ? "Test OK" : "Test OK (no text)";
                StatusText =
                    $"Test OK — {result.ProviderName} ({transport}), {result.Lines.Count} line(s), " +
                    $"confidence {result.Confidence:P1}, {result.DurationMs} ms.{fallbackNote}";
                if (result.Lines.Count == 0 && string.IsNullOrWhiteSpace(result.Text))
                    ShowError(
                        $"Provider '{result.ProviderName}' responded but found no text in the image. " +
                        "Check debug\\latest_ocr_sent_to_provider.png to verify the crop.");
            }
            else
            {
                ChangeStatus = "Test failed";
                StatusText = $"Test FAILED — {result.ProviderName}: {result.ErrorMessage}{fallbackNote}";
                ShowError(
                    $"Selected: {_ocrEngineSettings.PreferredProvider}\n" +
                    $"Actual:   {result.ProviderName}\n" +
                    $"Error:    {result.ErrorMessage}" +
                    (string.IsNullOrEmpty(_ocrEngineManager.LastFallbackReason)
                        ? string.Empty
                        : $"\nReason:   {_ocrEngineManager.LastFallbackReason}"));
            }

            await WriteDebugJsonAsync("test_selected_ocr_provider.json", new
            {
                Timestamp = DateTimeOffset.Now,
                SelectedProvider = _ocrEngineSettings.PreferredProvider.ToString(),
                ActualProvider = result.ProviderName,
                Transport = transport,
                FallbackUsed = _ocrEngineManager.LastFallbackUsed,
                FallbackReason = _ocrEngineManager.LastFallbackReason,
                SelectionNote = _ocrEngineManager.LastSelectionNote,
                TotalDurationMs = stopwatch.ElapsedMilliseconds,
                ProviderDurationMs = result.DurationMs,
                result.Success,
                result.ErrorMessage,
                result.Confidence,
                LineCount = result.Lines.Count,
                result.Text,
            });
        }
        catch (Exception exception)
        {
            ChangeStatus = "Test failed";
            StatusText = "Test FAILED - see details below.";
            ShowError(exception.Message);
            _logger.LogError(exception, "Test selected OCR provider failed");
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task TestAllOcrProvidersAsync()
    {
        ClearError();
        if (!File.Exists(OcrRegionPath))
        {
            StatusText = "No OCR crop exists to test.";
            ShowError($"File not found:\n  {OcrRegionPath}");
            return;
        }

        var imageBytes = await File.ReadAllBytesAsync(OcrRegionPath);
        var results = new List<object>();

        foreach (var provider in _ocrEngineManager.Providers)
        {
            var req = new OcrRequest
            {
                ImageBytes = imageBytes,
                Language = "en",
                RegionId = "manual-test-all",
                PreprocessingSettings = new PreprocessingSettings { Preset = _ocrEngineSettings.PreprocessingPreset },
                ForceOcr = true,
                DebugMode = true
            };

            OcrResult result;
            try
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(Math.Max(_ocrEngineSettings.SubprocessOcrTimeoutMs, 20_000)));
                result = await provider.RecognizeAsync(req, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                result = new OcrResult
                {
                    ProviderName = provider.Name,
                    Success = false,
                    ErrorMessage = "Timed out during test.",
                };
            }
            catch (Exception exception)
            {
                result = new OcrResult
                {
                    ProviderName = provider.Name,
                    Success = false,
                    ErrorMessage = exception.Message,
                };
            }

            results.Add(new
            {
                Provider = provider.Name,
                Status = provider.IsAvailable ? "Available" : "Unavailable",
                result.Success,
                Text = result.Text,
                LineCount = result.Lines.Count,
                Confidence = result.Confidence,
                Duration = result.DurationMs,
                Error = result.ErrorMessage
            });
        }

        await WriteDebugJsonAsync("test_all_ocr_providers.json", new { Timestamp = DateTimeOffset.Now, Results = results });
        await OpenOcrDebugFolderAsync();
        var okCount = _ocrEngineManager.Providers.Count(provider => provider.IsAvailable);
        StatusText =
            $"Test all providers complete — {okCount}/{_ocrEngineManager.Providers.Count} available. " +
            "Results saved in debug folder.";
    }

    private async Task SaveOcrDebugCropAsync()
    {
        ClearError();
        if (!File.Exists(OcrRegionPath))
        {
            StatusText = "No OCR crop exists to save.";
            ShowError($"File not found:\n  {OcrRegionPath}");
            return;
        }

        var bytes = await File.ReadAllBytesAsync(OcrRegionPath);
        await SaveDebugImagesAsync(bytes);
        RegionPreview = ToBitmapSource(bytes);
        StatusText = "OCR debug crop saved to debug folder.";
    }

    private Task OpenOcrDebugFolderAsync()
    {
        Directory.CreateDirectory(DebugDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = DebugDirectory,
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }

    private static async Task SaveDebugImagesAsync(byte[] imageBytes)
    {
        Directory.CreateDirectory(DebugDirectory);
        await File.WriteAllBytesAsync(Path.Combine(DebugDirectory, "latest_ocr_region_raw.png"), imageBytes);
        await File.WriteAllBytesAsync(Path.Combine(DebugDirectory, "latest_ocr_region_processed.png"), imageBytes);
        await File.WriteAllBytesAsync(Path.Combine(DebugDirectory, "latest_ocr_sent_to_provider.png"), imageBytes);
    }

    private async Task SaveOcrRequestAsync(OcrResult result, byte[] imageBytes, bool debug)
    {
        Directory.CreateDirectory(DebugDirectory);
        var sentImagePath = Path.Combine(DebugDirectory, "latest_ocr_sent_to_provider.png");
        if (debug)
            await File.WriteAllBytesAsync(sentImagePath, imageBytes);

        await WriteDebugJsonAsync("last_ocr_request.json", new
        {
            Timestamp = DateTimeOffset.Now,
            SelectedProvider = _ocrEngineSettings.PreferredProvider.ToString(),
            ActualProvider = _ocrEngineManager.LastProviderUsed,
            FallbackEnabled = _ocrEngineSettings.EnableOcrProviderFallback,
            FallbackUsed = _ocrEngineManager.LastFallbackUsed,
            FallbackReason = _ocrEngineManager.LastFallbackReason,
            SelectedWindowTitle = "manual OCR tab sample/latest crop",
            CaptureRect = "sample/latest crop",
            OcrRegionRect = "sample/latest crop",
            CropSize = TryReadImageSize(imageBytes),
            PreprocessingSettings = new { _ocrEngineSettings.PreprocessingPreset },
            SentImagePath = sentImagePath,
            ProviderUsed = result.ProviderName,
            ProviderDuration = result.DurationMs,
            ProviderRawOutput = result.RawOutput,
            ParsedLineCount = result.Lines.Count,
            ParsedText = result.Text,
            result.Confidence,
            result.Success,
            result.ErrorMessage,
            Lines = result.Lines.Select(line => new
            {
                line.Text,
                line.Confidence,
                line.BoundingBox,
            }),
        });
    }

    private async Task SaveFailureReasonAsync(
        OcrFailureReason failureReason,
        bool providerFailed,
        string providerError,
        OcrResult? result = null,
        bool rejectedByThreshold = false,
        bool rejectedByCandidateValidator = false,
        bool rejectedAsDuplicate = false)
    {
        await WriteDebugJsonAsync("last_ocr_failure_reason.json", new
        {
            Timestamp = DateTimeOffset.Now,
            ProviderFailed = providerFailed,
            ProviderError = providerError,
            RawProviderOutput = result?.RawOutput ?? string.Empty,
            RawLinesCount = result?.Lines.Count ?? 0,
            ParsedLinesCount = result?.Lines.Count ?? 0,
            Confidence = result?.Confidence ?? 0,
            ConfiguredConfidenceThreshold = _settings.MinimumConfidenceThreshold,
            RejectedByThreshold = rejectedByThreshold,
            RejectedByCandidateValidator = rejectedByCandidateValidator,
            RejectedAsDuplicateOrUnchanged = rejectedAsDuplicate,
            SelectedProvider = _ocrEngineSettings.PreferredProvider.ToString(),
            ActualProvider = _ocrEngineManager.LastProviderUsed,
            FallbackEnabled = _ocrEngineSettings.EnableOcrProviderFallback,
            FallbackUsed = _ocrEngineManager.LastFallbackUsed,
            FallbackReason = _ocrEngineManager.LastFallbackReason,
            FinalReason = failureReason.ToString(),
            failureReason
        });
    }

    private static async Task WriteDebugJsonAsync(string fileName, object value)
    {
        Directory.CreateDirectory(DebugDirectory);
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(DebugDirectory, fileName), json, new UTF8Encoding(false));
    }

    private static object TryReadImageSize(byte[] imageBytes)
    {
        try
        {
            using var stream = new MemoryStream(imageBytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return new { Width = frame.PixelWidth, Height = frame.PixelHeight };
        }
        catch
        {
            return new { Width = 0, Height = 0 };
        }
    }

    private void UpdateLines(IReadOnlyList<OcrLine> lines)
    {
        Lines.Clear();
        foreach (var line in lines)
        {
            Lines.Add(new OcrLineItem(line.Text, $"{line.Confidence:P1}"));
        }
    }

    private void ClearError()
    {
        HasError = false;
        ErrorDetail = string.Empty;
    }

    private void ShowError(string detail)
    {
        ErrorDetail = detail;
        HasError = true;
    }

    private static BitmapSource ToBitmapSource(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

public sealed record OcrLineItem(string Text, string Confidence);
