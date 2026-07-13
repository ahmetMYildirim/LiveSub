namespace PsGameTranslator.Core.Models;

/// <summary>
/// Converts the user-selected manual replacement mask region (window-relative)
/// into screen coordinates (Part A). The region is used exactly as selected —
/// it never shrinks to the translated-text size — only clamped to the window.
/// </summary>
public static class ManualReplacementRegionHelper
{
    public static bool IsConfigured(SubtitleReplacementOverlaySettings settings) =>
        settings.UseManualReplacementRegion &&
        settings.ManualReplacementRegionWidth > 100 &&
        settings.ManualReplacementRegionHeight > 30;

    public static OverlayRectangle? TryGetScreenRect(
        SubtitleReplacementOverlaySettings settings,
        double windowLeft,
        double windowTop,
        double windowWidth,
        double windowHeight)
    {
        if (!IsConfigured(settings) || windowWidth <= 0 || windowHeight <= 0)
            return null;

        var x = windowLeft + settings.ManualReplacementRegionX;
        var y = windowTop + settings.ManualReplacementRegionY;
        var width = Math.Min(settings.ManualReplacementRegionWidth, windowWidth);
        var height = Math.Min(settings.ManualReplacementRegionHeight, windowHeight);

        // Clamp inside the window without changing the selected size.
        x = Math.Clamp(x, windowLeft, Math.Max(windowLeft, windowLeft + windowWidth - width));
        y = Math.Clamp(y, windowTop, Math.Max(windowTop, windowTop + windowHeight - height));

        return new OverlayRectangle { X = x, Y = y, Width = width, Height = height };
    }
}
