using System.Text;
using System.Text.Json;
using System.IO;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Ocr;
using PsGameTranslator.Core.Subtitles;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Subtitles;
using PsGameTranslator.Infrastructure.Translation;
using PsGameTranslator.Ocr;
using PsGameTranslator.Overlay;

namespace PsGameTranslator.App.Services;

public sealed class RuntimePipelineHealthService
{
    private static readonly string DebugDirectory = Path.Combine(AppContext.BaseDirectory, "debug");

    private readonly OcrEngineManager _ocrEngineManager;
    private readonly OcrEngineSettings _ocrSettings;
    private readonly IOcrServerService _ocrServer;
    private readonly TranslationSettings _translationSettings;
    private readonly TranslationProviderSelector _translationProviderSelector;
    private readonly MachineTranslationServerManager _translationServer;
    private readonly PipelineDiagnostics _diagnostics;
    private readonly IOverlayService _overlayService;
    private readonly ISubtitleFormatter _subtitleFormatter;
    private readonly SubtitleCandidateValidator _candidateValidator;
    private readonly TranslationPostProcessor _postProcessor;
    private readonly TranslationPlaybackQueue _playbackQueue;
    private readonly ILogger<RuntimePipelineHealthService> _logger;

    public RuntimePipelineHealthService(
        OcrEngineManager ocrEngineManager,
        OcrEngineSettings ocrSettings,
        IOcrServerService ocrServer,
        TranslationSettings translationSettings,
        TranslationProviderSelector translationProviderSelector,
        MachineTranslationServerManager translationServer,
        PipelineDiagnostics diagnostics,
        IOverlayService overlayService,
        ISubtitleFormatter subtitleFormatter,
        SubtitleCandidateValidator candidateValidator,
        TranslationPostProcessor postProcessor,
        TranslationPlaybackQueue playbackQueue,
        ILogger<RuntimePipelineHealthService> logger)
    {
        _ocrEngineManager = ocrEngineManager;
        _ocrSettings = ocrSettings;
        _ocrServer = ocrServer;
        _translationSettings = translationSettings;
        _translationProviderSelector = translationProviderSelector;
        _translationServer = translationServer;
        _diagnostics = diagnostics;
        _overlayService = overlayService;
        _subtitleFormatter = subtitleFormatter;
        _candidateValidator = candidateValidator;
        _postProcessor = postProcessor;
        _playbackQueue = playbackQueue;
        _logger = logger;
    }

    public async Task<RuntimePipelineHealthReport> RunHealthCheckAsync(
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DebugDirectory);

        var ocrHealth = await _ocrEngineManager.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        var selectedOcrHealth = ocrHealth.FirstOrDefault(health =>
            health.ProviderType == _ocrSettings.PreferredProvider);
        var ocrServerHealth = await _ocrServer.TestConnectionAsync(cancellationToken).ConfigureAwait(false);

        var translationProvider = _translationProviderSelector.SelectProvider();
        var translationProviderHealth = translationProvider is null
            ? new TranslationProviderHealth
            {
                ProviderName = "none",
                ProviderType = TranslationProviderType.None,
                IsAvailable = false,
                Message = "No translation provider selected.",
            }
            : await translationProvider.CheckHealthAsync(cancellationToken).ConfigureAwait(false);

        var translationServerOk = true;
        var translationServerMessage = "Not required for selected provider.";
        if (_translationSettings.ProviderType is TranslationProviderType.OpusMT or TranslationProviderType.HybridMachineThenOllama)
        {
            translationServerOk = await _translationServer.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            translationServerMessage = translationServerOk
                ? "OPUS-MT server is healthy."
                : _translationServer.LastHealthError;
        }

        var overlaySettings = _overlayService.CurrentSettings;
        var manualRegionOk = ManualReplacementRegionHelper.IsConfigured(overlaySettings.Replacement);
        var replacementOverlayOk = overlaySettings.DisplayMode == SubtitleDisplayMode.SubtitleReplacementOverlay &&
            _translationSettings.TurkishOnlyMode &&
            overlaySettings.Replacement.UseManualReplacementRegion &&
            _translationSettings.ShowMaskWhileTranslationPending;

        var turkishDisplayed = !string.IsNullOrWhiteSpace(_diagnostics.LastDisplayedTurkishText) ||
            !string.IsNullOrWhiteSpace(_overlayService.LastReplacementSnapshot?.Text);

        var report = new RuntimePipelineHealthReport
        {
            Timestamp = DateTimeOffset.Now,
            OcrProviderOk = selectedOcrHealth?.IsAvailable == true,
            OcrServerOk = ocrServerHealth.Success || _ocrSettings.PreferredProvider != OcrProviderType.PaddleOCR,
            OcrResultOk = string.IsNullOrWhiteSpace(_diagnostics.LastOcrError) &&
                !string.IsNullOrWhiteSpace(_diagnostics.LastOcrRawText),
            TranslationProviderOk = translationProviderHealth.IsAvailable,
            TranslationServerOk = translationServerOk,
            TranslationResultOk = string.IsNullOrWhiteSpace(_diagnostics.LastTranslationError) &&
                !string.IsNullOrWhiteSpace(_diagnostics.LastTranslationPostProcessedText),
            ReplacementOverlayOk = replacementOverlayOk,
            ManualReplacementRegionOk = manualRegionOk,
            TurkishDisplayedOk = turkishDisplayed,
            SelectedOcrProvider = _ocrSettings.PreferredProvider.ToString(),
            ActualOcrProvider = string.IsNullOrWhiteSpace(_ocrEngineManager.LastProviderUsed)
                ? "-"
                : _ocrEngineManager.LastProviderUsed,
            OcrServerUrl = _ocrServer.ServerBaseUrl,
            OcrServerState = ocrServerHealth.Success ? "Running" : "Unreachable",
            OcrServerMessage = ocrServerHealth.Message,
            LastOcrText = _diagnostics.LastOcrRawText,
            LastOcrDurationMs = _diagnostics.LastOcrDurationMs,
            LastOcrError = _diagnostics.LastOcrError,
            LastOcrAcceptedOrRejected = string.IsNullOrWhiteSpace(_diagnostics.LastRejectedReason)
                ? "accepted-or-not-run"
                : $"rejected:{_diagnostics.LastRejectedReason}",
            SelectedTranslationProvider = _translationSettings.ProviderType.ToString(),
            ActualTranslationProvider = translationProvider?.ProviderName ?? "none",
            TranslationServerUrl = _translationServer.ServerBaseUrl,
            TranslationServerState = translationServerOk ? "Running" : "Unreachable",
            TranslationServerMessage = translationServerMessage,
            LastTranslationSourceText = _diagnostics.LastTranslationSourceText,
            LastTranslationResult = _diagnostics.LastTranslationPostProcessedText,
            LastTranslationDurationMs = _diagnostics.LastTranslationDurationMs,
            TranslationCacheHit = _diagnostics.CacheHitCount > 0 || _diagnostics.LastTranslationWasFromCache,
            TranslationMemoryHit = _diagnostics.MemoryHitCount > 0,
            TranslationInFlightHit = _diagnostics.InFlightHitCount > 0,
            FallbackReason = _diagnostics.LastTranslationFallbackReason,
            SelectedDisplayMode = overlaySettings.DisplayMode.ToString(),
            ActualDisplayMode = _overlayService.LastReplacementSnapshot is not null
                ? "SubtitleReplacementOverlay"
                : overlaySettings.DisplayMode.ToString(),
            TurkishOnlyMode = _translationSettings.TurkishOnlyMode,
            UseManualReplacementRegion = overlaySettings.Replacement.UseManualReplacementRegion,
            ManualReplacementRegion = new
            {
                overlaySettings.Replacement.ManualReplacementRegionX,
                overlaySettings.Replacement.ManualReplacementRegionY,
                overlaySettings.Replacement.ManualReplacementRegionWidth,
                overlaySettings.Replacement.ManualReplacementRegionHeight,
            },
            LastOverlayUpdateText = _overlayService.LastReplacementSnapshot?.Text ?? _diagnostics.CurrentOverlayDisplayText,
            LastOverlayUpdateSource = _diagnostics.LastOverlayUpdateSource,
            OverlayVisible = _overlayService.IsOpen,
            EnglishBlocked = _diagnostics.WasEnglishBlockedInReplacementMode,
            LastDisplayFailureReason = BuildDisplayFailureReason(replacementOverlayOk, manualRegionOk, _overlayService.IsOpen),
        };

        await SaveJsonAsync("runtime_pipeline_health.json", report, cancellationToken).ConfigureAwait(false);
        await SaveJsonAsync("last_ocr_provider_selection.json", new
        {
            Timestamp = DateTimeOffset.Now,
            SelectedProviderFromUi = _ocrSettings.PreferredProvider.ToString(),
            ActualProviderUsed = string.IsNullOrWhiteSpace(_ocrEngineManager.LastProviderUsed)
                ? "none"
                : _ocrEngineManager.LastProviderUsed,
            FallbackEnabled = _ocrSettings.EnableOcrProviderFallback,
            FallbackUsed = _ocrEngineManager.LastFallbackUsed,
            FallbackReason = _ocrEngineManager.LastFallbackReason,
            ProviderAvailability = _ocrEngineManager.Providers.Select(provider => new
            {
                provider.Name,
                provider.ProviderType,
                provider.IsAvailable,
            }),
            ProviderHealthResult = selectedOcrHealth,
            ServerStatus = report.OcrServerState,
        }, cancellationToken).ConfigureAwait(false);
        await SaveJsonAsync("last_ocr_provider_health.json", new
        {
            Timestamp = DateTimeOffset.Now,
            SelectedProvider = _ocrSettings.PreferredProvider.ToString(),
            ActualProvider = report.ActualOcrProvider,
            FallbackEnabled = _ocrSettings.EnableOcrProviderFallback,
            Providers = ocrHealth,
        }, cancellationToken).ConfigureAwait(false);
        await SaveJsonAsync("last_ocr_server_state.json", new
        {
            Timestamp = DateTimeOffset.Now,
            SelectedProvider = _ocrSettings.PreferredProvider,
            ServerUrl = _ocrServer.ServerBaseUrl,
            State = report.OcrServerState,
            IsRunning = _ocrServer.IsRunning,
            HealthMessage = report.OcrServerMessage,
        }, cancellationToken).ConfigureAwait(false);
        await SaveJsonAsync("last_manual_replacement_region_state.json", new
        {
            Timestamp = DateTimeOffset.Now,
            IsValid = manualRegionOk,
            Reason = manualRegionOk ? string.Empty : "Replacement Mask Region is not selected.",
            Required = "width > 100 and height > 30",
            overlaySettings.Replacement.UseManualReplacementRegion,
            overlaySettings.Replacement.ManualReplacementRegionX,
            overlaySettings.Replacement.ManualReplacementRegionY,
            overlaySettings.Replacement.ManualReplacementRegionWidth,
            overlaySettings.Replacement.ManualReplacementRegionHeight,
            overlaySettings.X,
            overlaySettings.Y,
            overlaySettings.Width,
            overlaySettings.Height,
        }, cancellationToken).ConfigureAwait(false);
        await SaveJsonAsync("last_translation_server_state.json", new
        {
            Timestamp = DateTimeOffset.Now,
            SelectedProvider = _translationSettings.ProviderType,
            ServerUrl = _translationServer.ServerBaseUrl,
            State = report.TranslationServerState,
            ManagerState = _translationServer.State.ToString(),
            ProcessId = _translationServer.ProcessId,
            LastHealthError = _translationServer.LastHealthError,
            LastStartError = _translationServer.LastStartError,
        }, cancellationToken).ConfigureAwait(false);
        await SaveJsonAsync("last_queue_state.json", BuildQueueState(), cancellationToken).ConfigureAwait(false);
        await SaveJsonAsync("last_overlay_visibility_state.json", BuildOverlayVisibilityState(), cancellationToken).ConfigureAwait(false);
        return report;
    }

    public async Task<EndToEndPipelineTestReport> RunEndToEndPipelineTestAsync(
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DebugDirectory);
        var traceId = Guid.NewGuid().ToString("N");
        const string rawInput = "Haymish\nI thank you for your help.";

        var trace = new Dictionary<string, object?>
        {
            ["TraceId"] = traceId,
            ["RawOcrText"] = rawInput,
            ["OcrCaptured"] = true,
        };

        var formatted = await _subtitleFormatter
            .FormatAsync(rawInput, 0.95, cancellationToken)
            .ConfigureAwait(false);
        var dialogueText = formatted.MainText.Trim();
        var speakerName = formatted.SpeakerName.Trim();
        var normalizedKey = NormalizeKey(dialogueText);
        trace["SpeakerDetected"] = !string.IsNullOrWhiteSpace(speakerName);
        trace["SpeakerName"] = speakerName;
        trace["DialogueText"] = dialogueText;
        trace["NormalizedDialogueKey"] = normalizedKey;

        var validation = _candidateValidator.IsValidForReplacementMode(dialogueText);
        trace["CandidateAccepted"] = validation.IsValid;
        trace["RejectionReason"] = validation.IsValid ? string.Empty : validation.Reason;
        if (!validation.IsValid)
            return await FailEndToEndAsync(traceId, rawInput, speakerName, dialogueText, "candidate_validation", validation.Reason, trace, cancellationToken);

        var settings = ForceReplacementSettings(out var backup);
        try
        {
        if (!ManualReplacementRegionHelper.IsConfigured(settings.Replacement))
        {
            return await FailEndToEndAsync(
                traceId, rawInput, speakerName, dialogueText,
                "manual_replacement_region",
                "Replacement Mask Region is not selected.",
                trace,
                cancellationToken);
        }

        EnsureOverlayOpen(settings);
        var context = BuildManualReplacementContext(settings);
        _playbackQueue.NotifyActivity(normalizedKey, dialogueText, context);
        trace["QueuedForTranslation"] = true;

        var provider = _translationProviderSelector.SelectProvider();
        if (provider is null)
            return await FailEndToEndAsync(traceId, rawInput, speakerName, dialogueText, "translation_provider", "No provider selected.", trace, cancellationToken);

        trace["ProviderCalled"] = true;
        var result = await provider.TranslateAsync(new TranslationRequest
        {
            SourceText = dialogueText,
            SourceLanguage = _translationSettings.SourceLanguage,
            TargetLanguage = _translationSettings.TargetLanguage,
            SpeakerName = speakerName,
            GameProfileName = _translationSettings.GameProfile,
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Success && string.IsNullOrWhiteSpace(result.TranslatedText))
        {
            return await FailEndToEndAsync(
                traceId, rawInput, speakerName, dialogueText,
                "translation_provider",
                result.ErrorMessage ?? "Translation failed.",
                trace,
                cancellationToken,
                provider.ProviderName,
                result.RawResponse,
                string.Empty,
                string.Empty);
        }

        var postProcessed = _postProcessor.Process(dialogueText, result.TranslatedText);
        trace["TranslationCompleted"] = true;
        trace["PostprocessorApplied"] = true;

        var displayItem = new TranslatedSubtitleDisplayItem
        {
            SourceText = dialogueText,
            TranslatedText = postProcessed,
            NormalizedSourceKey = normalizedKey,
            SpeakerName = speakerName,
            CreatedAt = DateTimeOffset.Now,
            ReadyAt = DateTimeOffset.Now,
            MinDisplayMs = _translationSettings.MinTurkishDisplayMs,
            MaxDisplayMs = _translationSettings.MaxTurkishDisplayMs,
            DisplayDurationMs = EstimateDisplayMs(postProcessed),
            ReplacementContext = context.Clone(),
            Source = "END_TO_END_PIPELINE_TEST",
        };
        _playbackQueue.Enqueue(displayItem);
        trace["PlaybackQueued"] = true;

        var displayed = await WaitForOverlayTextAsync(postProcessed, TimeSpan.FromMilliseconds(1200), cancellationToken)
            .ConfigureAwait(false);
        var snapshot = _overlayService.LastReplacementSnapshot;
        trace["OverlayUpdated"] = displayed;
        trace["DisplayedText"] = snapshot?.Text ?? string.Empty;

        var report = new EndToEndPipelineTestReport
        {
            Timestamp = DateTimeOffset.Now,
            TraceId = traceId,
            RawInput = rawInput,
            DetectedSpeaker = speakerName,
            DialogueText = dialogueText,
            ProviderUsed = provider.ProviderName,
            RawProviderTranslation = result.TranslatedText,
            PostProcessedTranslation = postProcessed,
            FinalDisplayText = snapshot?.Text ?? string.Empty,
            OverlayUpdateSource = snapshot?.Reason ?? _diagnostics.LastOverlayUpdateSource,
            OverlayRectangle = snapshot?.Context.OverlayRect,
            Success = displayed &&
                !string.IsNullOrWhiteSpace(postProcessed) &&
                !ContainsEnglishSource(snapshot?.Text ?? string.Empty, dialogueText) &&
                !dialogueText.StartsWith(speakerName + " ", StringComparison.OrdinalIgnoreCase),
            FailureStage = displayed ? string.Empty : "display_routing",
            FailureReason = displayed ? string.Empty : "Translation succeeded but overlay did not update.",
            EnglishWasBlocked = !ContainsEnglishSource(snapshot?.Text ?? string.Empty, dialogueText),
            SpeakerExcludedFromTranslation = !dialogueText.StartsWith(speakerName + " ", StringComparison.OrdinalIgnoreCase),
        };

        if (!report.Success)
            _logger.LogError("ERROR_TRANSLATION_NOT_DISPLAYED trace={TraceId} stage={Stage} reason={Reason}", traceId, report.FailureStage, report.FailureReason);

        await SaveJsonAsync("end_to_end_pipeline_test.json", report, cancellationToken).ConfigureAwait(false);
        await SaveJsonAsync("last_pipeline_trace.json", trace, cancellationToken).ConfigureAwait(false);
        await SaveJsonAsync("last_display_routing.json", new
        {
            Timestamp = DateTimeOffset.Now,
            TraceId = traceId,
            TranslationCompleted = true,
            OverlayUpdated = displayed,
            ExpectedText = postProcessed,
            SnapshotText = snapshot?.Text,
            SnapshotReason = snapshot?.Reason,
            SnapshotRect = snapshot?.Context.OverlayRect,
            Failure = report.FailureReason,
        }, cancellationToken).ConfigureAwait(false);

        return report;
        }
        finally
        {
            RestoreForcedSettings(backup);
        }
    }

    public async Task<TranslationProviderOnlyTestReport> RunSelectedTranslationProviderTestAsync(
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DebugDirectory);
        const string sourceText = "Come to think of it, we're all of differing vocations, aren't we?";
        var request = new TranslationRequest
        {
            SourceText = sourceText,
            SourceLanguage = _translationSettings.SourceLanguage,
            TargetLanguage = _translationSettings.TargetLanguage,
            GameProfileName = _translationSettings.GameProfile,
        };
        var execution = await _translationProviderSelector
            .TranslateAsync(request, _translationSettings.AllowFallbackDuringProviderTest, cancellationToken)
            .ConfigureAwait(false);
        var result = execution.Result;

        var postProcessed = result.Success
            ? _postProcessor.Process(sourceText, result.TranslatedText)
            : string.Empty;

        var report = new TranslationProviderOnlyTestReport
        {
            Timestamp = DateTimeOffset.Now,
            SourceText = sourceText,
            ProviderUsed = execution.Selection.ActualProviderName,
            SelectedProvider = execution.Selection.SelectedProviderType.ToString(),
            FallbackUsed = execution.Selection.FallbackUsed,
            FallbackReason = execution.Selection.FallbackReason,
            RawTranslation = result.TranslatedText,
            PostProcessedTranslation = postProcessed,
            DurationMs = result.DurationMs,
            Success = result.Success && !string.IsNullOrWhiteSpace(postProcessed),
            ErrorMessage = result.ErrorMessage ?? string.Empty,
        };
        await SaveJsonAsync("selected_translation_provider_test.json", report, cancellationToken).ConfigureAwait(false);
        await SaveJsonAsync("test_selected_translation_provider.json", report, cancellationToken).ConfigureAwait(false);
        return report;
    }

    public async Task<TranslationProviderOnlyTestReport> RunProviderChainTestAsync(
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DebugDirectory);
        const string sourceText = "Come to think of it, we're all of differing vocations, aren't we?";
        var request = new TranslationRequest
        {
            SourceText = sourceText,
            SourceLanguage = _translationSettings.SourceLanguage,
            TargetLanguage = _translationSettings.TargetLanguage,
            GameProfileName = _translationSettings.GameProfile,
        };

        var previousMode = _translationSettings.ProviderChainMode;
        _translationSettings.ProviderChainMode = TranslationProviderChainMode.ProviderChain;
        TranslationProviderExecutionResult execution;
        try
        {
            execution = await _translationProviderSelector
                .TranslateAsync(request, allowFallback: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _translationSettings.ProviderChainMode = previousMode;
        }
        var result = execution.Result;
        var postProcessed = result.Success
            ? _postProcessor.Process(sourceText, result.TranslatedText)
            : string.Empty;

        var report = new TranslationProviderOnlyTestReport
        {
            Timestamp = DateTimeOffset.Now,
            SourceText = sourceText,
            SelectedProvider = execution.Selection.SelectedProviderType.ToString(),
            ProviderUsed = execution.Selection.ActualProviderName,
            FallbackUsed = execution.Selection.FallbackUsed,
            FallbackReason = execution.Selection.FallbackReason,
            RawTranslation = result.TranslatedText,
            PostProcessedTranslation = postProcessed,
            DurationMs = result.DurationMs,
            Success = result.Success && !string.IsNullOrWhiteSpace(postProcessed),
            ErrorMessage = result.ErrorMessage ?? string.Empty,
            ChainSteps =
            [
                "Memory miss",
                "Cache miss",
                $"Selected provider status: {execution.Selection.ProviderStatus}",
                $"Fallback provider: {execution.Selection.FallbackProviderName}",
                $"Final provider used: {execution.Selection.ActualProviderName}",
            ],
        };
        await SaveJsonAsync("test_translation_provider_chain.json", report, cancellationToken).ConfigureAwait(false);
        return report;
    }

    public async Task<ReplacementOverlayDisplayTestReport> RunReplacementOverlayDisplayTestAsync(
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DebugDirectory);
        const string turkishText = "Yardımın için teşekkür ederim.";
        var settings = ForceReplacementSettings(out var backup);
        try
        {
        var manualValid = ManualReplacementRegionHelper.IsConfigured(settings.Replacement);
        if (!manualValid)
        {
            var invalid = new ReplacementOverlayDisplayTestReport
            {
                Timestamp = DateTimeOffset.Now,
                InputText = turkishText,
                Success = false,
                ManualRegionValid = false,
                FailureReason = "ManualReplacementRegion is invalid.",
            };
            await SaveJsonAsync("replacement_overlay_display_test.json", invalid, cancellationToken).ConfigureAwait(false);
            return invalid;
        }

        EnsureOverlayOpen(settings);
        var context = BuildManualReplacementContext(settings);
        _overlayService.UpdateReplacementOverlay(new SubtitleReplacementOverlayUpdate
        {
            Text = turkishText,
            Context = context,
            ShowMaskOnly = false,
            Reason = "REPLACEMENT_OVERLAY_DISPLAY_TEST",
        });

        var displayed = await WaitForOverlayTextAsync(turkishText, TimeSpan.FromMilliseconds(500), cancellationToken)
            .ConfigureAwait(false);
        var snapshot = _overlayService.LastReplacementSnapshot;
        var report = new ReplacementOverlayDisplayTestReport
        {
            Timestamp = DateTimeOffset.Now,
            InputText = turkishText,
            DisplayedText = snapshot?.Text ?? string.Empty,
            Success = displayed,
            ManualRegionValid = true,
            OverlayVisible = _overlayService.IsOpen,
            OverlayRectangle = snapshot?.Context.OverlayRect,
            FailureReason = displayed ? string.Empty : "Replacement overlay did not show the test Turkish text.",
        };
        await SaveJsonAsync("replacement_overlay_display_test.json", report, cancellationToken).ConfigureAwait(false);
        await SaveJsonAsync("last_display_routing.json", new
        {
            Timestamp = DateTimeOffset.Now,
            FinalTranslationText = turkishText,
            DisplayMode = settings.DisplayMode.ToString(),
            OverlayTarget = "SubtitleReplacementOverlay",
            ManualRegionValid = true,
            OverlayUpdateCalled = true,
            OverlayVisible = _overlayService.IsOpen,
            DisplayedText = snapshot?.Text ?? string.Empty,
            FailureReason = report.FailureReason,
        }, cancellationToken).ConfigureAwait(false);
        return report;
        }
        finally
        {
            RestoreForcedSettings(backup);
        }
    }

    private async Task<EndToEndPipelineTestReport> FailEndToEndAsync(
        string traceId,
        string rawInput,
        string speakerName,
        string dialogueText,
        string stage,
        string reason,
        Dictionary<string, object?> trace,
        CancellationToken cancellationToken,
        string provider = "",
        string rawProviderTranslation = "",
        string postProcessedTranslation = "",
        string finalDisplayText = "")
    {
        trace["FailureStage"] = stage;
        trace["FailureReason"] = reason;
        await SaveJsonAsync("last_pipeline_trace.json", trace, cancellationToken).ConfigureAwait(false);

        var report = new EndToEndPipelineTestReport
        {
            Timestamp = DateTimeOffset.Now,
            TraceId = traceId,
            RawInput = rawInput,
            DetectedSpeaker = speakerName,
            DialogueText = dialogueText,
            ProviderUsed = provider,
            RawProviderTranslation = rawProviderTranslation,
            PostProcessedTranslation = postProcessedTranslation,
            FinalDisplayText = finalDisplayText,
            Success = false,
            FailureStage = stage,
            FailureReason = reason,
            SpeakerExcludedFromTranslation = !dialogueText.StartsWith(speakerName + " ", StringComparison.OrdinalIgnoreCase),
            EnglishWasBlocked = true,
        };
        await SaveJsonAsync("end_to_end_pipeline_test.json", report, cancellationToken).ConfigureAwait(false);
        return report;
    }

    private sealed record ForcedSettingsBackup(
        bool OverlayEnabled,
        SubtitleDisplayMode DisplayMode,
        bool UseManualReplacementRegion,
        string ReplacementRectSource,
        string ReplacementPendingMode,
        bool ReplacementMaskUseFixedRegionSize,
        bool EnableTranslation,
        bool TurkishOnlyMode,
        bool ShowSourceWhileTranslating,
        bool ShowMaskWhileTranslationPending);

    /// <summary>Mutates live settings into replacement/Turkish-only mode for a test run.
    /// Always pair with <see cref="RestoreForcedSettings"/> in a finally block so a
    /// diagnostic run cannot permanently change the user's configuration.</summary>
    private OverlaySettings ForceReplacementSettings(out ForcedSettingsBackup backup)
    {
        var settings = _overlayService.CurrentSettings;
        backup = new ForcedSettingsBackup(
            settings.IsEnabled,
            settings.DisplayMode,
            settings.Replacement.UseManualReplacementRegion,
            settings.Replacement.ReplacementRectSource,
            settings.Replacement.ReplacementPendingMode,
            settings.Replacement.ReplacementMaskUseFixedRegionSize,
            _translationSettings.EnableTranslation,
            _translationSettings.TurkishOnlyMode,
            _translationSettings.ShowSourceWhileTranslating,
            _translationSettings.ShowMaskWhileTranslationPending);

        settings.IsEnabled = true;
        settings.DisplayMode = SubtitleDisplayMode.SubtitleReplacementOverlay;
        settings.Replacement.UseManualReplacementRegion = true;
        settings.Replacement.ReplacementRectSource = "ManualReplacementRegion";
        settings.Replacement.ReplacementPendingMode = "MaskOnly";
        settings.Replacement.ReplacementMaskUseFixedRegionSize = true;
        _translationSettings.EnableTranslation = true;
        _translationSettings.TurkishOnlyMode = true;
        _translationSettings.ShowSourceWhileTranslating = false;
        _translationSettings.ShowMaskWhileTranslationPending = true;
        return settings;
    }

    private void RestoreForcedSettings(ForcedSettingsBackup backup)
    {
        var settings = _overlayService.CurrentSettings;
        settings.IsEnabled = backup.OverlayEnabled;
        settings.DisplayMode = backup.DisplayMode;
        settings.Replacement.UseManualReplacementRegion = backup.UseManualReplacementRegion;
        settings.Replacement.ReplacementRectSource = backup.ReplacementRectSource;
        settings.Replacement.ReplacementPendingMode = backup.ReplacementPendingMode;
        settings.Replacement.ReplacementMaskUseFixedRegionSize = backup.ReplacementMaskUseFixedRegionSize;
        _translationSettings.EnableTranslation = backup.EnableTranslation;
        _translationSettings.TurkishOnlyMode = backup.TurkishOnlyMode;
        _translationSettings.ShowSourceWhileTranslating = backup.ShowSourceWhileTranslating;
        _translationSettings.ShowMaskWhileTranslationPending = backup.ShowMaskWhileTranslationPending;
    }

    private void EnsureOverlayOpen(OverlaySettings settings)
    {
        if (_overlayService.IsOpen)
            _overlayService.ApplySettings(settings);
        else
            _overlayService.Open(settings);
    }

    private static SubtitleReplacementContext BuildManualReplacementContext(OverlaySettings settings)
    {
        var rect = new OverlayRectangle
        {
            X = settings.X + settings.Replacement.ManualReplacementRegionX,
            Y = settings.Y + settings.Replacement.ManualReplacementRegionY,
            Width = settings.Replacement.ManualReplacementRegionWidth,
            Height = settings.Replacement.ManualReplacementRegionHeight,
        };

        return new SubtitleReplacementContext
        {
            WindowRect = new OverlayRectangle { X = settings.X, Y = settings.Y, Width = settings.Width, Height = settings.Height },
            ScreenRect = rect.Clone(),
            OverlayRect = rect.Clone(),
            OcrLineRect = rect.Clone(),
            CropRect = rect.Clone(),
            SelectedLinesText = "manual replacement region",
        };
    }

    private async Task<bool> WaitForOverlayTextAsync(
        string expectedText,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = _overlayService.LastReplacementSnapshot?.Text ?? string.Empty;
            if (text.Contains(expectedText, StringComparison.Ordinal))
                return true;

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private int EstimateDisplayMs(string translatedText)
    {
        var charCount = translatedText.Count(c => !char.IsWhiteSpace(c));
        var extraLines = Math.Max(0, translatedText.Split('\n').Length - 1);
        var ms = _translationSettings.MinTurkishDisplayMs +
            charCount * _translationSettings.MsPerCharacter +
            extraLines * _translationSettings.ExtraLineMs;
        return Math.Clamp(ms, _translationSettings.MinTurkishDisplayMs, _translationSettings.MaxTurkishDisplayMs);
    }

    private object BuildQueueState() => new
    {
        Timestamp = DateTimeOffset.Now,
        framesCaptured = _diagnostics.LastFrameNumber,
        framesBuffered = _diagnostics.CapturedQueueCount,
        framesDropped = _diagnostics.ExpiredSkippedCount,
        ocrStarted = _diagnostics.LastOcrStartedAt is not null ? 1 : 0,
        ocrCompleted = _diagnostics.LastOcrFinishedAt is not null ? 1 : 0,
        candidatesAccepted = _diagnostics.AcceptedSubtitleCandidateCount,
        candidatesRejected = _diagnostics.RejectedBeforeQueueCount,
        queuedForTranslation = _diagnostics.CapturedQueueCount,
        translationStarted = _diagnostics.TranslationStartedCount,
        translationCompleted = _diagnostics.TranslationCompletedCount,
        playbackQueued = _diagnostics.PlaybackQueueCount,
        overlayUpdated = string.IsNullOrWhiteSpace(_diagnostics.LastOverlayUpdateSource) ? 0 : 1,
    };

    private object BuildOverlayVisibilityState()
    {
        var settings = _overlayService.CurrentSettings;
        var snapshot = _overlayService.LastReplacementSnapshot;
        return new
        {
            Timestamp = DateTimeOffset.Now,
            IsOpen = _overlayService.IsOpen,
            settings.DisplayMode,
            settings.Opacity,
            settings.IsClickThrough,
            settings.X,
            settings.Y,
            settings.Width,
            settings.Height,
            ReplacementRect = snapshot?.Context.OverlayRect,
            ManualRegionConfigured = ManualReplacementRegionHelper.IsConfigured(settings.Replacement),
            settings.Replacement.ReplacementTextColor,
            settings.Replacement.ReplacementMaskOpacity,
            LastSnapshotText = snapshot?.Text,
            LastSnapshotReason = snapshot?.Reason,
        };
    }

    private static string BuildDisplayFailureReason(bool replacementOk, bool manualRegionOk, bool overlayOpen)
    {
        if (!replacementOk) return "Replacement overlay mode/settings are not active.";
        if (!manualRegionOk) return "Replacement Mask Region is not selected.";
        if (!overlayOpen) return "Overlay window is not visible.";
        return string.Empty;
    }

    private static bool ContainsEnglishSource(string displayText, string sourceText)
    {
        var sourceKey = NormalizeKey(sourceText);
        var displayKey = NormalizeKey(displayText);
        return sourceKey.Length > 0 && displayKey.Contains(sourceKey, StringComparison.Ordinal);
    }

    private static string NormalizeKey(string text)
    {
        var normalized = text.ToLowerInvariant().Trim();
        return System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ");
    }

    private static async Task SaveJsonAsync(string fileName, object value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DebugDirectory);
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(
            Path.Combine(DebugDirectory, fileName),
            json,
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed class RuntimePipelineHealthReport
{
    public DateTimeOffset Timestamp { get; init; }
    public bool OcrProviderOk { get; init; }
    public bool OcrServerOk { get; init; }
    public bool OcrResultOk { get; init; }
    public bool TranslationProviderOk { get; init; }
    public bool TranslationServerOk { get; init; }
    public bool TranslationResultOk { get; init; }
    public bool ReplacementOverlayOk { get; init; }
    public bool ManualReplacementRegionOk { get; init; }
    public bool TurkishDisplayedOk { get; init; }
    public string SelectedOcrProvider { get; init; } = string.Empty;
    public string ActualOcrProvider { get; init; } = string.Empty;
    public string OcrServerUrl { get; init; } = string.Empty;
    public string OcrServerState { get; init; } = string.Empty;
    public string OcrServerMessage { get; init; } = string.Empty;
    public string LastOcrText { get; init; } = string.Empty;
    public long LastOcrDurationMs { get; init; }
    public string LastOcrError { get; init; } = string.Empty;
    public string LastOcrAcceptedOrRejected { get; init; } = string.Empty;
    public string SelectedTranslationProvider { get; init; } = string.Empty;
    public string ActualTranslationProvider { get; init; } = string.Empty;
    public string TranslationServerUrl { get; init; } = string.Empty;
    public string TranslationServerState { get; init; } = string.Empty;
    public string TranslationServerMessage { get; init; } = string.Empty;
    public string LastTranslationSourceText { get; init; } = string.Empty;
    public string LastTranslationResult { get; init; } = string.Empty;
    public long LastTranslationDurationMs { get; init; }
    public bool TranslationCacheHit { get; init; }
    public bool TranslationMemoryHit { get; init; }
    public bool TranslationInFlightHit { get; init; }
    public string FallbackReason { get; init; } = string.Empty;
    public string SelectedDisplayMode { get; init; } = string.Empty;
    public string ActualDisplayMode { get; init; } = string.Empty;
    public bool TurkishOnlyMode { get; init; }
    public bool UseManualReplacementRegion { get; init; }
    public object? ManualReplacementRegion { get; init; }
    public string LastOverlayUpdateText { get; init; } = string.Empty;
    public string LastOverlayUpdateSource { get; init; } = string.Empty;
    public bool OverlayVisible { get; init; }
    public bool EnglishBlocked { get; init; }
    public string LastDisplayFailureReason { get; init; } = string.Empty;
}

public sealed class EndToEndPipelineTestReport
{
    public DateTimeOffset Timestamp { get; init; }
    public string TraceId { get; init; } = string.Empty;
    public string RawInput { get; init; } = string.Empty;
    public string DetectedSpeaker { get; init; } = string.Empty;
    public string DialogueText { get; init; } = string.Empty;
    public string ProviderUsed { get; init; } = string.Empty;
    public string RawProviderTranslation { get; init; } = string.Empty;
    public string PostProcessedTranslation { get; init; } = string.Empty;
    public string FinalDisplayText { get; init; } = string.Empty;
    public string OverlayUpdateSource { get; init; } = string.Empty;
    public OverlayRectangle? OverlayRectangle { get; init; }
    public bool Success { get; init; }
    public string FailureStage { get; init; } = string.Empty;
    public string FailureReason { get; init; } = string.Empty;
    public bool EnglishWasBlocked { get; init; }
    public bool SpeakerExcludedFromTranslation { get; init; }
}

public sealed class TranslationProviderOnlyTestReport
{
    public DateTimeOffset Timestamp { get; init; }
    public string SourceText { get; init; } = string.Empty;
    public string SelectedProvider { get; init; } = string.Empty;
    public string ProviderUsed { get; init; } = string.Empty;
    public bool FallbackUsed { get; init; }
    public string FallbackReason { get; init; } = string.Empty;
    public string RawTranslation { get; init; } = string.Empty;
    public string PostProcessedTranslation { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public bool Success { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public IReadOnlyList<string> ChainSteps { get; init; } = [];
}

public sealed class ReplacementOverlayDisplayTestReport
{
    public DateTimeOffset Timestamp { get; init; }
    public string InputText { get; init; } = string.Empty;
    public string DisplayedText { get; init; } = string.Empty;
    public bool Success { get; init; }
    public bool ManualRegionValid { get; init; }
    public bool OverlayVisible { get; init; }
    public OverlayRectangle? OverlayRectangle { get; init; }
    public string FailureReason { get; init; } = string.Empty;
}
