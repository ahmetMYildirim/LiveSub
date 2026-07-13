using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using PsGameTranslator.App.Views;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Overlay;

namespace PsGameTranslator.App.Services;

public sealed class OverlayService : IOverlayService
{
    private readonly ILogger<OverlayService> _logger;
    private OverlayWindow? _window;
    private DispatcherTimer? _debounceTimer;
    private string? _pendingText;
    private int _debounceMs = 150;
    private OverlaySettings _currentSettings = new();
    private SubtitleReplacementOverlaySnapshot? _lastReplacementSnapshot;

    public OverlayService(ILogger<OverlayService> logger)
    {
        _logger = logger;
    }

    public bool IsOpen
    {
        get
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
                return _window is { IsVisible: true };

            return dispatcher.Invoke(() => _window is { IsVisible: true });
        }
    }

    public OverlaySettings CurrentSettings => CloneSettings(_currentSettings);
    public SubtitleReplacementOverlaySnapshot? LastReplacementSnapshot => _lastReplacementSnapshot?.Clone();

    public void Open(OverlaySettings settings)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _currentSettings = CloneSettings(settings);
            if (_window != null)
            {
                _logger.LogWarning("Overlay window is already open, applying new settings");
                ApplySettingsCore(settings);
                return;
            }

            ValidateSettings(settings);
            _window = new OverlayWindow();
            _window.Closed += OnWindowClosed;
            ApplyNormalWindowBounds(settings);
            _window.Opacity = Math.Clamp(settings.Opacity, 0.1, 1.0);
            _window.SetAutoFit(settings.AutoFitHeight, settings.MaxHeight);
            _window.ApplyStyle(settings.Style);
            _window.ClearReplacementMode();
            _debounceMs = Math.Max(0, settings.OverlayUpdateDebounceMs);
            _window.Show();
            _window.SetClickThrough(settings.IsClickThrough);
            _logger.LogInformation(
                "Overlay window opened at ({X},{Y}) {W}x{H}",
                settings.X,
                settings.Y,
                settings.Width,
                settings.Height);
        });
    }

    public void Close()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_window == null) return;

            _debounceTimer?.Stop();
            _pendingText = null;
            _window.Closed -= OnWindowClosed;
            _window.Close();
            _window = null;
            _lastReplacementSnapshot = null;
            _logger.LogInformation("Overlay window closed");
        });
    }

    public void UpdateText(string text)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_window == null) return;

            if (_currentSettings.DisplayMode == SubtitleDisplayMode.SubtitleReplacementOverlay &&
                !string.IsNullOrWhiteSpace(text))
            {
                _logger.LogError("ERROR_ENGLISH_DISPLAYED_IN_REPLACEMENT_MODE blocked direct UpdateText call");
                return;
            }

            if (_debounceMs <= 0)
            {
                if (string.IsNullOrEmpty(text))
                    RestoreNormalMode();

                _window.SetText(text);
                _logger.LogInformation("Overlay text updated without debounce");
                return;
            }

            _pendingText = text;
            _debounceTimer ??= CreateDebounceTimer();
            _debounceTimer.Interval = TimeSpan.FromMilliseconds(_debounceMs);
            _debounceTimer.Stop();
            _debounceTimer.Start();
        });
    }

    public void UpdateReplacementOverlay(SubtitleReplacementOverlayUpdate update)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_window == null) return;

            _debounceTimer?.Stop();
            _pendingText = null;

            var context = update.Context.Clone();
            var requestedRect = context.OverlayRect.Width > 0 && context.OverlayRect.Height > 0
                ? context.OverlayRect.Clone()
                : context.ScreenRect.Clone();
            var (dpiScaleX, dpiScaleY) = GetDpiScale(requestedRect);
            context.DpiScaleX = dpiScaleX;
            context.DpiScaleY = dpiScaleY;
            PopulateMonitorInfo(context);

            var safeText = update.Text ?? string.Empty;
            var showMaskOnly = update.ShowMaskOnly;
            if (!string.IsNullOrWhiteSpace(safeText) &&
                (string.Equals(NormalizeGuardText(safeText), NormalizeGuardText(update.SourceText), StringComparison.Ordinal) ||
                 safeText.TrimStart().StartsWith("[TR]", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogError("ERROR_ENGLISH_DISPLAYED_IN_REPLACEMENT_MODE blocked replacement text");
                safeText = string.Empty;
                showMaskOnly = true;
            }

            // Speaker name is rendered on its own right-aligned line above the
            // Turkish text by the overlay window (ApplySpeaker) rather than being
            // prepended into the sentence — kept separate so the English guard
            // above keeps comparing dialogue-to-dialogue and the speaker is never
            // merged into the translated line (Part E).
            var speakerName = (!showMaskOnly && !string.IsNullOrWhiteSpace(safeText))
                ? update.SpeakerName?.Trim() ?? string.Empty
                : string.Empty;

            context.OverlayRect = requestedRect.Clone();
            _window.Opacity = 1.0;
            _window.ShowReplacementOverlay(
                safeText,
                _currentSettings.Replacement,
                showMaskOnly,
                ToDip(requestedRect.Width, dpiScaleX),
                ToDip(requestedRect.Height, dpiScaleY),
                speakerName);

            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    (int)Math.Round(requestedRect.X),
                    (int)Math.Round(requestedRect.Y),
                    Math.Max(40, (int)Math.Round(requestedRect.Width)),
                    Math.Max(24, (int)Math.Round(requestedRect.Height)),
                    SWP_NOACTIVATE | SWP_NOZORDER | SWP_SHOWWINDOW);
            }
            else
            {
                _window.Left = ToDip(requestedRect.X, dpiScaleX);
                _window.Top = ToDip(requestedRect.Y, dpiScaleY);
                _window.Width = Math.Max(40, ToDip(requestedRect.Width, dpiScaleX));
                _window.Height = Math.Max(24, ToDip(requestedRect.Height, dpiScaleY));
            }
            _window.SetClickThrough(_currentSettings.IsClickThrough);

            _lastReplacementSnapshot = new SubtitleReplacementOverlaySnapshot
            {
                Text = safeText,
                SourceText = update.SourceText,
                Reason = update.Reason,
                ShowMaskOnly = showMaskOnly,
                DisplayDurationMs = update.DisplayDurationMs,
                Context = context,
            };
        });
    }

    public void ApplySettings(OverlaySettings settings)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _currentSettings = CloneSettings(settings);
            if (settings.DisplayMode == SubtitleDisplayMode.SubtitleReplacementOverlay)
            {
                _debounceTimer?.Stop();
                _pendingText = null;
            }
            ApplySettingsCore(settings);
        });
    }

    public void EnterConfigMode()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_window == null) return;

            _window.SetClickThrough(false);
            _window.EnterConfigMode();
            _logger.LogInformation("Overlay config mode entered");
        });
    }

    public (double X, double Y, double Width, double Height) ExitConfigMode()
    {
        return Application.Current.Dispatcher.Invoke(() =>
        {
            if (_window == null)
            {
                _logger.LogWarning("ExitConfigMode called but overlay window is null");
                return (100.0, 100.0, 1280.0, 260.0);
            }

            var bounds = (_window.Left, _window.Top, _window.ActualWidth, _window.ActualHeight);
            _window.ExitConfigMode();
            _logger.LogInformation(
                "Overlay config mode exited: ({X},{Y}) {W}x{H}",
                bounds.Left,
                bounds.Top,
                bounds.ActualWidth,
                bounds.ActualHeight);
            return bounds;
        });
    }

    private DispatcherTimer CreateDebounceTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_window != null && _pendingText != null)
            {
                if (string.IsNullOrEmpty(_pendingText))
                    RestoreNormalMode();

                _window.SetText(_pendingText);
                _logger.LogInformation("Overlay text updated (debounced)");
            }

            _pendingText = null;
        };

        return timer;
    }

    private void ApplySettingsCore(OverlaySettings settings)
    {
        if (_window == null) return;

        try
        {
            ValidateSettings(settings);
            if (!_window.IsReplacementMode)
                ApplyNormalWindowBounds(settings);

            _window.Opacity = Math.Clamp(settings.Opacity, 0.1, 1.0);
            _window.SetAutoFit(settings.AutoFitHeight, settings.MaxHeight);
            _window.ApplyStyle(settings.Style);
            _window.SetClickThrough(settings.IsClickThrough);
            _debounceMs = Math.Max(0, settings.OverlayUpdateDebounceMs);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to apply overlay settings");
        }
    }

    private static void ValidateSettings(OverlaySettings settings)
    {
        if (settings.Width < 240 || settings.Height < 80)
            throw new ArgumentException(
                $"Invalid overlay size {settings.Width}x{settings.Height} - minimum is 240x80.");
    }

    private void ApplyNormalWindowBounds(OverlaySettings settings)
    {
        if (_window == null) return;
        _window.Left = settings.X;
        _window.Top = settings.Y;
        _window.Width = Math.Max(240, settings.Width);
        _window.Height = Math.Max(80, settings.Height);
    }

    private void RestoreNormalMode()
    {
        if (_window == null) return;
        _window.ClearReplacementMode();
        ApplyNormalWindowBounds(_currentSettings);
        _window.Opacity = Math.Clamp(_currentSettings.Opacity, 0.1, 1.0);
        _window.ApplyStyle(_currentSettings.Style);
    }

    private static OverlaySettings CloneSettings(OverlaySettings source) => new()
    {
        DisplayMode = source.DisplayMode,
        IsEnabled = source.IsEnabled,
        IsClickThrough = source.IsClickThrough,
        Opacity = source.Opacity,
        X = source.X,
        Y = source.Y,
        Width = source.Width,
        Height = source.Height,
        AutoFitHeight = source.AutoFitHeight,
        MaxHeight = source.MaxHeight,
        OverlayUpdateDebounceMs = source.OverlayUpdateDebounceMs,
        Style = source.Style is null ? SubtitleOverlayStyleSettings.CreatePreset(SubtitlePreset.Cinematic) : new SubtitleOverlayStyleSettings
        {
            SubtitlePreset = source.Style.SubtitlePreset,
            FontFamily = source.Style.FontFamily,
            FontSize = source.Style.FontSize,
            FontWeight = source.Style.FontWeight,
            BackgroundEnabled = source.Style.BackgroundEnabled,
            BackgroundOpacity = source.Style.BackgroundOpacity,
            BackgroundCornerRadius = source.Style.BackgroundCornerRadius,
            PaddingHorizontal = source.Style.PaddingHorizontal,
            PaddingVertical = source.Style.PaddingVertical,
            MaxWidthPercent = source.Style.MaxWidthPercent,
            BottomMargin = source.Style.BottomMargin,
            TextAlignment = source.Style.TextAlignment,
            ShadowEnabled = source.Style.ShadowEnabled,
            OutlineEnabled = source.Style.OutlineEnabled,
            OutlineThickness = source.Style.OutlineThickness,
        },
        Replacement = new SubtitleReplacementOverlaySettings
        {
            ReplacementMaskEnabled = source.Replacement.ReplacementMaskEnabled,
            ReplacementMaskOpacity = source.Replacement.ReplacementMaskOpacity,
            ReplacementMaskCornerRadius = source.Replacement.ReplacementMaskCornerRadius,
            ReplacementMaskColor = source.Replacement.ReplacementMaskColor,
            ReplacementMaskPaddingLeft = source.Replacement.ReplacementMaskPaddingLeft,
            ReplacementMaskPaddingTop = source.Replacement.ReplacementMaskPaddingTop,
            ReplacementMaskPaddingRight = source.Replacement.ReplacementMaskPaddingRight,
            ReplacementMaskPaddingBottom = source.Replacement.ReplacementMaskPaddingBottom,
            ReplacementMinWidth = source.Replacement.ReplacementMinWidth,
            ReplacementMaxWidthPercent = source.Replacement.ReplacementMaxWidthPercent,
            ReplacementMinHeight = source.Replacement.ReplacementMinHeight,
            ReplacementFontFamily = source.Replacement.ReplacementFontFamily,
            ReplacementFontSize = source.Replacement.ReplacementFontSize,
            ReplacementFontWeight = source.Replacement.ReplacementFontWeight,
            ReplacementTextColor = source.Replacement.ReplacementTextColor,
            ReplacementTextAlignment = source.Replacement.ReplacementTextAlignment,
            ReplacementMaxLines = source.Replacement.ReplacementMaxLines,
            ReplacementLineSpacing = source.Replacement.ReplacementLineSpacing,
            ReplacementTextShadowEnabled = source.Replacement.ReplacementTextShadowEnabled,
            ReplacementOutlineEnabled = source.Replacement.ReplacementOutlineEnabled,
            ShowReplacementRectOutline = source.Replacement.ShowReplacementRectOutline,
            RejectHudControlText = source.Replacement.RejectHudControlText,
            UseSubtitleCandidateScoring = source.Replacement.UseSubtitleCandidateScoring,
            UseManualReplacementRegion = source.Replacement.UseManualReplacementRegion,
            ManualReplacementRegionX = source.Replacement.ManualReplacementRegionX,
            ManualReplacementRegionY = source.Replacement.ManualReplacementRegionY,
            ManualReplacementRegionWidth = source.Replacement.ManualReplacementRegionWidth,
            ManualReplacementRegionHeight = source.Replacement.ManualReplacementRegionHeight,
            ReplacementRectSource = source.Replacement.ReplacementRectSource,
            ReplacementPendingMode = source.Replacement.ReplacementPendingMode,
            ReplacementMaskUseFixedRegionSize = source.Replacement.ReplacementMaskUseFixedRegionSize,
            ReplacementAutoFitText = source.Replacement.ReplacementAutoFitText,
            ReplacementMinFontSize = source.Replacement.ReplacementMinFontSize,
        },
        OverlayTargetMonitorMode = source.OverlayTargetMonitorMode,
        SelectedOverlayMonitorDeviceName = source.SelectedOverlayMonitorDeviceName,
        AutoRecoverOffscreenOverlay = source.AutoRecoverOffscreenOverlay,
        LastKnownMonitorDeviceName = source.LastKnownMonitorDeviceName,
    };

    private static (double ScaleX, double ScaleY) GetDpiScale(OverlayRectangle screenRect)
    {
        var point = new POINT
        {
            X = (int)Math.Round(screenRect.X + Math.Max(0, screenRect.Width / 2.0)),
            Y = (int)Math.Round(screenRect.Y + Math.Max(0, screenRect.Height / 2.0)),
        };

        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero &&
            GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY) == 0)
        {
            return (dpiX / 96.0, dpiY / 96.0);
        }

        return (1.0, 1.0);
    }

    private static double ToDip(double value, double dpiScale) =>
        dpiScale <= 0 ? value : value / dpiScale;

    private static string NormalizeGuardText(string? value) =>
        string.Join(' ', (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void PopulateMonitorInfo(SubtitleReplacementContext context)
    {
        var point = new POINT
        {
            X = (int)Math.Round(context.ScreenRect.X + context.ScreenRect.Width / 2),
            Y = (int)Math.Round(context.ScreenRect.Y + context.ScreenRect.Height / 2),
        };
        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        context.MonitorDeviceName = info.szDevice ?? string.Empty;
        context.MonitorBounds = new OverlayRectangle
        {
            X = info.rcMonitor.Left,
            Y = info.rcMonitor.Top,
            Width = info.rcMonitor.Right - info.rcMonitor.Left,
            Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
        };
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _window = null;
        _logger.LogInformation("Overlay window was closed by user");
    }

    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
