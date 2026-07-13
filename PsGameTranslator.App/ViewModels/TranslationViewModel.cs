using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Text;
using System.Text.Json;
using System.IO;
using Microsoft.Extensions.Logging;
using PsGameTranslator.App.Commands;
using PsGameTranslator.App.Services;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Translation;

namespace PsGameTranslator.App.ViewModels;

public sealed class TranslationViewModel : ObservableObject
{
    private readonly TranslationSettings _settings;
    private readonly OllamaTranslationService _ollama;
    private readonly FakeTranslationProvider _fakeProvider;
    private readonly MachineTranslationProvider _machineProvider;
    private readonly MachineTranslationServerManager _serverManager;
    private readonly TranslationProviderSelector _providerSelector;
    private readonly TranslationQueue _queue;
    private readonly TranslationCache _cache;
    private readonly PipelineDiagnostics _diagnostics;
    private readonly PipelineDiagnosticsStore _diagnosticsStore;
    private readonly RuntimePipelineHealthService _pipelineHealthService;
    private readonly UserSettingsPersistenceService _persistence;
    private readonly ILogger<TranslationViewModel> _logger;
    private readonly SynchronizationContext _uiContext;

    private string _statusText = "Translation disabled";
    private string _testTranslationText = "More marks of the dragon's fury.";
    private int _selectedFallbackChainIndex;
    private string _lastSourceText = "-";
    private string _lastTranslatedText = "-";
    private string _lastRawResponse = "-";
    private string _lastParsedText = "-";
    private string _lastPostProcessedText = "-";
    private string _lastDurationText = "-";
    private string _lastFromCacheText = "-";
    private string _lastErrorText = string.Empty;
    private string _cacheCountText = "0 entries";
    private string _lastProviderName = "-";
    private string _lastTranslationTimeText = "-";
    private string _serverStatusText = "Not checked";
    private string _providerHealthSummaryText = "Not checked — click 'Check Providers'.";
    private string _serverLastHealthCheckText = "-";
    private string _serverLastHealthErrorText = "-";
    private string _serverProcessIdText = "-";

    public TranslationViewModel(
        TranslationSettings settings,
        OllamaTranslationService ollama,
        FakeTranslationProvider fakeProvider,
        MachineTranslationProvider machineProvider,
        MachineTranslationServerManager serverManager,
        TranslationProviderSelector providerSelector,
        TranslationQueue queue,
        TranslationCache cache,
        PipelineDiagnostics diagnostics,
        PipelineDiagnosticsStore diagnosticsStore,
        RuntimePipelineHealthService pipelineHealthService,
        UserSettingsPersistenceService persistence,
        ILogger<TranslationViewModel> logger)
    {
        _settings = settings;
        _ollama = ollama;
        _fakeProvider = fakeProvider;
        _machineProvider = machineProvider;
        _serverManager = serverManager;
        _providerSelector = providerSelector;
        _queue = queue;
        _cache = cache;
        _diagnostics = diagnostics;
        _diagnosticsStore = diagnosticsStore;
        _pipelineHealthService = pipelineHealthService;
        _persistence = persistence;
        _logger = logger;
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TranslationViewModel must be created on the UI thread.");

        MoveFallbackItemUpCommand = new AsyncRelayCommand(MoveFallbackItemUpAsync);
        MoveFallbackItemDownCommand = new AsyncRelayCommand(MoveFallbackItemDownAsync);
        ResetFallbackOrderCommand = new AsyncRelayCommand(ResetFallbackOrderAsync);
        SyncFallbackChainItems();

        ApplyOrderedTurkishLiveModePresetCommand = new AsyncRelayCommand(ApplyOrderedTurkishLiveModePresetAsync);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync);
        TestTranslateCommand = new AsyncRelayCommand(TestTranslateAsync);
        TestFakeTranslationCommand = new AsyncRelayCommand(TestFakeTranslationAsync);
        TestTranslationServerCommand = new AsyncRelayCommand(TestTranslationServerAsync);
        TestMachineTranslationCommand = new AsyncRelayCommand(TestMachineTranslationAsync);
        StartTranslationServerCommand = new AsyncRelayCommand(StartTranslationServerAsync);
        StopTranslationServerCommand = new AsyncRelayCommand(StopTranslationServerAsync);
        CheckTranslationServerCommand = new AsyncRelayCommand(CheckTranslationServerAsync);
        ClearCacheCommand = new AsyncRelayCommand(ClearCacheAsync);
        RunPipelineHealthCheckCommand = new AsyncRelayCommand(RunPipelineHealthCheckAsync);
        TestLivePipelineEndToEndCommand = new AsyncRelayCommand(TestLivePipelineEndToEndAsync);
        TestSelectedTranslationProviderCommand = new AsyncRelayCommand(TestSelectedTranslationProviderAsync);
        TestProviderChainCommand = new AsyncRelayCommand(TestProviderChainAsync);
        TestReplacementOverlayDisplayCommand = new AsyncRelayCommand(TestReplacementOverlayDisplayAsync);
        CheckAllProvidersCommand = new AsyncRelayCommand(CheckAllProvidersAsync);
        RefreshModelListsCommand = new AsyncRelayCommand(RefreshModelListsAsync);
        _ = RefreshModelListsAsync();
        _queue.TranslationCompleted += OnTranslationCompleted;
        _queue.DiagnosticsChanged += OnDiagnosticsChanged;
        _diagnostics.Changed += OnDiagnosticsChanged;
        _serverManager.Changed += OnServerManagerChanged;
        RefreshCacheCount();
        RefreshServerStatus();
    }

    public bool EnableTranslation
    {
        get => _settings.EnableTranslation;
        set
        {
            if (_settings.EnableTranslation == value) return;
            _settings.EnableTranslation = value;
            _diagnostics.TranslationEnabled = value;
            if (!value)
                _diagnostics.LastTranslationQueueStatus = "disabled";
            _diagnosticsStore.Save();
            OnPropertyChanged();
            RaisePropertyChanged(nameof(TranslationEnabledText));
            RaiseDiagnosticsProperties();
            StatusText = value ? "Translation enabled" : "Translation disabled";
            _logger.LogInformation("Translation {State}", value ? "enabled" : "disabled");

            if (value)
            {
                // Fire-and-forget: never block the UI toggle on server startup.
                _ = EnsureServerRunningInBackgroundAsync();
            }
        }
    }

    // Curated engine list for the Home page "Ceviri Motoru" picker — only real,
    // usable providers (no unconfigured cloud stubs/aliases/debug entries).
    // Order must match the ComboBoxItems in AppShellWindow.xaml's Hizli Ayarlar card.
    private static readonly TranslationProviderType[] CuratedProviders =
    [
        TranslationProviderType.OpusMT,
        TranslationProviderType.Ollama,
        TranslationProviderType.LMStudio,
        TranslationProviderType.GoogleTranslate,
        TranslationProviderType.DeepL,
        TranslationProviderType.Gemini,
        TranslationProviderType.Groq,
    ];

    public int CeviriMotoruIndex
    {
        get
        {
            var index = Array.IndexOf(CuratedProviders, _settings.TranslationProviderType);
            return index >= 0 ? index : 0;
        }
        set
        {
            if (value < 0 || value >= CuratedProviders.Length) return;
            TranslationProviderTypeIndex = (int)CuratedProviders[value];
            OnPropertyChanged();
        }
    }

    public int TranslationProviderTypeIndex
    {
        get => (int)_settings.TranslationProviderType;
        set
        {
            var providerType = (TranslationProviderType)Math.Clamp(value, 0, 11);
            if (_settings.TranslationProviderType == providerType) return;
            _settings.TranslationProviderType = providerType;
            if (providerType is not (TranslationProviderType.None or TranslationProviderType.OpusMT or TranslationProviderType.MachineTranslation) &&
                _settings.ProviderChainMode == TranslationProviderChainMode.LocalOnly)
            {
                _settings.ProviderChainMode = TranslationProviderChainMode.SelectedOnly;
                OnPropertyChanged(nameof(TranslationProviderChainModeIndex));
            }
            OnPropertyChanged();
            RaisePropertyChanged(nameof(SelectedProviderText));
            RaisePropertyChanged(nameof(FallbackProviderText));
            RaisePropertyChanged(nameof(ProviderStatusText));
            RaisePropertyChanged(nameof(CeviriMotoruIndex));
            StatusText = $"Translation provider: {providerType}";
            _persistence.Save();

            if (_settings.EnableTranslation)
            {
                // Fire-and-forget: never block the UI toggle on server startup.
                _ = EnsureServerRunningInBackgroundAsync();
            }

            // Tell the user immediately whether the chosen provider can actually
            // translate, instead of failing silently at the first subtitle.
            _ = ReportSelectedProviderHealthAsync(providerType);
        }
    }

    private async Task ReportSelectedProviderHealthAsync(TranslationProviderType providerType)
    {
        if (providerType == TranslationProviderType.None) return;
        try
        {
            var health = await _providerSelector.CheckAllProviderHealthAsync();
            var selected = health.FirstOrDefault(h => h.ProviderType == providerType);
            if (selected is null)
            {
                _uiContext.Post(_ => StatusText = $"{providerType}: no provider registered.", null);
                return;
            }

            var icon = selected.IsAvailable ? "✅" : "❌";
            _uiContext.Post(_ =>
                StatusText = $"{icon} {selected.ProviderName}: {selected.Message}", null);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "selected_provider_health_check_failed");
        }
    }

    private async Task CheckAllProvidersAsync()
    {
        StatusText = "Checking all translation providers (deep — runs real inference)…";
        try
        {
            var health = await _providerSelector.CheckAllProviderHealthAsync(deep: true);
            var lines = health
                .OrderByDescending(h => h.IsAvailable)
                .Select(h => $"{(h.IsAvailable ? "✅" : "❌")} {h.ProviderName} — {h.Message}");
            ProviderHealthSummaryText = string.Join(Environment.NewLine, lines);
            var available = health.Count(h => h.IsAvailable);
            StatusText = $"Provider check complete — {available}/{health.Count} available.";
        }
        catch (Exception exception)
        {
            StatusText = $"Provider check failed: {exception.Message}";
            _logger.LogWarning(exception, "check_all_providers_failed");
        }
    }

    public int TranslationProfileIndex
    {
        get => (int)_settings.Profile;
        set
        {
            var profile = (TranslationProfile)Math.Clamp(value, 0, 4);
            if (_settings.Profile == profile) return;
            _settings.Profile = profile;
            if (profile != TranslationProfile.Custom)
            {
                _settings.TranslationTimeoutMs = profile switch
                {
                    TranslationProfile.Fast => _settings.FastTranslationTimeoutMs,
                    TranslationProfile.Accurate => _settings.AccurateTranslationTimeoutMs,
                    TranslationProfile.LocalFast => _settings.FastTranslationTimeoutMs,
                    _ => _settings.BalancedTranslationTimeoutMs,
                };
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(TranslationTimeoutMs));
            StatusText = $"Translation profile: {profile}";
        }
    }

    public int TranslationProviderChainModeIndex
    {
        get => (int)_settings.ProviderChainMode;
        set
        {
            var mode = (TranslationProviderChainMode)Math.Clamp(value, 0, 3);
            if (_settings.ProviderChainMode == mode) return;
            _settings.ProviderChainMode = mode;
            OnPropertyChanged();
            RaisePropertyChanged(nameof(FallbackProviderText));
            RaisePropertyChanged(nameof(ProviderStatusText));
            StatusText = $"Provider mode: {mode}";
        }
    }

    public bool EnableTranslationProviderFallback
    {
        get => _settings.EnableTranslationProviderFallback;
        set
        {
            if (_settings.EnableTranslationProviderFallback == value) return;
            _settings.EnableTranslationProviderFallback = value;
            OnPropertyChanged();
            RaisePropertyChanged(nameof(FallbackProviderText));
            StatusText = value ? "Translation provider fallback enabled." : "Translation provider fallback disabled.";
        }
    }

    public bool AllowFallbackDuringProviderTest
    {
        get => _settings.AllowFallbackDuringProviderTest;
        set
        {
            if (_settings.AllowFallbackDuringProviderTest == value) return;
            _settings.AllowFallbackDuringProviderTest = value;
            OnPropertyChanged();
            StatusText = value
                ? "Provider tests may use fallback."
                : "Provider tests use selected provider only.";
        }
    }

    public bool AutoStartOpusServer
    {
        get => _settings.AutoStartOpusServer;
        set
        {
            if (_settings.AutoStartOpusServer == value) return;
            _settings.AutoStartOpusServer = value;
            OnPropertyChanged();
            StatusText = value ? "OPUS-MT auto-start enabled." : "OPUS-MT auto-start disabled.";
        }
    }

    public bool StartOpusOnlyWhenSelectedOrFallback
    {
        get => _settings.StartOpusOnlyWhenSelectedOrFallback;
        set
        {
            if (_settings.StartOpusOnlyWhenSelectedOrFallback == value) return;
            _settings.StartOpusOnlyWhenSelectedOrFallback = value;
            OnPropertyChanged();
            StatusText = value
                ? "OPUS-MT will start only when selected or fallback is active."
                : "OPUS-MT may start whenever translation server auto-start is enabled.";
        }
    }

    public bool UseFakeTranslationProviderForDebug
    {
        get => _settings.UseFakeTranslationProviderForDebug;
        set
        {
            if (_settings.UseFakeTranslationProviderForDebug == value) return;
            _settings.UseFakeTranslationProviderForDebug = value;
            OnPropertyChanged();
            StatusText = value
                ? "WARNING: fake translation provider override is ON (debug only)."
                : "Fake translation provider override is off.";
            _logger.LogWarning(
                "UseFakeTranslationProviderForDebug set to {Value}", value);
        }
    }

    public string SourceLanguage
    {
        get => _settings.SourceLanguage;
        set { if (_settings.SourceLanguage != value) { _settings.SourceLanguage = value; OnPropertyChanged(); _persistence.Save(); } }
    }

    public string TargetLanguage
    {
        get => _settings.TargetLanguage;
        set { if (_settings.TargetLanguage != value) { _settings.TargetLanguage = value; OnPropertyChanged(); _persistence.Save(); } }
    }

    public string MachineTranslationBaseUrl
    {
        get => _settings.MachineTranslationBaseUrl;
        set
        {
            if (_settings.MachineTranslationBaseUrl != value)
            {
                _settings.MachineTranslationBaseUrl = value;
                OnPropertyChanged();
            }
        }
    }

    public int MachineTranslationTimeoutMs
    {
        get => _settings.MachineTranslationTimeoutMs;
        set
        {
            var clamped = Math.Max(100, value);
            if (_settings.MachineTranslationTimeoutMs == clamped) return;
            _settings.MachineTranslationTimeoutMs = clamped;
            OnPropertyChanged();
        }
    }

    public string OllamaBaseUrl
    {
        get => _settings.OllamaBaseUrl;
        set { if (_settings.OllamaBaseUrl != value) { _settings.OllamaBaseUrl = value; OnPropertyChanged(); } }
    }

    public string OllamaModel
    {
        get => _settings.OllamaModel;
        set { if (_settings.OllamaModel != value) { _settings.OllamaModel = value; OnPropertyChanged(); _persistence.Save(); } }
    }

    // ── Provider/model separation ────────────────────────────────────────────

    public IReadOnlyList<string> MachineTranslationModelOptions { get; } =
        TranslationModelCatalog.MachineTranslationModels.Select(m => m.ModelId).ToArray();

    public string MachineTranslationModel
    {
        get => _settings.MachineTranslationModel;
        set
        {
            if (_settings.MachineTranslationModel == value || string.IsNullOrWhiteSpace(value)) return;
            _settings.MachineTranslationModel = value.Trim();
            OnPropertyChanged();
            _persistence.Save();
            StatusText = _serverManager.State == MachineTranslationServerState.Running
                ? $"Model set to {value}. Restart the translation server to apply."
                : $"Model set to {value}. It will be used on next server start.";
        }
    }

    public System.Collections.ObjectModel.ObservableCollection<string> OllamaModels { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<string> LmStudioModels { get; } = [];

    public string LmStudioModel
    {
        get => _settings.LmStudioModel;
        set { if (_settings.LmStudioModel != value) { _settings.LmStudioModel = value ?? string.Empty; OnPropertyChanged(); _persistence.Save(); } }
    }

    public string LmStudioBaseUrl
    {
        get => _settings.LmStudioBaseUrl;
        set { if (_settings.LmStudioBaseUrl != value) { _settings.LmStudioBaseUrl = value; OnPropertyChanged(); _persistence.Save(); } }
    }

    // Official Google Cloud Translation API key. Empty = GoogleTranslateProvider
    // uses the free unofficial endpoint instead.
    public string GoogleTranslateApiKey
    {
        get => _settings.GoogleTranslateApiKey;
        set
        {
            if (_settings.GoogleTranslateApiKey == value) return;
            _settings.GoogleTranslateApiKey = value ?? string.Empty;
            OnPropertyChanged();
            RaisePropertyChanged(nameof(GoogleTranslateModeText));
            _persistence.Save();
        }
    }

    public string GoogleTranslateModeText => string.IsNullOrWhiteSpace(_settings.GoogleTranslateApiKey)
        ? "Ucretsiz (API anahtari yok)"
        : "Resmi API (ucretli, aylik 500K karakter ucretsiz)";

    public string DeepLApiKey
    {
        get => _settings.DeepLApiKey;
        set { if (_settings.DeepLApiKey != value) { _settings.DeepLApiKey = value ?? string.Empty; OnPropertyChanged(); _persistence.Save(); } }
    }

    public string GeminiApiKey
    {
        get => _settings.GeminiApiKey;
        set { if (_settings.GeminiApiKey != value) { _settings.GeminiApiKey = value ?? string.Empty; OnPropertyChanged(); _persistence.Save(); } }
    }

    public string GroqApiKey
    {
        get => _settings.GroqApiKey;
        set { if (_settings.GroqApiKey != value) { _settings.GroqApiKey = value ?? string.Empty; OnPropertyChanged(); _persistence.Save(); } }
    }

    // How long (from the first OCR read of a growing line) the pipeline keeps
    // merging continuation reads before flushing for translation. Raise this
    // for games with a slow "typewriter" subtitle reveal — otherwise a
    // half-typed sentence gets translated, then the full sentence gets
    // translated again moments later as a separate line.
    public int MaxSubtitleMergeWindowMs
    {
        get => _settings.MaxSubtitleMergeWindowMs;
        set
        {
            var clamped = Math.Max(150, value);
            if (_settings.MaxSubtitleMergeWindowMs == clamped) return;
            _settings.MaxSubtitleMergeWindowMs = clamped;
            OnPropertyChanged();
            _persistence.Save();
        }
    }

    // Quiet period after the last change to a growing line before it's
    // considered "done typing" and flushed (bounded by MaxSubtitleMergeWindowMs).
    public int SubtitleStabilizationMs
    {
        get => _settings.SubtitleStabilizationMs;
        set
        {
            var clamped = Math.Max(50, value);
            if (_settings.SubtitleStabilizationMs == clamped) return;
            _settings.SubtitleStabilizationMs = clamped;
            OnPropertyChanged();
            _persistence.Save();
        }
    }

    /// <summary>Fills the Ollama / LM Studio model pickers from the live servers.</summary>
    private async Task RefreshModelListsAsync()
    {
        StatusText = "Refreshing model lists…";
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };

        var ollamaModels = new List<string>();
        try
        {
            var json = await http.GetStringAsync(_settings.OllamaBaseUrl.TrimEnd('/') + "/api/tags");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var models))
                foreach (var model in models.EnumerateArray())
                    if (model.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } n)
                        ollamaModels.Add(n);
        }
        catch { /* server down — suggested models below */ }
        if (ollamaModels.Count == 0)
            ollamaModels.AddRange(TranslationModelCatalog.SuggestedOllamaModels.Select(m => m.ModelId));

        var lmModels = new List<string>();
        try
        {
            var json = await http.GetStringAsync(_settings.LmStudioBaseUrl.TrimEnd('/') + "/v1/models");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data))
                foreach (var model in data.EnumerateArray())
                    if (model.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } n)
                        lmModels.Add(n);
        }
        catch { /* LM Studio not running */ }

        _uiContext.Post(_ =>
        {
            OllamaModels.Clear();
            foreach (var model in ollamaModels) OllamaModels.Add(model);
            LmStudioModels.Clear();
            foreach (var model in lmModels) LmStudioModels.Add(model);
            StatusText = $"Models: Ollama {OllamaModels.Count} · LM Studio {LmStudioModels.Count}" +
                         (lmModels.Count == 0 ? " (LM Studio not reachable)" : string.Empty);
        }, null);
    }

    public int TranslationTimeoutMs
    {
        get => _settings.TranslationTimeoutMs;
        set
        {
            var clamped = Math.Max(500, value);
            if (_settings.TranslationTimeoutMs != clamped)
            {
                _settings.TranslationTimeoutMs = clamped;
                OnPropertyChanged();
            }
        }
    }

    public bool UseTranslationCache
    {
        get => _settings.UseTranslationCache;
        set
        {
            if (_settings.UseTranslationCache != value)
            {
                _settings.UseTranslationCache = value;
                OnPropertyChanged();
            }
        }
    }

    public bool EnableGlossaryCorrections
    {
        get => _settings.EnableGlossaryCorrections;
        set
        {
            if (_settings.EnableGlossaryCorrections != value)
            {
                _settings.EnableGlossaryCorrections = value;
                OnPropertyChanged();
                _persistence.Save();
            }
        }
    }

    public bool DropStaleTranslations
    {
        get => _settings.DropStaleTranslations;
        set { if (_settings.DropStaleTranslations != value) { _settings.DropStaleTranslations = value; OnPropertyChanged(); } }
    }

    public bool ShowOcrFallbackWhenTranslationFails
    {
        get => _settings.ShowOcrFallbackWhenTranslationFails;
        set
        {
            if (_settings.ShowOcrFallbackWhenTranslationFails != value)
            {
                _settings.ShowOcrFallbackWhenTranslationFails = value;
                OnPropertyChanged();
            }
        }
    }

    public bool ShowSourceWhileTranslating
    {
        get => _settings.ShowSourceWhileTranslating;
        set
        {
            if (_settings.ShowSourceWhileTranslating == value) return;
            _settings.ShowSourceWhileTranslating = value;
            OnPropertyChanged();
        }
    }

    public bool KeepTranslatedTextWhileSameSourceDetected
    {
        get => _settings.KeepTranslatedTextWhileSameSourceDetected;
        set
        {
            if (_settings.KeepTranslatedTextWhileSameSourceDetected == value) return;
            _settings.KeepTranslatedTextWhileSameSourceDetected = value;
            OnPropertyChanged();
        }
    }

    public int MinTranslatedDisplayMs
    {
        get => _settings.MinTranslatedDisplayMs;
        set
        {
            var clamped = Math.Max(0, value);
            if (_settings.MinTranslatedDisplayMs == clamped) return;
            _settings.MinTranslatedDisplayMs = clamped;
            OnPropertyChanged();
        }
    }

    public int ClearOverlayAfterNoSubtitleMs
    {
        get => _settings.ClearOverlayAfterNoSubtitleMs;
        set
        {
            var clamped = Math.Max(0, value);
            if (_settings.ClearOverlayAfterNoSubtitleMs == clamped) return;
            _settings.ClearOverlayAfterNoSubtitleMs = clamped;
            OnPropertyChanged();
        }
    }

    public int DisplayModeIndex
    {
        get => (int)_settings.DisplayMode;
        set
        {
            var mode = (TranslationDisplayMode)Math.Clamp(value, 0, 2);
            if (_settings.DisplayMode == mode) return;
            _settings.DisplayMode = mode;
            _diagnostics.TranslationDisplayMode = mode;
            _diagnosticsStore.Save();
            OnPropertyChanged();
            RaiseDiagnosticsProperties();
        }
    }

    public string TestTranslationText { get => _testTranslationText; set => SetProperty(ref _testTranslationText, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string LastSourceText { get => _lastSourceText; private set => SetProperty(ref _lastSourceText, value); }

    public string LastTranslatedText
    {
        get => _lastTranslatedText;
        private set
        {
            if (!SetProperty(ref _lastTranslatedText, value))
                return;

            if (value != "-" && !string.IsNullOrWhiteSpace(_lastSourceText) && _lastSourceText != "-")
                RecordHistoryEntry(_lastSourceText, value);
        }
    }

    // Most-recent-first, capped so the "Son Cevrilenler" panel stays short.
    public ObservableCollection<TranslationHistoryEntry> RecentTranslations { get; } = new();
    private const int MaxRecentTranslations = 8;

    private void RecordHistoryEntry(string sourceText, string translatedText)
    {
        RecentTranslations.Insert(0, new TranslationHistoryEntry(sourceText, translatedText, DateTime.Now));
        while (RecentTranslations.Count > MaxRecentTranslations)
            RecentTranslations.RemoveAt(RecentTranslations.Count - 1);
    }
    public string LastRawResponse { get => _lastRawResponse; private set => SetProperty(ref _lastRawResponse, value); }
    public string LastParsedText { get => _lastParsedText; private set => SetProperty(ref _lastParsedText, value); }
    public string LastPostProcessedText { get => _lastPostProcessedText; private set => SetProperty(ref _lastPostProcessedText, value); }
    public string LastDurationText { get => _lastDurationText; private set => SetProperty(ref _lastDurationText, value); }
    public string LastFromCacheText { get => _lastFromCacheText; private set => SetProperty(ref _lastFromCacheText, value); }
    public string LastErrorText { get => _lastErrorText; private set => SetProperty(ref _lastErrorText, value); }
    public string CacheCountText { get => _cacheCountText; private set => SetProperty(ref _cacheCountText, value); }
    public string LastProviderName { get => _lastProviderName; private set => SetProperty(ref _lastProviderName, value); }
    public string LastTranslationTimeText { get => _lastTranslationTimeText; private set => SetProperty(ref _lastTranslationTimeText, value); }
    public string TranslationQueueStatus => _diagnostics.LastTranslationQueueStatus;
    public string TranslationEnabledText => _settings.EnableTranslation ? "true" : "false";
    public string SelectedProviderText => _settings.ProviderType.ToString();
    public string ActualProviderUsedText =>
        string.IsNullOrWhiteSpace(_diagnostics.ActualProviderUsed) ? "-" : _diagnostics.ActualProviderUsed;
    public string FallbackProviderText =>
        _settings.EnableTranslationProviderFallback &&
        _settings.ProviderChainMode is TranslationProviderChainMode.ProviderChain or TranslationProviderChainMode.HybridBalanced
            ? "OPUS-MT"
            : "none";
    public string ProviderStatusText =>
        _settings.ProviderChainMode == TranslationProviderChainMode.LocalOnly
            ? $"LocalOnly: OPUS-MT ({TranslationServerStatusText})"
            : $"{_settings.ProviderChainMode}: selected={_settings.ProviderType}, actual={ActualProviderUsedText}";
    public string LastFallbackReasonText =>
        _diagnostics.LastTranslationWasFallbackUsed
            ? _diagnostics.LastTranslationFallbackReason
            : "-";
    public string TranslationServerStatusText { get => _serverStatusText; private set => SetProperty(ref _serverStatusText, value); }
    public string ProviderHealthSummaryText { get => _providerHealthSummaryText; private set => SetProperty(ref _providerHealthSummaryText, value); }
    public string TranslationServerLastHealthCheckText { get => _serverLastHealthCheckText; private set => SetProperty(ref _serverLastHealthCheckText, value); }
    public string TranslationServerLastHealthErrorText { get => _serverLastHealthErrorText; private set => SetProperty(ref _serverLastHealthErrorText, value); }
    public string TranslationServerProcessIdText { get => _serverProcessIdText; private set => SetProperty(ref _serverProcessIdText, value); }
    public string TranslationCountersText =>
        $"enqueued={_diagnostics.TranslationEnqueueCount}, started={_diagnostics.TranslationStartedCount}, " +
        $"completed={_diagnostics.TranslationCompletedCount}, failed={_diagnostics.TranslationFailedCount}, " +
        $"dropped={_diagnostics.TranslationDroppedCount}, cache={_diagnostics.TranslationCacheHitCount}";
    public string PipelineDiagnosticsText => BuildPipelineDiagnosticsText();

    public ObservableCollection<string> FallbackChainItems { get; } = [];

    public int SelectedFallbackChainIndex
    {
        get => _selectedFallbackChainIndex;
        set => SetProperty(ref _selectedFallbackChainIndex, value);
    }

    public ICommand MoveFallbackItemUpCommand { get; }
    public ICommand MoveFallbackItemDownCommand { get; }
    public ICommand ResetFallbackOrderCommand { get; }

    private static readonly TranslationProviderType[] FallbackChainTypes =
    [
        TranslationProviderType.MachineTranslation,
        TranslationProviderType.GoogleTranslate,
        TranslationProviderType.DeepL,
        TranslationProviderType.Gemini,
        TranslationProviderType.Groq,
    ];

    private static readonly string[] FallbackChainDisplayNames =
        ["OPUS-MT (Local)", "Google Translate", "DeepL", "Gemini", "Groq"];

    private void SyncFallbackChainItems()
    {
        FallbackChainItems.Clear();
        foreach (var type in _settings.FallbackProviderOrder)
        {
            var idx = Array.IndexOf(FallbackChainTypes, type);
            FallbackChainItems.Add(idx >= 0 ? FallbackChainDisplayNames[idx] : type.ToString());
        }
    }

    private Task MoveFallbackItemUpAsync()
    {
        var idx = _selectedFallbackChainIndex;
        if (idx <= 0 || idx >= _settings.FallbackProviderOrder.Count) return Task.CompletedTask;
        (_settings.FallbackProviderOrder[idx - 1], _settings.FallbackProviderOrder[idx]) =
            (_settings.FallbackProviderOrder[idx], _settings.FallbackProviderOrder[idx - 1]);
        SyncFallbackChainItems();
        SelectedFallbackChainIndex = idx - 1;
        return Task.CompletedTask;
    }

    private Task MoveFallbackItemDownAsync()
    {
        var idx = _selectedFallbackChainIndex;
        if (idx < 0 || idx >= _settings.FallbackProviderOrder.Count - 1) return Task.CompletedTask;
        (_settings.FallbackProviderOrder[idx], _settings.FallbackProviderOrder[idx + 1]) =
            (_settings.FallbackProviderOrder[idx + 1], _settings.FallbackProviderOrder[idx]);
        SyncFallbackChainItems();
        SelectedFallbackChainIndex = idx + 1;
        return Task.CompletedTask;
    }

    private Task ResetFallbackOrderAsync()
    {
        _settings.FallbackProviderOrder.Clear();
        _settings.FallbackProviderOrder.AddRange(FallbackChainTypes);
        SyncFallbackChainItems();
        SelectedFallbackChainIndex = 0;
        return Task.CompletedTask;
    }

    public ICommand ApplyOrderedTurkishLiveModePresetCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand TestTranslateCommand { get; }
    public ICommand TestFakeTranslationCommand { get; }
    public ICommand TestTranslationServerCommand { get; }
    public ICommand TestMachineTranslationCommand { get; }
    public ICommand StartTranslationServerCommand { get; }
    public ICommand StopTranslationServerCommand { get; }
    public ICommand CheckTranslationServerCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand RunPipelineHealthCheckCommand { get; }
    public ICommand TestLivePipelineEndToEndCommand { get; }
    public ICommand TestSelectedTranslationProviderCommand { get; }
    public ICommand TestProviderChainCommand { get; }
    public ICommand TestReplacementOverlayDisplayCommand { get; }
    public ICommand CheckAllProvidersCommand { get; }
    public ICommand RefreshModelListsCommand { get; }

    private Task ApplyOrderedTurkishLiveModePresetAsync()
    {
        _settings.EnableTranslation = true;
        _settings.ProviderType = TranslationProviderType.OpusMT;
        _settings.ProviderChainMode = TranslationProviderChainMode.LocalOnly;
        _settings.EnableTranslationProviderFallback = true;
        _settings.AutoStartOpusServer = true;
        _settings.StartOpusOnlyWhenSelectedOrFallback = true;
        _settings.TurkishOnlyMode = true;
        _settings.ShowSourceWhileTranslating = false;
        _settings.KeepPreviousTurkishWhileTranslating = true;
        _settings.ShowMaskWhileTranslationPending = true;
        _settings.PreserveQueueOrder = true;
        _settings.MaxCapturedQueueSize = 12;
        _settings.OrderedTranslationQueueMaxSize = 8;
        _settings.MaxPlaybackQueueSize = 10;
        _settings.EnableReadableSubtitleTiming = true;
        _settings.MinTurkishDisplayMs = 1700;
        _settings.MaxTurkishDisplayMs = 5000;
        _settings.MsPerCharacter = 45;
        _settings.ExtraLineMs = 350;
        _settings.SubtitleStabilizationMs = 150;
        _settings.MaxSubtitleMergeWindowMs = 2500;
        _settings.EnableTranslationMemory = true;
        _settings.UseTranslationCache = true;
        _settings.EnableDatasetQualityFilter = true;
        _settings.EnableOllamaRefinement = false;

        StatusText = "Applied preset: Ordered Turkish Live Mode.";
        _logger.LogInformation("applied_preset_ordered_turkish_live_mode");
        OnPropertyChanged(nameof(EnableTranslation));
        OnPropertyChanged(nameof(UseTranslationCache));
        OnPropertyChanged(nameof(TranslationProviderTypeIndex));
        OnPropertyChanged(nameof(TranslationProviderChainModeIndex));
        OnPropertyChanged(nameof(EnableTranslationProviderFallback));
        OnPropertyChanged(nameof(AutoStartOpusServer));
        OnPropertyChanged(nameof(StartOpusOnlyWhenSelectedOrFallback));
        RaisePropertyChanged(nameof(TranslationEnabledText));
        return Task.CompletedTask;
    }

    private async Task TestConnectionAsync()
    {
        StatusText = "Testing Ollama connection...";
        var (success, message) = await _ollama.TestConnectionAsync();
        StatusText = message;
        LastErrorText = success ? string.Empty : message;
        RaiseDiagnosticsProperties();
    }

    private async Task TestTranslateAsync()
    {
        if (string.IsNullOrWhiteSpace(TestTranslationText))
        {
            StatusText = "Enter test text first.";
            return;
        }

        StatusText = "Translating test text...";
        _diagnostics.LastTranslationStartedAt = DateTimeOffset.Now;
        _diagnostics.LastTranslationSourceText = TestTranslationText.Trim();
        _diagnostics.LastTranslationQueueStatus = "processing";
        _diagnosticsStore.Save();
        RaiseDiagnosticsProperties();
        var result = await _ollama.TranslateAsync(new TranslationRequest
        {
            SourceText = TestTranslationText.Trim(),
            SourceLanguage = _settings.SourceLanguage,
            TargetLanguage = _settings.TargetLanguage,
            GameProfile = _settings.GameProfile,
        });
        ApplyDiagnostics(result);
        ApplyResultToPipelineDiagnostics(result);
        StatusText = result.Success ? "Test translation OK" : result.ErrorMessage ?? "Translation failed.";
    }

    private async Task TestFakeTranslationAsync()
    {
        const string sample = "More marks of the dragon's fury.";
        StatusText = "Testing fake translation...";
        _diagnostics.TranslationEnqueueCount++;
        _diagnostics.TranslationStartedCount++;
        _diagnostics.LastTranslationStartedAt = DateTimeOffset.Now;
        _diagnostics.LastTranslationQueueStatus = "processing";
        _diagnostics.ActualProviderUsed = _fakeProvider.ProviderName;
        var result = await _fakeProvider.TranslateAsync(new TranslationRequest
        {
            SourceText = sample,
            SourceLanguage = _settings.SourceLanguage,
            TargetLanguage = _settings.TargetLanguage,
            GameProfileName = _settings.GameProfile,
        });

        ApplyDiagnostics(result);
        if (result.Success) _diagnostics.TranslationCompletedCount++;
        else _diagnostics.TranslationFailedCount++;
        ApplyResultToPipelineDiagnostics(result);
        await SaveTranslationResultAsync(result);
        StatusText = result.Success ? result.TranslatedText : result.ErrorMessage ?? "Fake translation failed.";
    }

    private async Task TestTranslationServerAsync()
    {
        StatusText = "Testing translation server...";
        try
        {
            var (success, message) = await _machineProvider.TestServerAsync();
            StatusText = message;
            LastErrorText = success ? string.Empty : message;
        }
        catch (Exception exception)
        {
            StatusText = $"Server test failed: {exception.Message}";
            LastErrorText = exception.Message;
        }
        RaiseDiagnosticsProperties();
    }

    private async Task TestMachineTranslationAsync()
    {
        if (string.IsNullOrWhiteSpace(TestTranslationText))
        {
            StatusText = "Enter test text first.";
            return;
        }

        // Ensure the server is up (auto-starts it if needed) before testing.
        StatusText = "Checking translation server...";
        var serverReady = await _serverManager.EnsureRunningAsync();
        if (!serverReady)
        {
            StatusText = $"Translation server not ready: {_serverManager.LastStartError}";
            return;
        }

        // Direct call: bypasses OCR, SubtitleFormatter, cache and queue.
        StatusText = "Translating with machine translation server...";
        _diagnostics.TranslationEnqueueCount++;
        _diagnostics.TranslationStartedCount++;
        _diagnostics.LastTranslationStartedAt = DateTimeOffset.Now;
        _diagnostics.LastTranslationSourceText = TestTranslationText.Trim();
        _diagnostics.LastTranslationQueueStatus = "processing";
        _diagnostics.ActualProviderUsed = _machineProvider.ProviderName;
        _diagnosticsStore.Save();
        RaiseDiagnosticsProperties();

        var result = await _machineProvider.TranslateAsync(new TranslationRequest
        {
            SourceText = TestTranslationText.Trim(),
            SourceLanguage = _settings.SourceLanguage,
            TargetLanguage = _settings.TargetLanguage,
            GameProfileName = _settings.GameProfile,
        });

        ApplyDiagnostics(result);
        if (result.Success) _diagnostics.TranslationCompletedCount++;
        else _diagnostics.TranslationFailedCount++;
        ApplyResultToPipelineDiagnostics(result);
        await SaveTranslationResultAsync(result);
        StatusText = result.Success
            ? $"Machine translation OK ({result.DurationMs} ms): {result.TranslatedText}"
            : result.ErrorMessage ?? "Machine translation failed.";
    }

    private Task ClearCacheAsync()
    {
        _cache.Clear();
        RefreshCacheCount();
        StatusText = "Translation cache cleared.";
        return Task.CompletedTask;
    }

    private async Task RunPipelineHealthCheckAsync()
    {
        StatusText = "Running pipeline health check...";
        var report = await _pipelineHealthService.RunHealthCheckAsync();
        StatusText =
            $"Health: OCR provider={(report.OcrProviderOk ? "PASS" : "FAIL")}, " +
            $"translation provider={(report.TranslationProviderOk ? "PASS" : "FAIL")}, " +
            $"replacement overlay={(report.ReplacementOverlayOk ? "PASS" : "FAIL")}, " +
            $"manual region={(report.ManualReplacementRegionOk ? "PASS" : "FAIL")}, " +
            $"Turkish displayed={(report.TurkishDisplayedOk ? "PASS" : "FAIL")}.";
        RaiseDiagnosticsProperties();
    }

    private async Task TestLivePipelineEndToEndAsync()
    {
        StatusText = "Running live pipeline end-to-end test...";
        var report = await _pipelineHealthService.RunEndToEndPipelineTestAsync();
        LastSourceText = report.DialogueText;
        LastTranslatedText = report.PostProcessedTranslation;
        LastProviderName = report.ProviderUsed;
        LastErrorText = report.Success ? string.Empty : $"{report.FailureStage}: {report.FailureReason}";
        StatusText = report.Success
            ? "End-to-end pipeline PASS: Turkish displayed inside ManualReplacementRegion."
            : $"End-to-end pipeline FAIL: {report.FailureStage} - {report.FailureReason}";
        RaiseDiagnosticsProperties();
    }

    private async Task TestSelectedTranslationProviderAsync()
    {
        StatusText = "Testing selected translation provider...";
        var report = await _pipelineHealthService.RunSelectedTranslationProviderTestAsync();
        LastSourceText = report.SourceText;
        LastTranslatedText = report.PostProcessedTranslation;
        LastProviderName = report.ProviderUsed;
        LastDurationText = $"{report.DurationMs} ms";
        LastErrorText = report.Success ? string.Empty : report.ErrorMessage;
        LastTranslationTimeText = report.Timestamp.ToString("HH:mm:ss.fff");
        StatusText = report.Success
            ? $"Selected translation provider PASS: {report.PostProcessedTranslation}"
            : $"Selected translation provider FAIL: {report.ErrorMessage}";
        RaiseDiagnosticsProperties();
    }

    private async Task TestProviderChainAsync()
    {
        StatusText = "Testing translation provider chain...";
        var report = await _pipelineHealthService.RunProviderChainTestAsync();
        LastSourceText = report.SourceText;
        LastTranslatedText = report.PostProcessedTranslation;
        LastProviderName = report.ProviderUsed;
        LastDurationText = $"{report.DurationMs} ms";
        LastErrorText = report.Success ? string.Empty : report.ErrorMessage;
        LastTranslationTimeText = report.Timestamp.ToString("HH:mm:ss.fff");
        StatusText = report.Success
            ? $"Provider chain PASS: final provider={report.ProviderUsed}"
            : $"Provider chain FAIL: {report.ErrorMessage}";
        RaiseDiagnosticsProperties();
    }

    private async Task TestReplacementOverlayDisplayAsync()
    {
        StatusText = "Testing replacement overlay display...";
        var report = await _pipelineHealthService.RunReplacementOverlayDisplayTestAsync();
        LastSourceText = "overlay-only";
        LastTranslatedText = report.DisplayedText;
        LastProviderName = "none";
        LastErrorText = report.Success ? string.Empty : report.FailureReason;
        LastTranslationTimeText = report.Timestamp.ToString("HH:mm:ss.fff");
        StatusText = report.Success
            ? "Replacement overlay display PASS."
            : $"Replacement overlay display FAIL: {report.FailureReason}";
        RaiseDiagnosticsProperties();
    }

    private async Task StartTranslationServerAsync()
    {
        StatusText = "Starting translation server...";
        var success = await _serverManager.StartServerAsync();
        StatusText = success
            ? "Translation server started and healthy."
            : $"Failed to start translation server: {_serverManager.LastStartError}";
    }

    private async Task StopTranslationServerAsync()
    {
        StatusText = "Stopping translation server...";
        await _serverManager.StopServerAsync();
        StatusText = _serverManager.State == MachineTranslationServerState.Stopped
            ? "Translation server stopped."
            : "Translation server was not started by this app — left untouched.";
    }

    private async Task CheckTranslationServerAsync()
    {
        StatusText = "Checking translation server...";
        var healthy = await _serverManager.CheckHealthAsync();
        StatusText = healthy
            ? "Translation server is healthy."
            : $"Translation server unreachable: {_serverManager.LastHealthError}";
    }

    private async Task EnsureServerRunningInBackgroundAsync()
    {
        try { await _serverManager.EnsureRunningIfEnabledAsync(); }
        catch (Exception exception) { _logger.LogWarning(exception, "translation_server_ensure_running_failed"); }
    }

    private void OnServerManagerChanged() =>
        _uiContext.Post(_ => RefreshServerStatus(), null);

    private void RefreshServerStatus()
    {
        TranslationServerStatusText = _serverManager.State.ToString();
        TranslationServerLastHealthCheckText =
            _serverManager.LastHealthCheckAt?.ToString("HH:mm:ss.fff") ?? "-";
        TranslationServerLastHealthErrorText =
            string.IsNullOrWhiteSpace(_serverManager.LastHealthError) ? "-" : _serverManager.LastHealthError;
        TranslationServerProcessIdText =
            _serverManager.ProcessId?.ToString() ?? "-";
    }

    private void OnTranslationCompleted(TranslationResult result)
    {
        _uiContext.Post(_ =>
        {
            ApplyDiagnostics(result);
            StatusText = result.Success
                ? (result.FromCache ? "Translated (from cache)" : "Translated")
                : result.ErrorMessage ?? "Translation failed.";
            RefreshCacheCount();
            RaiseDiagnosticsProperties();
        }, null);
    }

    private void OnDiagnosticsChanged() =>
        _uiContext.Post(_ =>
        {
            LastSourceText = string.IsNullOrWhiteSpace(_diagnostics.LastTranslationSourceText)
                ? "-" : _diagnostics.LastTranslationSourceText;
            LastTranslatedText = string.IsNullOrWhiteSpace(_diagnostics.LastTranslationPostProcessedText)
                ? "-" : _diagnostics.LastTranslationPostProcessedText;
            LastProviderName = string.IsNullOrWhiteSpace(_diagnostics.LastTranslationProviderName)
                ? "-" : _diagnostics.LastTranslationProviderName;
            LastDurationText = $"{_diagnostics.LastTranslationDurationMs} ms";
            LastErrorText = _diagnostics.LastTranslationError;
            LastTranslationTimeText = _diagnostics.LastTranslationTime?.ToString("HH:mm:ss.fff") ?? "-";
            RaiseDiagnosticsProperties();
        }, null);

    private void ApplyDiagnostics(TranslationResult result)
    {
        LastSourceText = result.SourceText;
        LastTranslatedText = result.Success ? result.TranslatedText : "-";
        LastRawResponse = string.IsNullOrWhiteSpace(result.RawResponse) ? "-" : result.RawResponse;
        LastParsedText = string.IsNullOrWhiteSpace(result.ParsedTranslation) ? "-" : result.ParsedTranslation;
        LastPostProcessedText = string.IsNullOrWhiteSpace(result.PostProcessedTranslation) ? "-" : result.PostProcessedTranslation;
        LastDurationText = $"{result.DurationMs} ms";
        LastFromCacheText = result.FromCache ? "true" : "false";
        LastErrorText = result.Success ? string.Empty : result.ErrorMessage ?? string.Empty;
        LastProviderName = result.ProviderName;
        LastTranslationTimeText = result.CreatedAt.ToLocalTime().ToString("HH:mm:ss.fff");
    }

    private void ApplyResultToPipelineDiagnostics(TranslationResult result)
    {
        _diagnostics.LastTranslationSourceText = result.SourceText;
        _diagnostics.LastTranslationFinishedAt = DateTimeOffset.Now;
        _diagnostics.LastTranslationDurationMs = result.DurationMs;
        _diagnostics.LastTranslationRawResponse = result.RawResponse;
        _diagnostics.LastTranslationParsedText = string.IsNullOrWhiteSpace(result.ParsedTranslation)
            ? result.TranslatedText : result.ParsedTranslation;
        _diagnostics.LastTranslationPostProcessedText = string.IsNullOrWhiteSpace(result.PostProcessedTranslation)
            ? result.TranslatedText : result.PostProcessedTranslation;
        _diagnostics.LastTranslationProviderName = result.ProviderName;
        _diagnostics.LastTranslationTime = DateTimeOffset.Now;
        _diagnostics.LastTranslationError = result.ErrorMessage ?? string.Empty;
        _diagnostics.LastTranslationWasFromCache = result.FromCache;
        _diagnostics.LastTranslationQueueStatus = result.Success ? "completed" : "failed";
        _diagnosticsStore.Save();
        _diagnostics.NotifyChanged();
        RaiseDiagnosticsProperties();
    }

    private static async Task SaveTranslationResultAsync(TranslationResult result)
    {
        var debugDirectory = Path.Combine(AppContext.BaseDirectory, "debug");
        Directory.CreateDirectory(debugDirectory);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(
            Path.Combine(debugDirectory, "last_translation_result.json"),
            json,
            Encoding.UTF8);
    }

    private string BuildPipelineDiagnosticsText() => string.Join(Environment.NewLine,
    [
        $"Frame: {_diagnostics.LastFrameNumber}",
        $"Capture/Crop: {_diagnostics.LastCaptureSucceeded}/{_diagnostics.LastCropSucceeded}",
        $"OCR duration/empty/error: {_diagnostics.LastOcrDurationMs} ms / {_diagnostics.LastOcrWasEmpty} / {_diagnostics.LastOcrError}",
        $"OCR raw: {_diagnostics.LastOcrRawText}",
        $"OCR cleaned: {_diagnostics.LastOcrCleanedText}",
        $"Queue status: {_diagnostics.LastTranslationQueueStatus}",
        $"Translation enabled: {_settings.EnableTranslation}",
        $"Selected provider: {_settings.ProviderType}",
        $"Actual provider used: {ActualProviderUsedText}",
        $"Fake debug override: {_settings.UseFakeTranslationProviderForDebug}",
        $"Fallback used/reason: {_diagnostics.LastTranslationWasFallbackUsed} / {LastFallbackReasonText}",
        $"Translation source: {_diagnostics.LastTranslationSourceText}",
        $"Translation raw: {_diagnostics.LastTranslationRawResponse}",
        $"Translation parsed: {_diagnostics.LastTranslationParsedText}",
        $"Translation post-processed: {_diagnostics.LastTranslationPostProcessedText}",
        $"Translation error: {_diagnostics.LastTranslationError}",
        $"Stale dropped/reason: {_diagnostics.LastTranslationWasDroppedAsStale} / {_diagnostics.LastTranslationDropReason}",
        $"Ollama: {_diagnostics.OllamaBaseUrl} / {_diagnostics.OllamaModel} / reachable={_diagnostics.OllamaReachable} / HTTP={_diagnostics.LastOllamaStatusCode}",
        $"Request preview: {_diagnostics.LastOllamaRequestBodyPreview}",
        $"Response preview: {_diagnostics.LastOllamaResponsePreview}",
        $"Translation server: {TranslationServerStatusText} / pid={TranslationServerProcessIdText} / " +
        $"lastCheck={TranslationServerLastHealthCheckText} / lastError={TranslationServerLastHealthErrorText}",
        $"Current subtitle source: {_diagnostics.CurrentSubtitleSourceText}",
        $"Current normalized source key: {_diagnostics.CurrentNormalizedSourceKey}",
        $"Overlay display text: {_diagnostics.CurrentOverlayDisplayText}",
        $"Overlay display language/state: {_diagnostics.CurrentOverlayDisplayLanguage} / {_diagnostics.CurrentOverlayDisplayState}",
        $"Overlay translation text: {_diagnostics.CurrentOverlayTranslationText}",
        $"Overlay sourceIgnored/late/cacheHit: {_diagnostics.LastOverlaySourceIgnoredBecauseTranslationExists} / {_diagnostics.LastOverlayTranslationWasLate} / {_diagnostics.LastOverlayCacheHit}",
        $"Selected subtitle lines: {_diagnostics.CurrentSubtitleSelectedLines}",
        $"Rejected HUD lines: {_diagnostics.RejectedHudLines}",
        $"Subtitle queue: count={_diagnostics.TranslationQueueCount}, items=[{_diagnostics.TranslationQueueItems}]",
        $"Late/expired/cacheSaved: {_diagnostics.TranslationLateCompletedCount}/" +
        $"{_diagnostics.TranslationExpiredCount}/{_diagnostics.TranslationCacheSavedCount}",
        $"Last queue drop reason: {_diagnostics.LastQueueDropReason}",
        $"Last subtitle age at completion: {_diagnostics.LastSubtitleAgeMsWhenTranslationCompleted} ms",
        $"Last overlay replace reason: {_diagnostics.LastOverlayReplaceReason}",
    ]);

    private void RaiseDiagnosticsProperties()
    {
        RaisePropertyChanged(nameof(TranslationQueueStatus));
        RaisePropertyChanged(nameof(TranslationCountersText));
        RaisePropertyChanged(nameof(ActualProviderUsedText));
        RaisePropertyChanged(nameof(LastFallbackReasonText));
        RaisePropertyChanged(nameof(FallbackProviderText));
        RaisePropertyChanged(nameof(ProviderStatusText));
        RaisePropertyChanged(nameof(TranslationServerStatusText));
        RaisePropertyChanged(nameof(TranslationServerLastHealthCheckText));
        RaisePropertyChanged(nameof(TranslationServerLastHealthErrorText));
        RaisePropertyChanged(nameof(TranslationServerProcessIdText));
        RaisePropertyChanged(nameof(PipelineDiagnosticsText));
    }

    private void RefreshCacheCount() => CacheCountText = $"{_cache.Count} entries";
}
