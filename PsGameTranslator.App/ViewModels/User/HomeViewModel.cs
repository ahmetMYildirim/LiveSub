using System.ComponentModel;
using System.Windows.Input;
using PsGameTranslator.App.Commands;
using PsGameTranslator.App.Services;

namespace PsGameTranslator.App.ViewModels.User;

public sealed class HomeViewModel : ObservableObject
{
    public HomeViewModel(
        CaptureViewModel capture,
        MonitoringViewModel monitoring,
        TranslationViewModel translation,
        OverlayViewModel overlay,
        OcrViewModel ocr,
        SystemResourceMonitorService systemResources,
        LocalizationService languageService)
    {
        Capture = capture;
        Monitoring = monitoring;
        Translation = translation;
        Overlay = overlay;
        Ocr = ocr;
        SystemResources = systemResources;
        Language = languageService;

        Capture.PropertyChanged += HandleChildChanged;
        Monitoring.PropertyChanged += HandleChildChanged;
        Translation.PropertyChanged += HandleChildChanged;
        Overlay.PropertyChanged += HandleChildChanged;
        Ocr.PropertyChanged += HandleChildChanged;
        Language.PropertyChanged += HandleChildChanged;

        // SystemResources is bound directly via "Home.SystemResources.CpuUsageText"
        // in XAML, so WPF already listens on its PropertyChanged at that path level Ã¢â‚¬â€
        // wiring it into HandleChildChanged would force a full Home-page binding
        // refresh (RaisePropertyChanged(string.Empty)) every tick for no reason.

        ToggleMonitoringCommand = new AsyncRelayCommand(ToggleMonitoringAsync);
    }

    public ICommand ToggleMonitoringCommand { get; }
    public string StartStopButtonText => Monitoring.IsMonitoringRunning ? Language.T("Stop") : Language.T("Start");

    private Task ToggleMonitoringAsync()
    {
        var command = Monitoring.IsMonitoringRunning ? Monitoring.StopMonitoringCommand : Monitoring.StartMonitoringCommand;
        if (command.CanExecute(null))
            command.Execute(null);
        return Task.CompletedTask;
    }

    public CaptureViewModel Capture { get; }
    public MonitoringViewModel Monitoring { get; }
    public TranslationViewModel Translation { get; }
    public OverlayViewModel Overlay { get; }
    public OcrViewModel Ocr { get; }
    public SystemResourceMonitorService SystemResources { get; }
    public LocalizationService Language { get; }

    public ICommand StartCommand => Monitoring.StartMonitoringCommand;
    public ICommand StopCommand => Monitoring.StopMonitoringCommand;
    public ICommand SelectGameCommand => Capture.RefreshCommand;
    public ICommand TestOverlayCommand => Overlay.TestReplacementOverlayCommand;
    public ICommand TestTranslationCommand => Translation.TestSelectedTranslationProviderCommand;

    public string GameTitle => Capture.SelectedWindow?.Title ?? Language.T("NoGameSelected");
    public string WindowInfo => Capture.SelectedWindow is null
        ? Language.T("NoWindowSelected")
        : $"{Capture.SelectedWindow.ProcessName} - {Capture.SelectedWindow.Width}x{Capture.SelectedWindow.Height}";

    public string RunStatus => Monitoring.IsMonitoringRunning ? Language.T("Running") : Language.T("Stopped");
    public string OcrStatus => Monitoring.IsOcrBusy ? Language.T("OcrRunning") : Monitoring.MonitoringStatusText;
    public string OcrEngine => $"Crop {Monitoring.FinalOcrCropWidth}x{Monitoring.FinalOcrCropHeight}";

    // Short, single-word status for the "Çeviri Durumu" card title. Translation's
    // own StatusText is a general-purpose operation message (it holds things like
    // "Models: Ollama 5 · LM Studio 0…" after a model refresh), which is far too
    // noisy for a status card — the model/provider detail lives on the subline
    // (TranslationEngine) and the full message is available via tooltip instead.
    public string TranslationStatus
    {
        get
        {
            if (!Translation.EnableTranslation)
                return Language.T("TranslationDisabled");
            if (Monitoring.TranslationPendingCount > 0)
                return Language.T("Translating");
            return Language.T("Ready");
        }
    }

    // Full operation message, surfaced only as the card tooltip so the detailed
    // provider/model text is still reachable without cluttering the card.
    public string TranslationStatusDetail => Translation.StatusText;
    public string TranslationEngine => Translation.ActualProviderUsedText;

    // Overlay's StatusText starts empty until the user first interacts with the
    // overlay, which left the card value blank. Fall back to a clear "not started".
    public string OverlayStatus => string.IsNullOrWhiteSpace(Overlay.StatusText)
        ? Language.T("OverlayNotStarted")
        : Overlay.StatusText;
    public string OverlayMode => Overlay.DisplayModeIndex == 1 ? "Replacement" : "Classic";

    // Checkmark badges on the Home status cards Ã¢â‚¬â€ hidden only when the status
    // text itself signals a problem, so they never assert "OK" over a visible error.
    public bool IsOcrOk => !HasErrorHint(OcrStatus);
    public bool IsTranslationOk => !HasErrorHint(TranslationStatus);
    public bool IsOverlayOk => !HasErrorHint(OverlayStatus);

    private static bool HasErrorHint(string status) =>
        status.Contains("hata", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("basarisiz", StringComparison.OrdinalIgnoreCase);
    public string AvgLatency => $"{Monitoring.TimeFromCaptureToOverlayMs} ms";
    public string Fps => Monitoring.CaptureFps <= 0 ? "-" : $"{Monitoring.CaptureFps:F0}";
    public string MemoryText => "RAM live metric later";
    public string LatestSource => Translation.LastSourceText;
    public string LatestTarget => Translation.LastTranslatedText;

    // "Ceviri Modu" quick-settings combo: 0 = Makine Cevirisi (translation on),
    // 1 = Sadece OCR (translation off, raw OCR text only).
    public int TranslationModeIndex
    {
        get => Translation.EnableTranslation ? 0 : 1;
        set => Translation.EnableTranslation = value == 0;
    }

    private void HandleChildChanged(object? sender, PropertyChangedEventArgs e)
    {
        RaisePropertyChanged(string.Empty);
    }
}
