using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Overlay;

public interface IMonitorService
{
    IReadOnlyList<MonitorInfo> GetConnectedMonitors();
    MonitorInfo GetPrimaryMonitor();
    MonitorInfo? GetMonitorContainingPoint(double x, double y);
    MonitorInfo? GetMonitorContainingRect(double x, double y, double width, double height);
    bool IsRectVisibleOnAnyMonitor(double x, double y, double width, double height);
    (double X, double Y, double Width, double Height) ClampRectToNearestMonitor(
        double x, double y, double width, double height);

    /// <summary>Returns the monitor a live window handle is currently on (nearest monitor if
    /// the window straddles multiple, or is partially/fully off-screen).</summary>
    MonitorInfo? GetMonitorForWindowHandle(nint hwnd);

    /// <summary>Fraction (0..1) of the rect's area that overlaps any connected monitor.</summary>
    double GetVisiblePercent(double x, double y, double width, double height);
}
