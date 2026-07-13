namespace PsGameTranslator.Overlay;

public sealed class OverlayPositionValidationResult
{
    public bool WasValid { get; init; }
    public bool WasRecovered { get; init; }
    public string Reason { get; init; } = string.Empty;
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public string? MonitorDeviceName { get; init; }
    public double VisiblePercent { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}
