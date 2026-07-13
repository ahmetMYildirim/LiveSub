using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PsGameTranslator.App.Commands;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Ocr;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Translation;
using PsGameTranslator.Ocr;
using PsGameTranslator.Overlay;

namespace PsGameTranslator.App.ViewModels;

/// <summary>
/// First-run setup wizard: choose OCR engine → install it → choose translation
/// engine → download its model → overlay test → finish. Everything happens
/// in-app; no terminal needed.
/// </summary>
public sealed class SetupWizardViewModel : ObservableObject
{
    public static readonly string SetupMarkerPath =
        Path.Combine(AppContext.BaseDirectory, "config", "setup_completed.json");

    public static bool IsSetupCompleted => File.Exists(SetupMarkerPath);

    private readonly OcrEngineSettings _ocrSettings;
    private readonly OcrEngineInstallService _ocrInstall;
    private readonly IOcrServerService _ocrServer;
    private readonly TranslationSettings _translationSettings;
    private readonly ModelInstallService _modelInstall;
    private readonly MachineTranslationServerManager _translationServer;
    private readonly IOverlayService _overlayService;
    private readonly IOverlaySettingsService _overlaySettingsService;
    private readonly ILogger<SetupWizardViewModel> _logger;

    private int _step = 1;
    private string _stepStatusText = string.Empty;
    private bool _isBusy;

    public event Action? CloseRequested;

    public SetupWizardViewModel(
        OcrEngineSettings ocrSettings,
        OcrEngineInstallService ocrInstall,
        IOcrServerService ocrServer,
        TranslationSettings translationSettings,
        ModelInstallService modelInstall,
        MachineTranslationServerManager translationServer,
        IOverlayService overlayService,
        IOverlaySettingsService overlaySettingsService,
        ILogger<SetupWizardViewModel> logger)
    {
        _ocrSettings = ocrSettings;
        _ocrInstall = ocrInstall;
        _ocrServer = ocrServer;
        _translationSettings = translationSettings;
        _modelInstall = modelInstall;
        _translationServer = translationServer;
        _overlayService = overlayService;
        _overlaySettingsService = overlaySettingsService;
        _logger = logger;

        BackCommand = new AsyncRelayCommand(() => { Step--; return Task.CompletedTask; }, () => Step > 1 && !_isBusy);
        NextCommand = new AsyncRelayCommand(() => { Step++; return Task.CompletedTask; }, () => Step < 6 && !_isBusy);
        InstallOcrEngineCommand = new AsyncRelayCommand(InstallOcrEngineAsync, () => !_isBusy);
        InstallTranslationModelCommand = new AsyncRelayCommand(InstallTranslationModelAsync, () => !_isBusy);
        TestOverlayCommand = new AsyncRelayCommand(TestOverlayAsync, () => !_isBusy);
        FinishCommand = new AsyncRelayCommand(FinishAsync, () => !_isBusy);
        SkipCommand = new AsyncRelayCommand(FinishAsync);
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    public int Step
    {
        get => _step;
        private set
        {
            var clamped = Math.Clamp(value, 1, 6);
            if (_step == clamped) return;
            _step = clamped;
            StepStatusText = string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TitleText));
            for (var i = 1; i <= 6; i++)
                OnPropertyChanged($"IsStep{i}");
            RaiseCommands();
        }
    }

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool IsStep4 => Step == 4;
    public bool IsStep5 => Step == 5;
    public bool IsStep6 => Step == 6;

    public string TitleText => Step switch
    {
        1 => "Adım 1 / 6 — OCR Motoru Seç",
        2 => "Adım 2 / 6 — OCR Motorunu Kur",
        3 => "Adım 3 / 6 — Çeviri Motoru Seç",
        4 => "Adım 4 / 6 — Çeviri Modelini İndir",
        5 => "Adım 5 / 6 — Overlay Testi",
        _ => "Adım 6 / 6 — Bitir",
    };

    public string StepStatusText { get => _stepStatusText; private set => SetProperty(ref _stepStatusText, value); }

    public ICommand BackCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand InstallOcrEngineCommand { get; }
    public ICommand InstallTranslationModelCommand { get; }
    public ICommand TestOverlayCommand { get; }
    public ICommand FinishCommand { get; }
    public ICommand SkipCommand { get; }

    // ── Step 1: OCR engine ────────────────────────────────────────────────────

    public IReadOnlyList<string> OcrEngineOptions { get; } =
        ["PaddleOCR (önerilen)", "RapidOCR (hafif)", "Windows OCR (kurulum gerekmez)", "EasyOCR (ağır)"];

    private int _ocrEngineIndex;
    public int OcrEngineIndex
    {
        get => _ocrEngineIndex;
        set
        {
            if (SetProperty(ref _ocrEngineIndex, value))
                _ocrSettings.PreferredProvider = SelectedOcrEngine;
        }
    }

    private OcrProviderType SelectedOcrEngine => _ocrEngineIndex switch
    {
        1 => OcrProviderType.RapidOCR,
        2 => OcrProviderType.WindowsOCR,
        3 => OcrProviderType.EasyOCR,
        _ => OcrProviderType.PaddleOCR,
    };

    // ── Step 2: install OCR engine ───────────────────────────────────────────

    private async Task InstallOcrEngineAsync()
    {
        _isBusy = true;
        RaiseCommands();
        try
        {
            _ocrSettings.PreferredProvider = SelectedOcrEngine;
            var state = await _ocrInstall.RefreshStateAsync(SelectedOcrEngine);
            if (state is OcrEngineInstallState.BuiltIn)
            {
                StepStatusText = $"✓ {SelectedOcrEngine} kurulum gerektirmiyor.";
                return;
            }
            if (state is OcrEngineInstallState.Installed)
            {
                StepStatusText = $"✓ {SelectedOcrEngine} zaten kurulu.";
            }
            else
            {
                StepStatusText = $"⏳ {SelectedOcrEngine} kuruluyor… (birkaç dakika sürebilir)";
                var progress = new Progress<string>(line => Post(() => StepStatusText = $"⏳ {line}"));
                var (success, message) = await _ocrInstall.InstallAsync(SelectedOcrEngine, progress);
                StepStatusText = success ? $"✓ {SelectedOcrEngine} kuruldu." : $"✗ {message}";
                if (!success) return;
            }

            if (OcrServerService.IsServerBackedProvider(SelectedOcrEngine))
            {
                StepStatusText += " OCR sunucusu başlatılıyor…";
                var (started, serverMessage) = await _ocrServer.EnsureRunningAsync();
                StepStatusText = started
                    ? $"✓ {SelectedOcrEngine} kuruldu ve OCR sunucusu çalışıyor."
                    : $"⚠ Kuruldu, ama sunucu başlatılamadı: {serverMessage}";
            }
        }
        finally
        {
            _isBusy = false;
            RaiseCommands();
        }
    }

    // ── Step 3: translation engine ───────────────────────────────────────────

    public IReadOnlyList<string> TranslationEngineOptions { get; } =
        ["Makine Çevirisi — OPUS-MT (yerel, önerilen)", "Ollama (yerel LLM)", "LM Studio (yerel LLM)"];

    private int _translationEngineIndex;
    public int TranslationEngineIndex
    {
        get => _translationEngineIndex;
        set
        {
            if (!SetProperty(ref _translationEngineIndex, value)) return;
            switch (value)
            {
                case 1:
                    _translationSettings.ProviderType = TranslationProviderType.Ollama;
                    _translationSettings.ProviderChainMode = TranslationProviderChainMode.SelectedOnly;
                    break;
                case 2:
                    _translationSettings.ProviderType = TranslationProviderType.LMStudio;
                    _translationSettings.ProviderChainMode = TranslationProviderChainMode.SelectedOnly;
                    break;
                default:
                    _translationSettings.ProviderType = TranslationProviderType.OpusMT;
                    _translationSettings.ProviderChainMode = TranslationProviderChainMode.LocalOnly;
                    break;
            }
        }
    }

    // ── Step 4: translation model ────────────────────────────────────────────

    private async Task InstallTranslationModelAsync()
    {
        _isBusy = true;
        RaiseCommands();
        try
        {
            var progress = new Progress<string>(line => Post(() => StepStatusText = $"⏳ {line}"));
            switch (_translationEngineIndex)
            {
                case 1: // Ollama
                    StepStatusText = $"⏳ {_translationSettings.OllamaModel} Ollama ile indiriliyor…";
                    var (pulled, pullMessage) = await _modelInstall.PullOllamaModelAsync(
                        _translationSettings.OllamaModel, progress);
                    StepStatusText = pulled
                        ? $"✓ {_translationSettings.OllamaModel} hazır."
                        : $"✗ {pullMessage}";
                    break;

                case 2: // LM Studio
                    StepStatusText =
                        "LM Studio kendi modellerini kendisi yönetir. LM Studio'yu açın, oradan bir model " +
                        "indirin ve yerel sunucuyu başlatın (port 1234), sonra devam edin.";
                    break;

                default: // OPUS-MT
                    var model = _translationSettings.MachineTranslationModel;
                    var state = await _modelInstall.GetHuggingFaceStateAsync(model);
                    if (state == ModelInstallState.Installed)
                    {
                        StepStatusText = $"✓ {model} zaten indirilmiş. Çeviri sunucusu başlatılıyor…";
                    }
                    else
                    {
                        StepStatusText = $"⏳ {model} indiriliyor (~1 GB, tek seferlik)…";
                        var (installed, message) = await _modelInstall.InstallHuggingFaceAsync(model, progress);
                        if (!installed)
                        {
                            StepStatusText = $"✗ {message}";
                            return;
                        }
                        StepStatusText = $"✓ {model} indirildi. Çeviri sunucusu başlatılıyor…";
                    }

                    var ready = await _translationServer.EnsureRunningAsync();
                    StepStatusText = ready
                        ? $"✓ {model} indirildi ve çeviri sunucusu çalışıyor."
                        : $"⚠ Model indirildi, ama sunucu başlatılamadı: {_translationServer.LastStartError}";
                    break;
            }
        }
        finally
        {
            _isBusy = false;
            RaiseCommands();
        }
    }

    // ── Step 5: overlay test ─────────────────────────────────────────────────

    private async Task TestOverlayAsync()
    {
        _isBusy = true;
        RaiseCommands();
        try
        {
            var settings = await _overlaySettingsService.LoadAsync();
            if (!_overlayService.IsOpen)
                _overlayService.Open(settings);
            _overlayService.UpdateText("Merhaba! Overlay çalışıyor. ✓");
            StepStatusText = _overlayService.IsOpen
                ? "✓ Overlay ekranda görünüyor. Türkçe test metnini okuyabiliyorsanız bu adım başarılı."
                : "✗ Overlay penceresi açılamadı.";
            await Task.Delay(200);
        }
        catch (Exception exception)
        {
            StepStatusText = $"✗ Overlay testi başarısız oldu: {exception.Message}";
            _logger.LogWarning(exception, "wizard_overlay_test_failed");
        }
        finally
        {
            _isBusy = false;
            RaiseCommands();
        }
    }

    // ── Step 6: finish ───────────────────────────────────────────────────────

    private Task FinishAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SetupMarkerPath)!);
            File.WriteAllText(SetupMarkerPath, JsonSerializer.Serialize(new
            {
                completedAt = DateTimeOffset.Now,
                ocrEngine = _ocrSettings.PreferredProvider.ToString(),
                translationProvider = _translationSettings.ProviderType.ToString(),
                machineTranslationModel = _translationSettings.MachineTranslationModel,
                ollamaModel = _translationSettings.OllamaModel,
            }, new JsonSerializerOptions { WriteIndented = true }));
            _logger.LogInformation("setup_wizard_completed");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "setup_wizard_marker_write_failed");
        }

        try { if (_overlayService.IsOpen) _overlayService.UpdateText(string.Empty); } catch { }
        CloseRequested?.Invoke();
        return Task.CompletedTask;
    }

    private void RaiseCommands()
    {
        ((AsyncRelayCommand)BackCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)NextCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)InstallOcrEngineCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)InstallTranslationModelCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)TestOverlayCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)FinishCommand).NotifyCanExecuteChanged();
    }

    private static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
