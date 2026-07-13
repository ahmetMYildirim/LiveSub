using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PsGameTranslator.App.Services;
using PsGameTranslator.App.ViewModels;
using PsGameTranslator.App.ViewModels.Shell;
using PsGameTranslator.App.ViewModels.User;
using PsGameTranslator.App.Views.Shell;
using PsGameTranslator.Capture;
using PsGameTranslator.Infrastructure.Configuration;
using PsGameTranslator.Infrastructure.Logging;
using PsGameTranslator.Infrastructure.Monitoring;
using PsGameTranslator.Infrastructure.Region;
using PsGameTranslator.Ocr;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Ocr;
using PsGameTranslator.Core.Subtitles;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Subtitles;
using PsGameTranslator.Infrastructure.Translation;
using PsGameTranslator.Overlay;
using Serilog;

namespace PsGameTranslator.App;

public partial class App : Application
{
    private readonly IHost _host;

    public App()
    {
        var configuration = JsonConfigurationHelper.Build(AppContext.BaseDirectory);

        Log.Logger = LoggingSetup.CreateLogger(configuration);

        _host = Host.CreateDefaultBuilder()
            .UseSerilog(Log.Logger, dispose: false)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(configuration);

                services.Configure<AppSettings>(
                    configuration.GetSection(AppSettings.SectionName));

                // Capture
                services.AddSingleton<IWindowCaptureService, WindowCaptureService>();
                services.AddSingleton<IImageCropService, ImageCropService>();

                // Region persistence
                services.AddSingleton<IRegionPersistenceService, RegionPersistenceService>();

                // OCR
                // SettingsViewModel is the live IOcrSettings source so PaddleOcrService
                // always uses whatever path the user has typed in the Settings tab.
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<IOcrSettings>(
                    sp => sp.GetRequiredService<SettingsViewModel>());
                services.AddSingleton<IOcrProcessingSettings>(
                    sp => sp.GetRequiredService<SettingsViewModel>());
                services.AddSingleton<OcrTextCleaner>();
                services.AddSingleton<OcrResultCache>();
                services.AddSingleton<IOcrServerService, OcrServerService>();
                services.AddSingleton<PaddleOcrService>();                // fallback (subprocess)
                services.AddSingleton<IOcrService>(
                    sp => sp.GetRequiredService<PaddleOcrService>());
                services.AddSingleton<HttpOcrService>();                  // persistent-server client
                var ocrEngineSettings = new OcrEngineSettings();
                configuration.GetSection("OcrEngine").Bind(ocrEngineSettings);
                ocrEngineSettings.ApplyProfileDefaults();
                services.AddSingleton(ocrEngineSettings);
                services.AddSingleton<OcrResultScorer>();
                // Server-backed engines (one multi-engine Python server, one port):
                services.AddSingleton<IOcrProvider>(sp => new HttpEngineOcrProvider(
                    sp.GetRequiredService<HttpOcrService>(), sp.GetRequiredService<IOcrServerService>(),
                    "PaddleOCR Server", OcrProviderType.PaddleOCR, "paddle"));
                services.AddSingleton<IOcrProvider>(sp => new HttpEngineOcrProvider(
                    sp.GetRequiredService<HttpOcrService>(), sp.GetRequiredService<IOcrServerService>(),
                    "RapidOCR Server", OcrProviderType.RapidOCR, "rapid"));
                services.AddSingleton<IOcrProvider>(sp => new HttpEngineOcrProvider(
                    sp.GetRequiredService<HttpOcrService>(), sp.GetRequiredService<IOcrServerService>(),
                    "EasyOCR Server", OcrProviderType.EasyOCR, "easy"));
                // Subprocess fallback transport for PaddleOCR (works without the server):
                services.AddSingleton<IOcrProvider, PaddleOcrProvider>();
                // Native + test providers:
                services.AddSingleton<IOcrProvider, WindowsOcrProvider>();
                services.AddSingleton<IOcrProvider>(_ => new MockOcrProvider());
                services.AddSingleton<IOcrProvider>(_ => new UnavailableOcrProvider(
                    "OneOCR", OcrProviderType.OneOCR,
                    "OneOCR is not supported yet (undocumented Windows internal API)."));
                services.AddSingleton<OcrProviderFactory>();
                services.AddSingleton<PythonEnvironmentService>();
                services.AddSingleton<OcrEngineInstallService>();
                services.AddSingleton<OcrEngineManager>();
                services.AddSingleton<OcrWorker>();
                services.AddSingleton<FastFrameDifferenceService>();

                // Subtitle formatting
                var subtitleFormatterSettings = new SubtitleFormatterSettings();
                configuration.GetSection("SubtitleFormatter").Bind(subtitleFormatterSettings);
                services.AddSingleton(subtitleFormatterSettings);
                services.AddSingleton<SpeakerNameDetector>();
                services.AddSingleton<ISubtitleFormatter, SubtitleFormatter>();

                // Subtitle line filtering (dialogue vs tutorial/HUD prompts)
                var subtitleFilterSettings = new SubtitleFilterSettings();
                configuration.GetSection("SubtitleFilter").Bind(subtitleFilterSettings);
                services.AddSingleton(subtitleFilterSettings);
                services.AddSingleton<GameProfileRepository>();
                services.AddSingleton<SubtitleLineClassifier>();
                services.AddSingleton<SubtitleDisplayStateManager>();

                // System resource monitoring (status bar CPU/RAM)
                services.AddSingleton<SystemResourceMonitorService>();

                // Theme
                services.AddSingleton<ThemeService>();
                services.AddSingleton<LocalizationService>();
                services.AddSingleton<GameCoverService>();

                // Overlay
                services.AddSingleton<IOverlayService, OverlayService>();
                services.AddSingleton<IOverlaySettingsService, OverlaySettingsService>();
                services.AddSingleton<IMonitorService, MonitorService>();
                services.AddSingleton<OverlayPositionValidator>();

                // Translation
                var translationSettings = new TranslationSettings();
                configuration.GetSection("Translation").Bind(translationSettings);
                services.AddSingleton(translationSettings);
                services.AddSingleton<UserSettingsPersistenceService>();
                services.AddSingleton<SystemRequirementsService>();
                services.AddSingleton<PipelineDiagnostics>();
                services.AddSingleton<PipelineDiagnosticsStore>();
                services.AddSingleton<TranslationCache>();
                services.AddSingleton<UserGlossaryRepository>();
                services.AddSingleton<GlossaryDictionaryManager>();
                services.AddSingleton<OllamaVisionGameIdentifier>();
                services.AddSingleton<ActiveGameCoordinator>();
                services.AddSingleton<TranslationPostProcessor>();
                services.AddSingleton<TranslationDatasetCollector>();
                // Translation Learning System
                services.AddSingleton<ITranslationRecordRepository, SqliteTranslationRecordRepository>();
                services.AddSingleton<ITranslationMemoryService, SqliteTranslationMemoryService>();
                services.AddSingleton<IFineTuneDatasetExporter, FineTuneDatasetExporter>();
                services.AddSingleton<ITranslationLearningService, TranslationLearningService>();
                services.AddSingleton<OllamaRefinementProvider>();
                services.AddSingleton<RefinementOrchestrator>();
                // Concrete providers. ITranslationProvider is intentionally NOT
                // open-registered: all runtime selection goes through
                // TranslationProviderSelector so the fake provider can never
                // silently become the active one.
                services.AddSingleton<FakeTranslationProvider>();
                services.AddSingleton<MachineTranslationProvider>();
                services.AddSingleton<MachineTranslationServerManager>();
                services.AddSingleton<OllamaTranslationService>();
                // LM Studio is a real local provider (OpenAI-compatible API).
                services.AddSingleton<ITranslationProvider, LmStudioTranslationProvider>();
                // Google Translate: free unofficial endpoint, no API key needed.
                services.AddSingleton<ITranslationProvider, GoogleTranslateProvider>();

                // DeepL / Gemini / Groq: real providers, inert until the user pastes an API key.
                services.AddSingleton<ITranslationProvider, DeepLTranslateProvider>();
                services.AddSingleton<ITranslationProvider, GeminiTranslateProvider>();
                services.AddSingleton<ITranslationProvider, GroqTranslateProvider>();

                // Cloud providers remain explicit placeholders until API keys/implementations exist.
                services.AddSingleton<ITranslationProvider>(_ => new UnavailableTranslationProvider(
                    "ChatGPT", TranslationProviderType.ChatGPT,
                    "ChatGPT is not configured. An API key is required.",
                    TranslationProviderStatus.MissingApiKey));
                services.AddSingleton<ITranslationProvider>(_ => new UnavailableTranslationProvider(
                    "Mistral", TranslationProviderType.Mistral,
                    "Mistral is not configured. An API key (or local endpoint) is required.",
                    TranslationProviderStatus.MissingApiKey));
                services.AddSingleton<ITranslationProvider>(
                    sp => sp.GetRequiredService<FakeTranslationProvider>());
                services.AddSingleton<ModelInstallService>();
                services.AddSingleton<TranslationProviderSelector>();
                services.AddSingleton<ITranslationService>(
                    sp => sp.GetRequiredService<OllamaTranslationService>());
                services.AddSingleton<TranslationQueue>();
                services.AddSingleton<SubtitleTranslationQueue>();
                services.AddSingleton<RuntimePipelineHealthService>();

                // Ordered subtitle pipeline (Turkish-only live mode)
                services.AddSingleton<SubtitleCandidateValidator>();
                services.AddSingleton<OrderedSubtitleCaptureQueue>();
                services.AddSingleton<TranslationPlaybackQueue>();
                services.AddSingleton<OrderedSubtitlePipeline>();

                // ViewModels
                services.AddSingleton<GlossaryViewModel>();
                services.AddSingleton<LearningViewModel>();
                services.AddSingleton<CaptureViewModel>();
                services.AddSingleton<OverlayMonitorCoordinator>();
                services.AddSingleton<RegionViewModel>();
                services.AddSingleton<OcrViewModel>();
                services.AddSingleton<MonitoringViewModel>();
                services.AddSingleton<OcrServerViewModel>();
                services.AddSingleton<TranslationViewModel>();
                services.AddSingleton<OverlayViewModel>();
                services.AddSingleton<ModelManagerViewModel>();
                services.AddSingleton<HomeViewModel>();
                services.AddSingleton<CapturePageViewModel>();
                services.AddSingleton<TranslationPageViewModel>();
                services.AddSingleton<SettingsPageViewModel>();
                services.AddSingleton<ModelManagerPageViewModel>();
                services.AddSingleton<GlossaryPageViewModel>();
                services.AddSingleton<ShortcutsPageViewModel>();
                services.AddSingleton<TrainingAccessService>();
                services.AddSingleton<TrainingService>();
                services.AddSingleton<TrainingViewModel>();
                services.AddSingleton<AppShellViewModel>();
                services.AddSingleton<MainViewModel>();
                services.AddTransient<SetupWizardViewModel>();
                services.AddTransient<Views.SetupWizardWindow>();

                // UI
                services.AddSingleton<MainWindow>();
                services.AddSingleton<AppShellWindow>();
            })
            .Build();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host.StartAsync().GetAwaiter().GetResult();
        Log.Information("PsGameTranslator starting");

        // Must run before any Window is constructed — StaticResource lookups
        // in XAML resolve once, at parse time.
        _host.Services.GetRequiredService<ThemeService>().ApplyStartupTheme();
        _host.Services.GetRequiredService<LocalizationService>().ApplyStartupLanguage();

        var mainWindow = _host.Services.GetRequiredService<AppShellWindow>();
        mainWindow.Show();

        // First run: guide the user through OCR engine, translation model and
        // overlay setup so nothing has to be started from a terminal.
        if (!SetupWizardViewModel.IsSetupCompleted)
        {
            var wizard = _host.Services.GetRequiredService<Views.SetupWizardWindow>();
            wizard.Owner = mainWindow;
            wizard.Show();
        }

        // Initialize translation learning database.
        var learningRepo = _host.Services.GetRequiredService<ITranslationRecordRepository>();
        _ = Task.Run(async () =>
        {
            try { await learningRepo.InitializeAsync(); }
            catch (Exception exception) { Log.Warning(exception, "learning_db_init_failed"); }
        });

        // Load any built-in dictionary files that exist on disk.
        var dictManager = _host.Services.GetRequiredService<GlossaryDictionaryManager>();
        _ = Task.Run(async () =>
        {
            try { await dictManager.ReloadDefaultPathsAsync(); }
            catch (Exception exception) { Log.Warning(exception, "glossary_dict_reload_failed"); }
        });

        // Fire-and-forget: never block app startup on the translation server.
        // Internal errors are caught and reflected in MachineTranslationServerManager.State.
        var serverManager = _host.Services.GetRequiredService<MachineTranslationServerManager>();
        _ = Task.Run(async () =>
        {
            try { await serverManager.EnsureRunningIfEnabledAsync(); }
            catch (Exception exception) { Log.Warning(exception, "translation_server_startup_check_failed"); }
        });

        // Same for the OCR server: honor AutoStartOcrServer when the selected
        // provider is served by the local Python OCR server.
        var ocrEngineSettings = _host.Services.GetRequiredService<OcrEngineSettings>();
        var ocrServer = _host.Services.GetRequiredService<IOcrServerService>();
        if (ocrEngineSettings.AutoStartOcrServer &&
            OcrServerService.IsServerBackedProvider(ocrEngineSettings.PreferredProvider))
        {
            _ = Task.Run(async () =>
            {
                var (success, message) = await ocrServer.EnsureRunningAsync();
                if (!success)
                    Log.Warning("ocr_server_startup_failed: {Message}", message);
            });
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("PsGameTranslator stopping");
        _host.Services.GetRequiredService<MonitoringViewModel>().StopIfRunning();
        _host.Services.GetRequiredService<IOcrServerService>().StopAsync().GetAwaiter().GetResult();
        _host.Services.GetRequiredService<MachineTranslationServerManager>().StopServerAsync().GetAwaiter().GetResult();
        _host.Services.GetRequiredService<IOverlayService>().Close();
        _host.Services.GetRequiredService<OrderedSubtitlePipeline>().Dispose();
        _host.Services.GetRequiredService<TranslationPlaybackQueue>().Dispose();
        _host.Services.GetRequiredService<OverlayMonitorCoordinator>().Dispose();
        _host.Services.GetRequiredService<TranslationCache>().Flush();
        PsGameTranslator.Core.Models.DebugFileWriter.FlushNow();
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
