using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PsGameTranslator.App.Commands;
using PsGameTranslator.App.Services;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Overlay;
using PsGameTranslator.Infrastructure.Region;
using PsGameTranslator.Infrastructure.Subtitles;
using System.Text;
using System.Text.Json;

namespace PsGameTranslator.App.ViewModels;

public sealed class OverlayViewModel : ObservableObject
{
    private static readonly string[] TestSubtitles =
    [
        "Kahretsin, hayir!",
        "Ejderhanin ofkesinden kalan daha fazla iz.",
        "Boyle bir canavari daha once hic gormemistim.",
    ];

    private readonly IOverlayService _overlayService;
    private readonly IOverlaySettingsService _settingsService;
    private readonly IMonitorService _monitorService;
    private readonly OverlayMonitorCoordinator _monitorCoordinator;
    private readonly TranslationSettings _translationSettings;
    private readonly PipelineDiagnostics _pipelineDiagnostics;
    private readonly SubtitleCandidateValidator _candidateValidator;
    private readonly IRegionPersistenceService _regionPersistenceService;
    private readonly ILogger<OverlayViewModel> _logger;
    private readonly SynchronizationContext _uiContext;

    private bool _isEnabled;
    private bool _isClickThrough = true;
    private double _opacity = 1.0;
    private double _x = 100;
    private double _y = 100;
    private double _width = 1280;
    private double _height = 260;
    private bool _isConfiguring;
    private string _statusText = string.Empty;
    private SubtitlePreset _selectedPreset = SubtitlePreset.Cinematic;
    private string _fontFamily = "Segoe UI";
    private double _fontSize = 30;
    private string _fontWeight = "SemiBold";
    private bool _backgroundEnabled = true;
    private double _backgroundOpacity = 0.55;
    private double _backgroundCornerRadius = 10;
    private double _paddingHorizontal = 22;
    private double _paddingVertical = 12;
    private double _maxWidthPercent = 0.72;
    private double _bottomMargin = 110;
    private string _textAlignment = "Center";
    private string _textColor = "#FFFFFF";
    private bool _shadowEnabled = true;
    private bool _outlineEnabled = true;
    private double _outlineThickness = 1;
    private bool _autoFitHeight = true;
    private double _maxHeight = 320;
    private int _overlayUpdateDebounceMs = 150;
    private SubtitleDisplayMode _displayMode = SubtitleDisplayMode.NativeSubtitleOverlay;
    private bool _replacementMaskEnabled = true;
    private double _replacementMaskOpacity = 0.90;
    private double _replacementPaddingLeft = 24;
    private double _replacementPaddingTop = 12;
    private double _replacementPaddingRight = 24;
    private double _replacementPaddingBottom = 12;
    private double _replacementFontSize = 26;
    private int _replacementMaxLines = 3;
    private bool _showReplacementRectOutline;
    private bool _rejectHudControlText = true;
    private bool _useSubtitleCandidateScoring = true;
    private bool _useManualReplacementRegion = true;
    private double _manualReplacementRegionX;
    private double _manualReplacementRegionY;
    private double _manualReplacementRegionWidth;
    private double _manualReplacementRegionHeight;
    private bool _replacementAutoFitText = true;
    private double _replacementMinFontSize = 20;
    private int _testSubtitleIndex;

    // ── Multi-monitor state (Part C/F) ───────────────────────────────────────────
    private OverlayTargetMonitorMode _overlayTargetMonitorMode = OverlayTargetMonitorMode.SameAsCaptureWindow;
    private string _selectedOverlayMonitorDeviceName = string.Empty;
    private bool _autoRecoverOffscreenOverlay = true;
    private string _lastKnownMonitorDeviceName = string.Empty;
    private bool _isOverlayOffScreen;
    private string _lastRecoveryReasonText = "-";
    private string _manualMonitorWarningText = string.Empty;

    public ObservableCollection<string> AvailableMonitorDeviceNames { get; } = [];
    public ObservableCollection<string> ConnectedMonitorsDisplay { get; } = [];

    public OverlayViewModel(
        IOverlayService overlayService,
        IOverlaySettingsService settingsService,
        IMonitorService monitorService,
        OverlayMonitorCoordinator monitorCoordinator,
        TranslationSettings translationSettings,
        PipelineDiagnostics pipelineDiagnostics,
        SubtitleCandidateValidator candidateValidator,
        IRegionPersistenceService regionPersistenceService,
        ILogger<OverlayViewModel> logger)
    {
        _overlayService = overlayService;
        _settingsService = settingsService;
        _monitorService = monitorService;
        _monitorCoordinator = monitorCoordinator;
        _translationSettings = translationSettings;
        _pipelineDiagnostics = pipelineDiagnostics;
        _candidateValidator = candidateValidator;
        _regionPersistenceService = regionPersistenceService;
        _logger = logger;
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("OverlayViewModel must be created on the UI thread.");

        ToggleConfigModeCommand = new AsyncRelayCommand(ToggleConfigModeAsync);
        TestOverlayCommand = new AsyncRelayCommand(TestOverlayAsync);
        TestSubtitleCommand = new AsyncRelayCommand(TestSubtitleAsync);
        ApplySettingsCommand = new AsyncRelayCommand(ApplySettingsAsync);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        ResetToPresetCommand = new AsyncRelayCommand(ResetToPresetAsync);
        TestReplacementOverlayCommand = new AsyncRelayCommand(TestReplacementOverlayAsync);
        PreviewLastReplacementRectCommand = new AsyncRelayCommand(PreviewLastReplacementRectAsync);
        ResetReplacementSettingsCommand = new AsyncRelayCommand(ResetReplacementSettingsAsync);
        UseOcrRegionAsMaskRegionCommand = new AsyncRelayCommand(UseOcrRegionAsMaskRegionAsync);
        PreviewReplacementMaskCommand = new AsyncRelayCommand(PreviewReplacementMaskAsync);
        ResetReplacementRegionCommand = new AsyncRelayCommand(ResetReplacementRegionAsync);
        RunReplacementSelfTestCommand = new AsyncRelayCommand(RunReplacementSelfTestAsync);
        MoveToPrimaryMonitorCommand = new AsyncRelayCommand(MoveToPrimaryMonitorAsync);
        MoveToCaptureWindowMonitorCommand = new AsyncRelayCommand(MoveToCaptureWindowMonitorAsync);
        CenterOnCurrentMonitorCommand = new AsyncRelayCommand(CenterOnCurrentMonitorAsync);
        SetOverlayAnchorCommand = new Commands.ParameterizedAsyncRelayCommand(SetOverlayAnchorAsync);
        ResetOverlayPositionCommand = new AsyncRelayCommand(ResetOverlayPositionAsync);
        RecoverOverlayNowCommand = new AsyncRelayCommand(RecoverOverlayNowAsync);
        RefreshMonitorsCommand = new AsyncRelayCommand(RefreshMonitorsAsync);

        _monitorCoordinator.MonitorConfigurationChanged += OnMonitorConfigurationChanged;
        _pipelineDiagnostics.Changed += OnPipelineDiagnosticsChanged;

        _ = LoadSettingsAsync();
        RefreshMonitorLists();
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
                OnIsEnabledChanged(value);
        }
    }

    public bool IsClickThrough
    {
        get => _isClickThrough;
        set => SetProperty(ref _isClickThrough, value);
    }

    public double Opacity
    {
        get => _opacity;
        set => SetProperty(ref _opacity, Math.Clamp(value, 0.1, 1.0));
    }

    public bool IsConfiguring
    {
        get => _isConfiguring;
        private set
        {
            if (SetProperty(ref _isConfiguring, value))
                OnPropertyChanged(nameof(ConfigButtonText));
        }
    }

    public string ConfigButtonText => _isConfiguring ? "Done Configuring" : "Configure Overlay";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public int PresetIndex
    {
        get => (int)_selectedPreset;
        set
        {
            var preset = (SubtitlePreset)Math.Clamp(value, 0, 3);
            if (_selectedPreset == preset) return;
            ApplyPreset(preset);
        }
    }

    public string FontFamily
    {
        get => _fontFamily;
        set => SetProperty(ref _fontFamily, string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value);
    }

    public double FontSize
    {
        get => _fontSize;
        set => SetProperty(ref _fontSize, Math.Clamp(value, 12, 72));
    }

    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        set => SetProperty(ref _backgroundOpacity, Math.Clamp(value, 0.0, 1.0));
    }

    public double BottomMargin
    {
        get => _bottomMargin;
        set => SetProperty(ref _bottomMargin, Math.Clamp(value, 0, 300));
    }

    public double MaxWidthPercent
    {
        get => _maxWidthPercent;
        set => SetProperty(ref _maxWidthPercent, Math.Clamp(value, 0.25, 1.0));
    }

    public string TextColor
    {
        get => _textColor;
        set => SetProperty(ref _textColor, string.IsNullOrWhiteSpace(value) ? "#FFFFFF" : value);
    }

    public bool BackgroundEnabled
    {
        get => _backgroundEnabled;
        set => SetProperty(ref _backgroundEnabled, value);
    }

    public bool OutlineEnabled
    {
        get => _outlineEnabled;
        set => SetProperty(ref _outlineEnabled, value);
    }

    public bool ShadowEnabled
    {
        get => _shadowEnabled;
        set => SetProperty(ref _shadowEnabled, value);
    }

    public int DisplayModeIndex
    {
        get => (int)_displayMode;
        set
        {
            var mode = (SubtitleDisplayMode)Math.Clamp(value, 0, 2);
            if (_displayMode == mode) return;
            _displayMode = mode;
            if (mode == SubtitleDisplayMode.SubtitleReplacementOverlay)
            {
                _translationSettings.EnableTranslation = true;
                _translationSettings.TurkishOnlyMode = true;
                _translationSettings.ShowSourceWhileTranslating = false;
                _translationSettings.ShowMaskWhileTranslationPending = true;
                OnPropertyChanged(nameof(ShowMaskWhileTranslationPending));
            }
            OnPropertyChanged();
        }
    }

    public bool ReplacementMaskEnabled
    {
        get => _replacementMaskEnabled;
        set => SetProperty(ref _replacementMaskEnabled, value);
    }

    public double ReplacementMaskOpacity
    {
        get => _replacementMaskOpacity;
        set => SetProperty(ref _replacementMaskOpacity, Math.Clamp(value, 0, 1));
    }

    public double ReplacementPaddingLeft
    {
        get => _replacementPaddingLeft;
        set => SetProperty(ref _replacementPaddingLeft, Math.Max(0, value));
    }

    public double ReplacementPaddingTop
    {
        get => _replacementPaddingTop;
        set => SetProperty(ref _replacementPaddingTop, Math.Max(0, value));
    }

    public double ReplacementPaddingRight
    {
        get => _replacementPaddingRight;
        set => SetProperty(ref _replacementPaddingRight, Math.Max(0, value));
    }

    public double ReplacementPaddingBottom
    {
        get => _replacementPaddingBottom;
        set => SetProperty(ref _replacementPaddingBottom, Math.Max(0, value));
    }

    public double ReplacementFontSize
    {
        get => _replacementFontSize;
        set => SetProperty(ref _replacementFontSize, Math.Clamp(value, 12, 72));
    }

    public int ReplacementMaxLines
    {
        get => _replacementMaxLines;
        set => SetProperty(ref _replacementMaxLines, Math.Clamp(value, 1, 4));
    }

    // ── Manual replacement region (Part A) ───────────────────────────────────────

    public bool UseManualReplacementRegion
    {
        get => _useManualReplacementRegion;
        set
        {
            if (!SetProperty(ref _useManualReplacementRegion, value)) return;
            OnPropertyChanged(nameof(ManualReplacementRegionSummary));
            OnPropertyChanged(nameof(ManualReplacementRegionIsValidText));
        }
    }

    public double ManualReplacementRegionX
    {
        get => _manualReplacementRegionX;
        set
        {
            if (!SetProperty(ref _manualReplacementRegionX, Math.Max(0, value))) return;
            OnPropertyChanged(nameof(ManualReplacementRegionSummary));
            OnPropertyChanged(nameof(ManualReplacementRegionIsValidText));
        }
    }

    public double ManualReplacementRegionY
    {
        get => _manualReplacementRegionY;
        set
        {
            if (!SetProperty(ref _manualReplacementRegionY, Math.Max(0, value))) return;
            OnPropertyChanged(nameof(ManualReplacementRegionSummary));
            OnPropertyChanged(nameof(ManualReplacementRegionIsValidText));
        }
    }

    public double ManualReplacementRegionWidth
    {
        get => _manualReplacementRegionWidth;
        set
        {
            if (!SetProperty(ref _manualReplacementRegionWidth, Math.Max(0, value))) return;
            OnPropertyChanged(nameof(ManualReplacementRegionSummary));
            OnPropertyChanged(nameof(ManualReplacementRegionIsValidText));
        }
    }

    public double ManualReplacementRegionHeight
    {
        get => _manualReplacementRegionHeight;
        set
        {
            if (!SetProperty(ref _manualReplacementRegionHeight, Math.Max(0, value))) return;
            OnPropertyChanged(nameof(ManualReplacementRegionSummary));
            OnPropertyChanged(nameof(ManualReplacementRegionIsValidText));
        }
    }

    public bool ReplacementAutoFitText
    {
        get => _replacementAutoFitText;
        set => SetProperty(ref _replacementAutoFitText, value);
    }

    public double ReplacementMinFontSize
    {
        get => _replacementMinFontSize;
        set => SetProperty(ref _replacementMinFontSize, Math.Clamp(value, 10, 40));
    }

    public string ManualReplacementRegionSummary =>
        IsManualReplacementRegionValid
            ? $"({_manualReplacementRegionX:F0}, {_manualReplacementRegionY:F0}) {_manualReplacementRegionWidth:F0}x{_manualReplacementRegionHeight:F0} (window-relative)"
            : "(not set or invalid — width must be > 100 and height > 30)";

    public string ManualReplacementRegionIsValidText =>
        IsManualReplacementRegionValid ? "IsValid: true" : "IsValid: false";

    private bool IsManualReplacementRegionValid =>
        _useManualReplacementRegion &&
        _manualReplacementRegionWidth > 100 &&
        _manualReplacementRegionHeight > 30;

    public bool EnableReadableSubtitleTiming
    {
        get => _translationSettings.EnableReadableSubtitleTiming;
        set
        {
            if (_translationSettings.EnableReadableSubtitleTiming == value) return;
            _translationSettings.EnableReadableSubtitleTiming = value;
            OnPropertyChanged();
        }
    }

    public int MinTurkishDisplayMs
    {
        get => _translationSettings.MinTurkishDisplayMs;
        set
        {
            var clamped = Math.Max(0, value);
            if (_translationSettings.MinTurkishDisplayMs == clamped) return;
            _translationSettings.MinTurkishDisplayMs = clamped;
            OnPropertyChanged();
        }
    }

    public int MaxTurkishDisplayMs
    {
        get => _translationSettings.MaxTurkishDisplayMs;
        set
        {
            var clamped = Math.Max(0, value);
            if (_translationSettings.MaxTurkishDisplayMs == clamped) return;
            _translationSettings.MaxTurkishDisplayMs = clamped;
            OnPropertyChanged();
        }
    }

    public int MsPerCharacter
    {
        get => _translationSettings.MsPerCharacter;
        set
        {
            var clamped = Math.Max(0, value);
            if (_translationSettings.MsPerCharacter == clamped) return;
            _translationSettings.MsPerCharacter = clamped;
            OnPropertyChanged();
        }
    }

    public bool ShowMaskWhileTranslationPending
    {
        get => _translationSettings.ShowMaskWhileTranslationPending;
        set
        {
            if (_translationSettings.ShowMaskWhileTranslationPending == value) return;
            _translationSettings.ShowMaskWhileTranslationPending = value;
            OnPropertyChanged();
        }
    }

    public bool ShowReplacementRectOutline
    {
        get => _showReplacementRectOutline;
        set => SetProperty(ref _showReplacementRectOutline, value);
    }

    public bool RejectHudControlText
    {
        get => _rejectHudControlText;
        set => SetProperty(ref _rejectHudControlText, value);
    }

    public bool UseSubtitleCandidateScoring
    {
        get => _useSubtitleCandidateScoring;
        set => SetProperty(ref _useSubtitleCandidateScoring, value);
    }

    public string LastReplacementRectText =>
        string.IsNullOrWhiteSpace(_pipelineDiagnostics.LastReplacementRect) ? "-" : _pipelineDiagnostics.LastReplacementRect;
    public string LastDisplayedTurkishText =>
        string.IsNullOrWhiteSpace(_pipelineDiagnostics.LastDisplayedTurkishText) ? "-" : _pipelineDiagnostics.LastDisplayedTurkishText;
    public string LastDisplayDurationText => _pipelineDiagnostics.LastSubtitleDisplayDurationMs > 0
        ? $"{_pipelineDiagnostics.LastSubtitleDisplayDurationMs} ms"
        : "-";
    public string LastOverlayUpdateReasonText =>
        string.IsNullOrWhiteSpace(_pipelineDiagnostics.LastOverlayUpdateSource) ? "-" : _pipelineDiagnostics.LastOverlayUpdateSource;
    public string LastAcceptedCandidateText =>
        string.IsNullOrWhiteSpace(_pipelineDiagnostics.LastAcceptedOcrCandidate) ? "-" : _pipelineDiagnostics.LastAcceptedOcrCandidate;
    public string LastRejectedReasonText =>
        string.IsNullOrWhiteSpace(_pipelineDiagnostics.LastRejectedReason) ? "-" : _pipelineDiagnostics.LastRejectedReason;
    public string WasEnglishBlockedText => _pipelineDiagnostics.WasEnglishBlockedInReplacementMode ? "true" : "false";

    // ── Multi-monitor bindable properties (Part C/F) ─────────────────────────────

    public int OverlayTargetMonitorModeIndex
    {
        get => (int)_overlayTargetMonitorMode;
        set
        {
            var mode = (OverlayTargetMonitorMode)Math.Clamp(value, 0, 2);
            if (_overlayTargetMonitorMode == mode) return;
            _overlayTargetMonitorMode = mode;
            OnPropertyChanged();
            RefreshMonitorDiagnosticsText();
        }
    }

    public string SelectedOverlayMonitorDeviceName
    {
        get => _selectedOverlayMonitorDeviceName;
        set
        {
            if (SetProperty(ref _selectedOverlayMonitorDeviceName, value ?? string.Empty))
                RefreshMonitorDiagnosticsText();
        }
    }

    public bool AutoRecoverOffscreenOverlay
    {
        get => _autoRecoverOffscreenOverlay;
        set => SetProperty(ref _autoRecoverOffscreenOverlay, value);
    }

    public bool IsOverlayOffScreen { get => _isOverlayOffScreen; private set => SetProperty(ref _isOverlayOffScreen, value); }
    public string LastRecoveryReasonText { get => _lastRecoveryReasonText; private set => SetProperty(ref _lastRecoveryReasonText, value); }
    public string ManualMonitorWarningText { get => _manualMonitorWarningText; private set => SetProperty(ref _manualMonitorWarningText, value); }

    public string PrimaryMonitorText => _monitorService.GetPrimaryMonitor().ToString();
    public string CaptureWindowMonitorText => _monitorCoordinator.GetCaptureWindowMonitor()?.ToString() ?? "(no capture window selected)";
    public string CurrentOverlayMonitorText =>
        _monitorService.GetMonitorContainingRect(_x, _y, _width, _height)?.ToString() ?? "(off-screen)";
    public string CurrentOverlayRectText => $"({_x:F0}, {_y:F0}) {_width:F0}x{_height:F0}";
    public string OverlayVisibleText => IsOverlayOffScreen ? "false" : "true";

    public ICommand ToggleConfigModeCommand { get; }
    public ICommand TestOverlayCommand { get; }
    public ICommand TestSubtitleCommand { get; }
    public ICommand ApplySettingsCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ResetToPresetCommand { get; }
    public ICommand TestReplacementOverlayCommand { get; }
    public ICommand PreviewLastReplacementRectCommand { get; }
    public ICommand ResetReplacementSettingsCommand { get; }
    public ICommand RunReplacementSelfTestCommand { get; }
    public ICommand UseOcrRegionAsMaskRegionCommand { get; }
    public ICommand PreviewReplacementMaskCommand { get; }
    public ICommand ResetReplacementRegionCommand { get; }
    public ICommand MoveToPrimaryMonitorCommand { get; }
    public ICommand MoveToCaptureWindowMonitorCommand { get; }
    public ICommand CenterOnCurrentMonitorCommand { get; }
    /// <summary>Takes the anchor name (e.g. "BottomCenter") as its parameter.</summary>
    public ICommand SetOverlayAnchorCommand { get; }
    public ICommand ResetOverlayPositionCommand { get; }
    public ICommand RecoverOverlayNowCommand { get; }
    public ICommand RefreshMonitorsCommand { get; }

    private async Task ToggleConfigModeAsync()
    {
        if (!_isConfiguring)
        {
            if (!_overlayService.IsOpen)
            {
                try
                {
                    _overlayService.Open(BuildSettings());
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Failed to open overlay for configuration");
                    StatusText = $"Error opening overlay: {exception.Message}";
                    return;
                }

                _isEnabled = true;
                OnPropertyChanged(nameof(IsEnabled));
            }

            _overlayService.EnterConfigMode();
            IsConfiguring = true;
            StatusText = "Drag the header to move and drag the corner to resize.";
            return;
        }

        var (x, y, width, height) = _overlayService.ExitConfigMode();
        _x = x;
        _y = y;
        _width = width;
        _height = height;
        _lastKnownMonitorDeviceName = _monitorService.GetMonitorContainingRect(x, y, width, height)?.DeviceName
            ?? _lastKnownMonitorDeviceName;
        IsConfiguring = false;
        UpdateOffScreenState();

        if (_overlayService.IsOpen)
            _overlayService.ApplySettings(BuildSettings());

        await _settingsService.SaveAsync(BuildSettings());
        StatusText = $"Overlay position saved at ({x:F0},{y:F0}) size {width:F0}x{height:F0}.";
    }

    private Task TestOverlayAsync()
    {
        EnsureOverlayOpen();
        _overlayService.UpdateText("Overlay position test\nLine 2: The quick brown fox");
        StatusText = "Overlay test text shown.";
        return Task.CompletedTask;
    }

    private Task TestSubtitleAsync()
    {
        EnsureOverlayOpen();
        var text = TestSubtitles[_testSubtitleIndex % TestSubtitles.Length];
        _testSubtitleIndex++;
        _overlayService.UpdateText(text);
        StatusText = $"Subtitle preset preview: {_selectedPreset}";
        return Task.CompletedTask;
    }

    // ── Manual replacement region commands (Part A) ──────────────────────────────

    private async Task UseOcrRegionAsMaskRegionAsync()
    {
        var savedRegion = await _regionPersistenceService.LoadAsync();
        if (savedRegion is null)
        {
            StatusText = "No OCR region saved yet. Select an OCR region first (Region Selection tab).";
            return;
        }

        // Both regions are window-relative; the OCR region is a good starting mask
        // that the user can then tighten with the numeric fields.
        ManualReplacementRegionX = savedRegion.X;
        ManualReplacementRegionY = savedRegion.Y;
        ManualReplacementRegionWidth = savedRegion.Width;
        ManualReplacementRegionHeight = savedRegion.Height;
        UseManualReplacementRegion = true;
        OnPropertyChanged(nameof(ManualReplacementRegionSummary));
        OnPropertyChanged(nameof(ManualReplacementRegionIsValidText));

        await SaveSettingsAsync();
        StatusText = $"Replacement mask region set from OCR region: {ManualReplacementRegionSummary}";
    }

    private Task PreviewReplacementMaskAsync()
    {
        if (!IsManualReplacementRegionValid)
        {
            StatusText = "Set the replacement mask region first (or click 'Use OCR Region as Mask Region').";
            return Task.CompletedTask;
        }

        EnsureOverlayOpen();

        // Preview relative to the last known game-window rect; fall back to the
        // last replacement snapshot or the overlay's own position.
        var windowRect = _overlayService.LastReplacementSnapshot?.Context.WindowRect
            ?? new OverlayRectangle { X = _x, Y = _y, Width = Math.Max(_width, 1280), Height = Math.Max(_height, 720) };
        var settings = BuildSettings().Replacement;
        var maskRect = ManualReplacementRegionHelper.TryGetScreenRect(
            settings, windowRect.X, windowRect.Y, windowRect.Width, windowRect.Height);
        if (maskRect is null)
        {
            StatusText = "Replacement mask region is not valid.";
            return Task.CompletedTask;
        }

        _overlayService.ApplySettings(BuildSettings());
        _overlayService.UpdateReplacementOverlay(new SubtitleReplacementOverlayUpdate
        {
            Text = string.Empty,
            SourceText = string.Empty,
            Reason = "MANUAL_REGION_PREVIEW",
            ShowMaskOnly = true,
            Context = new SubtitleReplacementContext
            {
                ScreenRect = maskRect.Clone(),
                OverlayRect = maskRect.Clone(),
                WindowRect = windowRect.Clone(),
            },
        });
        StatusText = $"Previewing replacement mask at {ManualReplacementRegionSummary}.";
        return Task.CompletedTask;
    }

    private async Task ResetReplacementRegionAsync()
    {
        ManualReplacementRegionX = 0;
        ManualReplacementRegionY = 0;
        ManualReplacementRegionWidth = 0;
        ManualReplacementRegionHeight = 0;
        OnPropertyChanged(nameof(ManualReplacementRegionSummary));
        OnPropertyChanged(nameof(ManualReplacementRegionIsValidText));
        await SaveSettingsAsync();
        StatusText = "Replacement mask region cleared — falling back to OCR line boxes.";
    }

    private Task TestReplacementOverlayAsync()
    {
        EnsureOverlayOpen();
        _overlayService.UpdateReplacementOverlay(new SubtitleReplacementOverlayUpdate
        {
            SourceText = "More marks of the dragon's fury.",
            Text = "Ejderhanın öfkesinden kalan daha fazla iz.",
            Reason = "TEST_REPLACEMENT_OVERLAY",
            DisplayDurationMs = 2400,
            Context = BuildPreviewReplacementContext(),
        });
        StatusText = "Replacement overlay test shown.";
        return Task.CompletedTask;
    }

    private Task PreviewLastReplacementRectAsync()
    {
        EnsureOverlayOpen();
        var snapshot = _overlayService.LastReplacementSnapshot;
        _overlayService.UpdateReplacementOverlay(new SubtitleReplacementOverlayUpdate
        {
            SourceText = snapshot?.SourceText ?? string.Empty,
            Text = snapshot?.Text ?? "Önizleme",
            Reason = "PREVIEW_LAST_REPLACEMENT_RECT",
            DisplayDurationMs = snapshot?.DisplayDurationMs ?? 0,
            ShowMaskOnly = string.IsNullOrWhiteSpace(snapshot?.Text),
            Context = snapshot?.Context.Clone() ?? BuildPreviewReplacementContext(),
        });
        StatusText = "Last replacement rect preview shown.";
        return Task.CompletedTask;
    }

    private Task ResetReplacementSettingsAsync()
    {
        var defaults = new SubtitleReplacementOverlaySettings();
        _replacementMaskEnabled = defaults.ReplacementMaskEnabled;
        _replacementMaskOpacity = defaults.ReplacementMaskOpacity;
        _replacementPaddingLeft = defaults.ReplacementMaskPaddingLeft;
        _replacementPaddingTop = defaults.ReplacementMaskPaddingTop;
        _replacementPaddingRight = defaults.ReplacementMaskPaddingRight;
        _replacementPaddingBottom = defaults.ReplacementMaskPaddingBottom;
        _replacementFontSize = defaults.ReplacementFontSize;
        _replacementMaxLines = defaults.ReplacementMaxLines;
        _showReplacementRectOutline = defaults.ShowReplacementRectOutline;
        _rejectHudControlText = defaults.RejectHudControlText;
        _useSubtitleCandidateScoring = defaults.UseSubtitleCandidateScoring;
        _translationSettings.EnableReadableSubtitleTiming = true;
        _translationSettings.MinTurkishDisplayMs = 1700;
        _translationSettings.MaxTurkishDisplayMs = 5000;
        _translationSettings.MsPerCharacter = 45;
        _translationSettings.ShowMaskWhileTranslationPending = true;
        RaiseAllProperties();
        StatusText = "Replacement overlay settings reset.";
        return Task.CompletedTask;
    }

    private async Task RunReplacementSelfTestAsync()
    {
        var cases = new[]
        {
            new { Text = "Greetings! Welcome to the guild hall.\nHere we conduct all manner of procedures pertaining to vocations.", ExpectedValid = true },
            new { Text = "ARUD I", ExpectedValid = false },
            new { Text = "Sheathe/Draw LT\nSwitch Weapon Skill RB\nFront Kick Y\nDash B", ExpectedValid = false },
            new { Text = "Always a pleasure!", ExpectedValid = true },
        };
        var results = cases.Select(test =>
        {
            var validation = _candidateValidator.IsValidForReplacementMode(test.Text);
            return new
            {
                test.Text,
                test.ExpectedValid,
                ActualValid = validation.IsValid,
                validation.Reason,
                Passed = validation.IsValid == test.ExpectedValid,
            };
        }).ToArray();

        var debugDirectory = Path.Combine(AppContext.BaseDirectory, "debug");
        Directory.CreateDirectory(debugDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(debugDirectory, "subtitle_replacement_self_test.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        StatusText = results.All(result => result.Passed)
            ? "Replacement self-test passed."
            : "Replacement self-test failed; inspect debug/subtitle_replacement_self_test.json.";
    }

    private Task ApplySettingsAsync()
    {
        try
        {
            if (_overlayService.IsOpen)
                _overlayService.ApplySettings(BuildSettings());

            StatusText = "Overlay settings applied.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to apply overlay settings");
            StatusText = $"Error: {exception.Message}";
        }

        return Task.CompletedTask;
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            var settings = BuildSettings();
            await _settingsService.SaveAsync(settings);
            if (_overlayService.IsOpen)
                _overlayService.ApplySettings(settings);

            StatusText = "Overlay settings saved to overlay.json.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to save overlay settings");
            StatusText = $"Save error: {exception.Message}";
        }
    }

    private Task ResetToPresetAsync()
    {
        ApplyPreset(_selectedPreset);
        if (_overlayService.IsOpen)
            _overlayService.ApplySettings(BuildSettings());

        StatusText = $"Preset reset: {_selectedPreset}";
        return Task.CompletedTask;
    }

    private void OnIsEnabledChanged(bool enabled)
    {
        if (_isConfiguring && !enabled)
        {
            (_x, _y, _width, _height) = _overlayService.ExitConfigMode();
            IsConfiguring = false;
        }

        if (enabled)
        {
            try
            {
                _overlayService.Open(BuildSettings());
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to open overlay");
                StatusText = $"Error opening overlay: {exception.Message}";
                _isEnabled = false;
                OnPropertyChanged(nameof(IsEnabled));
            }

            return;
        }

        _overlayService.Close();
    }

    private async Task LoadSettingsAsync()
    {
        // Validate/recover BEFORE anything else reads settings.X/Y/Width/Height —
        // every subsequent BuildSettings()/Open() call in this ViewModel then
        // automatically uses the corrected, on-screen rectangle (Part B).
        var settings = await _monitorCoordinator.LoadAndValidateAsync();
        ApplyLoadedSettings(settings);
        _overlayService.ApplySettings(settings);
        RaiseAllProperties();
        RefreshMonitorLists();
    }

    private void ApplyLoadedSettings(OverlaySettings settings)
    {
        _isClickThrough = settings.IsClickThrough;
        _opacity = settings.Opacity;
        _x = settings.X;
        _y = settings.Y;
        _width = settings.Width;
        _height = settings.Height;
        _autoFitHeight = settings.AutoFitHeight;
        _maxHeight = settings.MaxHeight;
        _overlayUpdateDebounceMs = settings.OverlayUpdateDebounceMs;
        _overlayTargetMonitorMode = settings.OverlayTargetMonitorMode;
        _selectedOverlayMonitorDeviceName = settings.SelectedOverlayMonitorDeviceName;
        _autoRecoverOffscreenOverlay = settings.AutoRecoverOffscreenOverlay;
        _lastKnownMonitorDeviceName = settings.LastKnownMonitorDeviceName;
        _displayMode = settings.DisplayMode;
        LastRecoveryReasonText = _monitorCoordinator.LastRecoveryReason;
        ManualMonitorWarningText = _monitorCoordinator.LastManualMonitorFallbackWarning ?? string.Empty;

        var style = settings.Style ?? SubtitleOverlayStyleSettings.CreatePreset(SubtitlePreset.Cinematic);
        _selectedPreset = style.SubtitlePreset;
        _fontFamily = style.FontFamily;
        _fontSize = style.FontSize;
        _fontWeight = style.FontWeight;
        _backgroundEnabled = style.BackgroundEnabled;
        _backgroundOpacity = style.BackgroundOpacity;
        _backgroundCornerRadius = style.BackgroundCornerRadius;
        _paddingHorizontal = style.PaddingHorizontal;
        _paddingVertical = style.PaddingVertical;
        _maxWidthPercent = style.MaxWidthPercent;
        _bottomMargin = style.BottomMargin;
        _textAlignment = style.TextAlignment;
        _textColor = style.TextColor;
        _shadowEnabled = style.ShadowEnabled;
        _outlineEnabled = style.OutlineEnabled;
        _outlineThickness = style.OutlineThickness;
        _replacementMaskEnabled = settings.Replacement.ReplacementMaskEnabled;
        _replacementMaskOpacity = settings.Replacement.ReplacementMaskOpacity;
        _replacementPaddingLeft = settings.Replacement.ReplacementMaskPaddingLeft;
        _replacementPaddingTop = settings.Replacement.ReplacementMaskPaddingTop;
        _replacementPaddingRight = settings.Replacement.ReplacementMaskPaddingRight;
        _replacementPaddingBottom = settings.Replacement.ReplacementMaskPaddingBottom;
        _replacementFontSize = settings.Replacement.ReplacementFontSize;
        _replacementMaxLines = settings.Replacement.ReplacementMaxLines;
        _showReplacementRectOutline = settings.Replacement.ShowReplacementRectOutline;
        _rejectHudControlText = settings.Replacement.RejectHudControlText;
        _useSubtitleCandidateScoring = settings.Replacement.UseSubtitleCandidateScoring;
        _useManualReplacementRegion = settings.Replacement.UseManualReplacementRegion;
        _manualReplacementRegionX = settings.Replacement.ManualReplacementRegionX;
        _manualReplacementRegionY = settings.Replacement.ManualReplacementRegionY;
        _manualReplacementRegionWidth = settings.Replacement.ManualReplacementRegionWidth;
        _manualReplacementRegionHeight = settings.Replacement.ManualReplacementRegionHeight;
        _replacementAutoFitText = settings.Replacement.ReplacementAutoFitText;
        _replacementMinFontSize = settings.Replacement.ReplacementMinFontSize;
        if (_displayMode == SubtitleDisplayMode.SubtitleReplacementOverlay)
        {
            _translationSettings.EnableTranslation = true;
            _translationSettings.TurkishOnlyMode = true;
            _translationSettings.ShowSourceWhileTranslating = false;
            _translationSettings.ShowMaskWhileTranslationPending = true;
        }

        UpdateOffScreenState();
    }

    private void EnsureOverlayOpen()
    {
        if (_overlayService.IsOpen) return;

        _overlayService.Open(BuildSettings());
        _isEnabled = true;
        OnPropertyChanged(nameof(IsEnabled));
    }

    // ── Multi-monitor commands (Part D) ──────────────────────────────────────────

    private async Task MoveToPrimaryMonitorAsync()
    {
        var settings = await _monitorCoordinator.MoveToPrimaryMonitorAsync(BuildSettings());
        ApplyLoadedSettings(settings);
        RaiseAllProperties();
        if (_overlayService.IsOpen) _overlayService.ApplySettings(settings);
        StatusText = "Overlay moved to primary monitor.";
    }

    private async Task MoveToCaptureWindowMonitorAsync()
    {
        var settings = await _monitorCoordinator.MoveToCaptureWindowMonitorAsync(BuildSettings());
        ApplyLoadedSettings(settings);
        RaiseAllProperties();
        if (_overlayService.IsOpen) _overlayService.ApplySettings(settings);
        StatusText = "Overlay moved to capture window monitor.";
    }

    private async Task CenterOnCurrentMonitorAsync()
    {
        var settings = await _monitorCoordinator.CenterOnCurrentMonitorAsync(BuildSettings());
        ApplyLoadedSettings(settings);
        RaiseAllProperties();
        if (_overlayService.IsOpen) _overlayService.ApplySettings(settings);
        StatusText = "Overlay centered on current monitor.";
    }

    private async Task SetOverlayAnchorAsync(object? parameter)
    {
        if (parameter is not string name || !Enum.TryParse<OverlayAnchor>(name, out var anchor))
            return;

        var settings = await _monitorCoordinator.AnchorOnTargetMonitorAsync(BuildSettings(), anchor);
        ApplyLoadedSettings(settings);
        RaiseAllProperties();
        if (_overlayService.IsOpen) _overlayService.ApplySettings(settings);
        StatusText = $"Overlay anchored: {anchor}.";
    }

    private async Task ResetOverlayPositionAsync()
    {
        var settings = await _monitorCoordinator.ResetOverlayPositionAsync(BuildSettings());
        ApplyLoadedSettings(settings);
        RaiseAllProperties();
        if (_overlayService.IsOpen) _overlayService.ApplySettings(settings);
        StatusText = "Overlay position reset.";
    }

    private async Task RecoverOverlayNowAsync()
    {
        var settings = await _monitorCoordinator.RecoverNowAsync(BuildSettings());
        ApplyLoadedSettings(settings);
        RaiseAllProperties();
        if (_overlayService.IsOpen) _overlayService.ApplySettings(settings);
        StatusText = "Overlay recovered to an on-screen position.";
    }

    private Task RefreshMonitorsAsync()
    {
        RefreshMonitorLists();
        UpdateOffScreenState();
        return Task.CompletedTask;
    }

    private void RefreshMonitorLists()
    {
        var monitors = _monitorService.GetConnectedMonitors();

        ConnectedMonitorsDisplay.Clear();
        foreach (var monitor in monitors)
            ConnectedMonitorsDisplay.Add(monitor.ToString());

        var previousSelection = _selectedOverlayMonitorDeviceName;
        AvailableMonitorDeviceNames.Clear();
        foreach (var monitor in monitors)
            AvailableMonitorDeviceNames.Add(monitor.DeviceName);

        if (!string.IsNullOrEmpty(previousSelection) && AvailableMonitorDeviceNames.Contains(previousSelection))
            _selectedOverlayMonitorDeviceName = previousSelection;

        RefreshMonitorDiagnosticsText();
    }

    private void RefreshMonitorDiagnosticsText()
    {
        OnPropertyChanged(nameof(PrimaryMonitorText));
        OnPropertyChanged(nameof(CaptureWindowMonitorText));
        OnPropertyChanged(nameof(CurrentOverlayMonitorText));
        OnPropertyChanged(nameof(CurrentOverlayRectText));
        OnPropertyChanged(nameof(OverlayVisibleText));
    }

    private void UpdateOffScreenState()
    {
        IsOverlayOffScreen = !_monitorService.IsRectVisibleOnAnyMonitor(_x, _y, _width, _height);
        RefreshMonitorDiagnosticsText();
    }

    private void OnMonitorConfigurationChanged() =>
        _uiContext.Post(_ => { _ = LoadSettingsAsync(); }, null);

    private void ApplyPreset(SubtitlePreset preset)
    {
        var style = SubtitleOverlayStyleSettings.CreatePreset(preset);
        _selectedPreset = style.SubtitlePreset;
        _fontFamily = style.FontFamily;
        _fontSize = style.FontSize;
        _fontWeight = style.FontWeight;
        _backgroundEnabled = style.BackgroundEnabled;
        _backgroundOpacity = style.BackgroundOpacity;
        _backgroundCornerRadius = style.BackgroundCornerRadius;
        _paddingHorizontal = style.PaddingHorizontal;
        _paddingVertical = style.PaddingVertical;
        _maxWidthPercent = style.MaxWidthPercent;
        _bottomMargin = style.BottomMargin;
        _textAlignment = style.TextAlignment;
        _textColor = style.TextColor;
        _shadowEnabled = style.ShadowEnabled;
        _outlineEnabled = style.OutlineEnabled;
        _outlineThickness = style.OutlineThickness;
        RaiseAllProperties();
    }

    private void RaiseAllProperties()
    {
        OnPropertyChanged(nameof(IsClickThrough));
        OnPropertyChanged(nameof(Opacity));
        OnPropertyChanged(nameof(PresetIndex));
        OnPropertyChanged(nameof(FontSize));
        OnPropertyChanged(nameof(BackgroundOpacity));
        OnPropertyChanged(nameof(BottomMargin));
        OnPropertyChanged(nameof(MaxWidthPercent));
        OnPropertyChanged(nameof(TextColor));
        OnPropertyChanged(nameof(BackgroundEnabled));
        OnPropertyChanged(nameof(OutlineEnabled));
        OnPropertyChanged(nameof(ShadowEnabled));
        OnPropertyChanged(nameof(DisplayModeIndex));
        OnPropertyChanged(nameof(ReplacementMaskEnabled));
        OnPropertyChanged(nameof(ReplacementMaskOpacity));
        OnPropertyChanged(nameof(ReplacementPaddingLeft));
        OnPropertyChanged(nameof(ReplacementPaddingTop));
        OnPropertyChanged(nameof(ReplacementPaddingRight));
        OnPropertyChanged(nameof(ReplacementPaddingBottom));
        OnPropertyChanged(nameof(ReplacementFontSize));
        OnPropertyChanged(nameof(ReplacementMaxLines));
        OnPropertyChanged(nameof(EnableReadableSubtitleTiming));
        OnPropertyChanged(nameof(MinTurkishDisplayMs));
        OnPropertyChanged(nameof(MaxTurkishDisplayMs));
        OnPropertyChanged(nameof(MsPerCharacter));
        OnPropertyChanged(nameof(ShowMaskWhileTranslationPending));
        OnPropertyChanged(nameof(ShowReplacementRectOutline));
        OnPropertyChanged(nameof(RejectHudControlText));
        OnPropertyChanged(nameof(UseSubtitleCandidateScoring));
        OnPropertyChanged(nameof(LastReplacementRectText));
        OnPropertyChanged(nameof(LastDisplayedTurkishText));
        OnPropertyChanged(nameof(LastDisplayDurationText));
        OnPropertyChanged(nameof(LastOverlayUpdateReasonText));
        OnPropertyChanged(nameof(LastAcceptedCandidateText));
        OnPropertyChanged(nameof(LastRejectedReasonText));
        OnPropertyChanged(nameof(WasEnglishBlockedText));
        OnPropertyChanged(nameof(OverlayTargetMonitorModeIndex));
        OnPropertyChanged(nameof(SelectedOverlayMonitorDeviceName));
        OnPropertyChanged(nameof(AutoRecoverOffscreenOverlay));
        RefreshMonitorDiagnosticsText();
    }

    private void OnPipelineDiagnosticsChanged() =>
        _uiContext.Post(_ =>
        {
            OnPropertyChanged(nameof(LastReplacementRectText));
            OnPropertyChanged(nameof(LastDisplayedTurkishText));
            OnPropertyChanged(nameof(LastDisplayDurationText));
            OnPropertyChanged(nameof(LastOverlayUpdateReasonText));
            OnPropertyChanged(nameof(LastAcceptedCandidateText));
            OnPropertyChanged(nameof(LastRejectedReasonText));
            OnPropertyChanged(nameof(WasEnglishBlockedText));
        }, null);

    private OverlaySettings BuildSettings() => new()
    {
        DisplayMode = _displayMode,
        IsEnabled = _isEnabled,
        IsClickThrough = _isClickThrough,
        Opacity = _opacity,
        X = _x,
        Y = _y,
        Width = _width,
        Height = _height,
        AutoFitHeight = _autoFitHeight,
        MaxHeight = _maxHeight,
        OverlayUpdateDebounceMs = _overlayUpdateDebounceMs,
        OverlayTargetMonitorMode = _overlayTargetMonitorMode,
        SelectedOverlayMonitorDeviceName = _selectedOverlayMonitorDeviceName,
        AutoRecoverOffscreenOverlay = _autoRecoverOffscreenOverlay,
        LastKnownMonitorDeviceName = _lastKnownMonitorDeviceName,
        Replacement = new SubtitleReplacementOverlaySettings
        {
            ReplacementMaskEnabled = _replacementMaskEnabled,
            ReplacementMaskOpacity = _replacementMaskOpacity,
            ReplacementMaskPaddingLeft = _replacementPaddingLeft,
            ReplacementMaskPaddingTop = _replacementPaddingTop,
            ReplacementMaskPaddingRight = _replacementPaddingRight,
            ReplacementMaskPaddingBottom = _replacementPaddingBottom,
            ReplacementFontSize = _replacementFontSize,
            ReplacementMaxLines = _replacementMaxLines,
            ShowReplacementRectOutline = _showReplacementRectOutline,
            RejectHudControlText = _rejectHudControlText,
            UseSubtitleCandidateScoring = _useSubtitleCandidateScoring,
            UseManualReplacementRegion = _useManualReplacementRegion,
            ManualReplacementRegionX = _manualReplacementRegionX,
            ManualReplacementRegionY = _manualReplacementRegionY,
            ManualReplacementRegionWidth = _manualReplacementRegionWidth,
            ManualReplacementRegionHeight = _manualReplacementRegionHeight,
            ReplacementAutoFitText = _replacementAutoFitText,
            ReplacementMinFontSize = _replacementMinFontSize,
        },
        Style = new SubtitleOverlayStyleSettings
        {
            SubtitlePreset = _selectedPreset,
            FontFamily = _fontFamily,
            FontSize = _fontSize,
            FontWeight = _fontWeight,
            BackgroundEnabled = _backgroundEnabled,
            BackgroundOpacity = _backgroundOpacity,
            BackgroundCornerRadius = _backgroundCornerRadius,
            PaddingHorizontal = _paddingHorizontal,
            PaddingVertical = _paddingVertical,
            MaxWidthPercent = _maxWidthPercent,
            BottomMargin = _bottomMargin,
            TextAlignment = _textAlignment,
            TextColor = _textColor,
            ShadowEnabled = _shadowEnabled,
            OutlineEnabled = _outlineEnabled,
            OutlineThickness = _outlineThickness,
        },
    };

    private SubtitleReplacementContext BuildPreviewReplacementContext()
    {
        var snapshot = _overlayService.LastReplacementSnapshot;
        if (snapshot is not null)
            return snapshot.Context.Clone();

        return new SubtitleReplacementContext
        {
            ScreenRect = new OverlayRectangle
            {
                X = Math.Max(0, _x + 220),
                Y = Math.Max(0, _y + 100),
                Width = 720,
                Height = 110,
            },
            OverlayRect = new OverlayRectangle
            {
                X = Math.Max(0, _x + 220),
                Y = Math.Max(0, _y + 100),
                Width = 720,
                Height = 110,
            },
            WindowRect = new OverlayRectangle
            {
                X = _x,
                Y = _y,
                Width = _width,
                Height = _height,
            },
            CropRect = new OverlayRectangle
            {
                X = Math.Max(0, _x + 220),
                Y = Math.Max(0, _y + 100),
                Width = 720,
                Height = 110,
            },
            OcrLineRect = new OverlayRectangle
            {
                X = 20,
                Y = 18,
                Width = 680,
                Height = 56,
            },
        };
    }
}
