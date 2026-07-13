namespace PsGameTranslator.Core.Models;

/// <summary>A connected display, in physical device pixels (screen coordinates).</summary>
public sealed class MonitorInfo
{
    public string DeviceName { get; init; } = string.Empty;
    public double BoundsX { get; init; }
    public double BoundsY { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public double WorkingAreaX { get; init; }
    public double WorkingAreaY { get; init; }
    public double WorkingAreaWidth { get; init; }
    public double WorkingAreaHeight { get; init; }
    public bool IsPrimary { get; init; }
    public double DpiScaleX { get; init; } = 1.0;
    public double DpiScaleY { get; init; } = 1.0;

    public override string ToString() =>
        $"{DeviceName}{(IsPrimary ? " (Primary)" : string.Empty)} — " +
        $"{Width:F0}x{Height:F0} at ({BoundsX:F0},{BoundsY:F0}), DPI {DpiScaleX:P0}";
}
