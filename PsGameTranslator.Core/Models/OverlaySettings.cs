namespace PsGameTranslator.Core.Models;

public sealed class OverlaySettings
{
    public SubtitleDisplayMode DisplayMode { get; set; } = SubtitleDisplayMode.NativeSubtitleOverlay;
    public bool IsEnabled { get; set; } = false;
    public bool IsClickThrough { get; set; } = true;
    public double Opacity { get; set; } = 1.0;
    public double X { get; set; } = 100;
    public double Y { get; set; } = 100;
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 260;

    /// <summary>Grow/shrink overlay height to fit the text, capped at <see cref="MaxHeight"/>.</summary>
    public bool AutoFitHeight { get; set; } = true;

    /// <summary>Maximum overlay height when <see cref="AutoFitHeight"/> is on.</summary>
    public double MaxHeight { get; set; } = 320;

    /// <summary>Coalesce rapid overlay text updates; only the latest is shown.</summary>
    public int OverlayUpdateDebounceMs { get; set; } = 150;

    public SubtitleOverlayStyleSettings Style { get; set; } =
        SubtitleOverlayStyleSettings.CreatePreset(SubtitlePreset.Cinematic);

    public SubtitleReplacementOverlaySettings Replacement { get; set; } = new();

    // ── Multi-monitor support (Part C/G) ─────────────────────────────────────────
    public OverlayTargetMonitorMode OverlayTargetMonitorMode { get; set; } =
        OverlayTargetMonitorMode.SameAsCaptureWindow;
    public string SelectedOverlayMonitorDeviceName { get; set; } = string.Empty;
    public bool AutoRecoverOffscreenOverlay { get; set; } = true;
    /// <summary>The monitor the overlay was last known to be positioned on.
    /// Used to detect "old X/Y point to a monitor that no longer exists."</summary>
    public string LastKnownMonitorDeviceName { get; set; } = string.Empty;
}
