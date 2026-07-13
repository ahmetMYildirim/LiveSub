using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Overlay;

/// <summary>
/// Validates a saved overlay rectangle against currently connected monitors and, when
/// the rectangle is off-screen (or only barely visible), computes a safe recovered
/// position (Part B). Never leaves the overlay somewhere the user cannot reach it.
/// </summary>
public sealed class OverlayPositionValidator
{
    /// <summary>Below this fraction of the overlay's area being visible, treat the position as invalid.</summary>
    private const double MinVisiblePercent = 0.15;

    private readonly IMonitorService _monitorService;
    private readonly ILogger<OverlayPositionValidator> _logger;

    public OverlayPositionValidator(IMonitorService monitorService, ILogger<OverlayPositionValidator> logger)
    {
        _monitorService = monitorService;
        _logger = logger;
    }

    public OverlayPositionValidationResult Validate(
        OverlaySettings settings,
        bool autoRecover,
        OverlayResetKind resetKind = OverlayResetKind.NativeSubtitleOverlay,
        MonitorInfo? preferredMonitor = null)
    {
        var monitors = _monitorService.GetConnectedMonitors();
        if (monitors.Count == 0)
        {
            // Nothing to validate against — pass the settings through unchanged rather
            // than guessing at a "primary monitor" that couldn't be enumerated.
            return new OverlayPositionValidationResult
            {
                WasValid = true,
                X = settings.X,
                Y = settings.Y,
                Width = settings.Width,
                Height = settings.Height,
                Reason = "no_monitors_detected",
            };
        }

        var visiblePercent = _monitorService.GetVisiblePercent(settings.X, settings.Y, settings.Width, settings.Height);
        var containingMonitor = _monitorService.GetMonitorContainingRect(settings.X, settings.Y, settings.Width, settings.Height);
        var oversized = containingMonitor is not null &&
            (settings.Width > containingMonitor.WorkingAreaWidth || settings.Height > containingMonitor.WorkingAreaHeight);

        var isValid = visiblePercent >= MinVisiblePercent && !oversized;

        if (isValid || !autoRecover)
        {
            return new OverlayPositionValidationResult
            {
                WasValid = isValid,
                WasRecovered = false,
                Reason = isValid ? "overlay_position_valid" : "overlay_invalid_recovery_disabled",
                X = settings.X,
                Y = settings.Y,
                Width = settings.Width,
                Height = settings.Height,
                MonitorDeviceName = containingMonitor?.DeviceName,
                VisiblePercent = visiblePercent,
            };
        }

        var targetMonitor = preferredMonitor ?? _monitorService.GetPrimaryMonitor();
        var (x, y, width, height) = ComputeDefaultPosition(targetMonitor, resetKind, settings.Width, settings.Height);

        _logger.LogWarning(
            "overlay_recovered_from_disconnected_monitor - oldPos=({OldX},{OldY}) {OldW}x{OldH} " +
            "(visible={Visible:P0}) -> newPos=({X},{Y}) {W}x{H} on {Monitor}",
            settings.X, settings.Y, settings.Width, settings.Height, visiblePercent,
            x, y, width, height, targetMonitor.DeviceName);

        return new OverlayPositionValidationResult
        {
            WasValid = false,
            WasRecovered = true,
            Reason = "overlay_recovered_from_disconnected_monitor",
            X = x,
            Y = y,
            Width = width,
            Height = height,
            MonitorDeviceName = targetMonitor.DeviceName,
            VisiblePercent = visiblePercent,
        };
    }

    public (double X, double Y, double Width, double Height) ComputeDefaultPosition(
        MonitorInfo monitor, OverlayResetKind kind, double requestedWidth, double requestedHeight) => kind switch
    {
        OverlayResetKind.TranslationPanelOverlay => ComputeBottomLeft(
            monitor, requestedWidth, requestedHeight, leftMargin: 40, bottomMargin: 90),
        OverlayResetKind.NativeSubtitleOverlay => ComputeBottomCenter(
            monitor, requestedWidth, requestedHeight, bottomMargin: 105),
        _ => ComputeBottomCenter(monitor, requestedWidth, requestedHeight, bottomMargin: 100),
    };

    public (double X, double Y, double Width, double Height) ComputeCentered(MonitorInfo monitor, double width, double height)
    {
        var clampedWidth = Math.Min(width > 0 ? width : monitor.WorkingAreaWidth * 0.7, monitor.WorkingAreaWidth);
        var clampedHeight = Math.Min(height > 0 ? height : 160, monitor.WorkingAreaHeight);
        var x = monitor.WorkingAreaX + (monitor.WorkingAreaWidth - clampedWidth) / 2.0;
        var y = monitor.WorkingAreaY + (monitor.WorkingAreaHeight - clampedHeight) / 2.0;
        return (x, y, clampedWidth, clampedHeight);
    }

    private static (double X, double Y, double Width, double Height) ComputeBottomCenter(
        MonitorInfo monitor, double requestedWidth, double requestedHeight, double bottomMargin)
    {
        var width = Math.Min(
            requestedWidth > 0 ? requestedWidth : monitor.WorkingAreaWidth * 0.7,
            monitor.WorkingAreaWidth * 0.95);
        var height = Math.Min(requestedHeight > 0 ? requestedHeight : 160, monitor.WorkingAreaHeight * 0.6);

        var x = monitor.WorkingAreaX + (monitor.WorkingAreaWidth - width) / 2.0;
        var y = Math.Max(monitor.WorkingAreaY, monitor.WorkingAreaY + monitor.WorkingAreaHeight - height - bottomMargin);
        return (x, y, width, height);
    }

    private static (double X, double Y, double Width, double Height) ComputeBottomLeft(
        MonitorInfo monitor, double requestedWidth, double requestedHeight, double leftMargin, double bottomMargin)
    {
        var width = Math.Min(
            requestedWidth > 0 ? requestedWidth : monitor.WorkingAreaWidth * 0.4,
            monitor.WorkingAreaWidth - leftMargin);
        var height = Math.Min(requestedHeight > 0 ? requestedHeight : 160, monitor.WorkingAreaHeight * 0.6);

        var x = monitor.WorkingAreaX + leftMargin;
        var y = Math.Max(monitor.WorkingAreaY, monitor.WorkingAreaY + monitor.WorkingAreaHeight - height - bottomMargin);
        return (x, y, width, height);
    }
}
