using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.App.ViewModels;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Overlay;

namespace PsGameTranslator.App.Services;

/// <summary>
/// Resolves which monitor the overlay should live on (Part C), validates/recovers its
/// saved position against the currently connected monitors (Part B), and watches for
/// monitor configuration changes at runtime (Part E). This is the single place that
/// decides "where should the overlay actually go" — OverlayViewModel and
/// MonitoringViewModel both go through it instead of trusting raw saved X/Y.
/// </summary>
public sealed class OverlayMonitorCoordinator : IDisposable
{
    private static readonly string DebugDirectory = Path.Combine(AppContext.BaseDirectory, "debug");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IMonitorService _monitorService;
    private readonly OverlayPositionValidator _validator;
    private readonly IOverlaySettingsService _settingsService;
    private readonly CaptureViewModel _captureViewModel;
    private readonly ILogger<OverlayMonitorCoordinator> _logger;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pollTask;
    private string _lastMonitorFingerprint = string.Empty;

    /// <summary>Raised on a background thread whenever the set of connected monitors changes.</summary>
    public event Action? MonitorConfigurationChanged;

    public string LastRecoveryReason { get; private set; } = "-";
    public string? LastManualMonitorFallbackWarning { get; private set; }

    public OverlayMonitorCoordinator(
        IMonitorService monitorService,
        OverlayPositionValidator validator,
        IOverlaySettingsService settingsService,
        CaptureViewModel captureViewModel,
        ILogger<OverlayMonitorCoordinator> logger)
    {
        _monitorService = monitorService;
        _validator = validator;
        _settingsService = settingsService;
        _captureViewModel = captureViewModel;
        _logger = logger;
        _lastMonitorFingerprint = ComputeFingerprint(_monitorService.GetConnectedMonitors());
        _pollTask = Task.Run(PollLoopAsync);
    }

    // ── Monitor mode resolution (Part C) ─────────────────────────────────────────

    /// <summary>Resolves the monitor OverlayTargetMonitorMode currently points to, falling back
    /// to the primary monitor (with a warning) if a manual selection is disconnected.</summary>
    public MonitorInfo ResolveTargetMonitor(OverlaySettings settings)
    {
        LastManualMonitorFallbackWarning = null;

        switch (settings.OverlayTargetMonitorMode)
        {
            case OverlayTargetMonitorMode.SameAsCaptureWindow:
                var handle = _captureViewModel.SelectedWindow?.Handle ?? nint.Zero;
                var captureMonitor = handle != nint.Zero ? _monitorService.GetMonitorForWindowHandle(handle) : null;
                return captureMonitor ?? _monitorService.GetPrimaryMonitor();

            case OverlayTargetMonitorMode.ManualMonitor:
                var manual = _monitorService.GetConnectedMonitors()
                    .FirstOrDefault(m => m.DeviceName == settings.SelectedOverlayMonitorDeviceName);
                if (manual is not null) return manual;

                LastManualMonitorFallbackWarning =
                    $"Selected monitor '{settings.SelectedOverlayMonitorDeviceName}' is disconnected — using primary monitor instead.";
                _logger.LogWarning(
                    "overlay_manual_monitor_disconnected - {DeviceName}", settings.SelectedOverlayMonitorDeviceName);
                return _monitorService.GetPrimaryMonitor();

            default:
                return _monitorService.GetPrimaryMonitor();
        }
    }

    public MonitorInfo? GetCaptureWindowMonitor()
    {
        var handle = _captureViewModel.SelectedWindow?.Handle ?? nint.Zero;
        return handle != nint.Zero ? _monitorService.GetMonitorForWindowHandle(handle) : null;
    }

    // ── Load + validate (Part B) ──────────────────────────────────────────────────

    /// <summary>Loads saved overlay settings, validates the rect against connected monitors,
    /// and — if it's off-screen/disconnected — recovers and persists a corrected position.</summary>
    public async Task<OverlaySettings> LoadAndValidateAsync()
    {
        var settings = await _settingsService.LoadAsync();
        var corrected = await ValidateAndCorrectAsync(settings);
        return corrected;
    }

    public async Task<OverlaySettings> ValidateAndCorrectAsync(OverlaySettings settings)
    {
        var targetMonitor = ResolveTargetMonitor(settings);
        var result = _validator.Validate(
            settings, settings.AutoRecoverOffscreenOverlay, OverlayResetKind.NativeSubtitleOverlay, targetMonitor);

        LastRecoveryReason = result.Reason;
        await SaveValidationDiagnosticsAsync(result, settings, targetMonitor);

        if (!result.WasRecovered) return settings;

        settings.X = result.X;
        settings.Y = result.Y;
        settings.Width = result.Width;
        settings.Height = result.Height;
        settings.LastKnownMonitorDeviceName = result.MonitorDeviceName ?? string.Empty;
        await _settingsService.SaveAsync(settings);
        return settings;
    }

    // ── Manual placement actions (Part D) ────────────────────────────────────────

    public Task<OverlaySettings> MoveToPrimaryMonitorAsync(OverlaySettings current)
    {
        var monitor = _monitorService.GetPrimaryMonitor();
        return ApplyPositionAsync(current, monitor, OverlayResetKind.NativeSubtitleOverlay, "moved_to_primary_monitor");
    }

    public Task<OverlaySettings> MoveToCaptureWindowMonitorAsync(OverlaySettings current)
    {
        var monitor = GetCaptureWindowMonitor() ?? _monitorService.GetPrimaryMonitor();
        return ApplyPositionAsync(current, monitor, OverlayResetKind.NativeSubtitleOverlay, "moved_to_capture_window_monitor");
    }

    public async Task<OverlaySettings> CenterOnCurrentMonitorAsync(OverlaySettings current)
    {
        var monitor = _monitorService.GetMonitorContainingRect(current.X, current.Y, current.Width, current.Height)
            ?? _monitorService.GetPrimaryMonitor();
        var (x, y, width, height) = _validator.ComputeCentered(monitor, current.Width, current.Height);

        current.X = x;
        current.Y = y;
        current.Width = width;
        current.Height = height;
        current.LastKnownMonitorDeviceName = monitor.DeviceName;
        LastRecoveryReason = "centered_on_current_monitor";
        await _settingsService.SaveAsync(current);
        return current;
    }

    public Task<OverlaySettings> ResetOverlayPositionAsync(OverlaySettings current)
    {
        var monitor = ResolveTargetMonitor(current);
        return ApplyPositionAsync(current, monitor, OverlayResetKind.Default, "overlay_position_reset", forceDefaultSize: true);
    }

    /// <summary>"Recover Overlay Now" — forces recovery to the mode-resolved target
    /// monitor regardless of current visibility.</summary>
    public Task<OverlaySettings> RecoverNowAsync(OverlaySettings current)
    {
        var monitor = ResolveTargetMonitor(current);
        return ApplyPositionAsync(current, monitor, OverlayResetKind.NativeSubtitleOverlay, "overlay_recovered_manually");
    }

    private async Task<OverlaySettings> ApplyPositionAsync(
        OverlaySettings current, MonitorInfo monitor, OverlayResetKind kind, string reason, bool forceDefaultSize = false)
    {
        var (x, y, width, height) = _validator.ComputeDefaultPosition(
            monitor, kind, forceDefaultSize ? 0 : current.Width, forceDefaultSize ? 0 : current.Height);

        current.X = x;
        current.Y = y;
        current.Width = width;
        current.Height = height;
        current.LastKnownMonitorDeviceName = monitor.DeviceName;
        LastRecoveryReason = reason;
        _logger.LogInformation("{Reason} - monitor={Monitor}, rect=({X},{Y}) {W}x{H}", reason, monitor.DeviceName, x, y, width, height);
        await _settingsService.SaveAsync(current);
        return current;
    }

    // ── Runtime change detection (Part E) ────────────────────────────────────────

    private async Task PollLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(3000, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try
            {
                var monitors = _monitorService.GetConnectedMonitors();
                var fingerprint = ComputeFingerprint(monitors);
                if (fingerprint == _lastMonitorFingerprint) continue;

                _lastMonitorFingerprint = fingerprint;
                _logger.LogInformation("monitor_config_changed - {Count} monitor(s) now connected", monitors.Count);
                await SaveMonitorStateAsync(monitors);
                MonitorConfigurationChanged?.Invoke();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "monitor_poll_failed");
            }
        }
    }

    private static string ComputeFingerprint(IReadOnlyList<MonitorInfo> monitors) =>
        string.Join("|", monitors
            .OrderBy(m => m.DeviceName, StringComparer.Ordinal)
            .Select(m => $"{m.DeviceName}:{m.BoundsX},{m.BoundsY},{m.Width}x{m.Height},{m.DpiScaleX:F2}"));

    // ── Diagnostics (Part H) ──────────────────────────────────────────────────────

    public async Task SaveMonitorStateAsync(IReadOnlyList<MonitorInfo>? monitors = null)
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            var list = monitors ?? _monitorService.GetConnectedMonitors();
            var snapshot = new
            {
                Timestamp = DateTimeOffset.Now,
                Monitors = list.Select(m => new
                {
                    m.DeviceName, m.BoundsX, m.BoundsY, m.Width, m.Height,
                    m.WorkingAreaX, m.WorkingAreaY, m.WorkingAreaWidth, m.WorkingAreaHeight,
                    m.IsPrimary, m.DpiScaleX, m.DpiScaleY,
                }),
                CaptureWindowMonitor = GetCaptureWindowMonitor()?.DeviceName,
            };
            await File.WriteAllTextAsync(
                Path.Combine(DebugDirectory, "last_monitor_state.json"),
                JsonSerializer.Serialize(snapshot, JsonOptions), Encoding.UTF8);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save monitor state diagnostics");
        }
    }

    private async Task SaveValidationDiagnosticsAsync(
        OverlayPositionValidationResult result, OverlaySettings originalSettings, MonitorInfo targetMonitor)
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            var snapshot = new
            {
                Timestamp = DateTimeOffset.Now,
                SavedRect = new { originalSettings.X, originalSettings.Y, originalSettings.Width, originalSettings.Height },
                CorrectedRect = new { result.X, result.Y, result.Width, result.Height },
                result.WasValid,
                result.WasRecovered,
                result.Reason,
                result.VisiblePercent,
                TargetMode = originalSettings.OverlayTargetMonitorMode.ToString(),
                SelectedMonitor = originalSettings.SelectedOverlayMonitorDeviceName,
                TargetMonitor = targetMonitor.DeviceName,
                CaptureWindowMonitor = GetCaptureWindowMonitor()?.DeviceName,
                LastKnownMonitor = originalSettings.LastKnownMonitorDeviceName,
            };
            await File.WriteAllTextAsync(
                Path.Combine(DebugDirectory, "last_overlay_position_validation.json"),
                JsonSerializer.Serialize(snapshot, JsonOptions), Encoding.UTF8);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save overlay position validation diagnostics");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _pollTask.Wait(2000); } catch { /* shutting down */ }
        _cts.Dispose();
    }
}
