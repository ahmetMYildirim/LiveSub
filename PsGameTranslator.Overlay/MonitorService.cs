using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Overlay;

/// <summary>
/// Detects all connected displays via Win32 (EnumDisplayMonitors/GetMonitorInfo/GetDpiForMonitor)
/// and answers visibility/containment/clamping questions used to keep the overlay on-screen.
/// Coordinates are physical device pixels — the same coordinate space this app already uses
/// (unconverted) for OverlaySettings.X/Y/Width/Height, so no DPI rescaling is introduced here.
/// </summary>
public sealed class MonitorService : IMonitorService
{
    private readonly ILogger<MonitorService> _logger;

    public MonitorService(ILogger<MonitorService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<MonitorInfo> GetConnectedMonitors()
    {
        var monitors = new List<MonitorInfo>();

        try
        {
            NativeMethods.EnumDisplayMonitors(nint.Zero, nint.Zero, (nint hMonitor, nint _, ref NativeMethods.RECT _, nint _) =>
            {
                var info = new NativeMethods.MONITORINFOEX();
                info.cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>();

                if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
                    return true; // keep enumerating

                var dpiScaleX = 1.0;
                var dpiScaleY = 1.0;
                try
                {
                    if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY) == 0)
                    {
                        dpiScaleX = dpiX / 96.0;
                        dpiScaleY = dpiY / 96.0;
                    }
                }
                catch
                {
                    // shcore.dll unavailable (very old Windows) — assume 100% scale.
                }

                monitors.Add(new MonitorInfo
                {
                    DeviceName = info.szDevice.TrimEnd('\0'),
                    BoundsX = info.rcMonitor.Left,
                    BoundsY = info.rcMonitor.Top,
                    Width = info.rcMonitor.Right - info.rcMonitor.Left,
                    Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                    WorkingAreaX = info.rcWork.Left,
                    WorkingAreaY = info.rcWork.Top,
                    WorkingAreaWidth = info.rcWork.Right - info.rcWork.Left,
                    WorkingAreaHeight = info.rcWork.Bottom - info.rcWork.Top,
                    IsPrimary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                    DpiScaleX = dpiScaleX,
                    DpiScaleY = dpiScaleY,
                });

                return true;
            }, nint.Zero);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "monitor_enum_failed");
        }

        return monitors;
    }

    public MonitorInfo GetPrimaryMonitor()
    {
        var monitors = GetConnectedMonitors();
        return monitors.FirstOrDefault(m => m.IsPrimary)
            ?? monitors.FirstOrDefault()
            ?? new MonitorInfo
            {
                DeviceName = "\\\\.\\DISPLAY1",
                Width = 1920,
                Height = 1080,
                WorkingAreaWidth = 1920,
                WorkingAreaHeight = 1040,
                IsPrimary = true,
            };
    }

    public MonitorInfo? GetMonitorContainingPoint(double x, double y) =>
        GetConnectedMonitors().FirstOrDefault(m =>
            x >= m.BoundsX && x < m.BoundsX + m.Width &&
            y >= m.BoundsY && y < m.BoundsY + m.Height);

    public MonitorInfo? GetMonitorContainingRect(double x, double y, double width, double height)
    {
        MonitorInfo? best = null;
        var bestOverlap = 0.0;

        foreach (var monitor in GetConnectedMonitors())
        {
            var overlap = OverlapArea(x, y, width, height, monitor.BoundsX, monitor.BoundsY, monitor.Width, monitor.Height);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = monitor;
            }
        }

        return best;
    }

    public bool IsRectVisibleOnAnyMonitor(double x, double y, double width, double height) =>
        GetConnectedMonitors().Any(m =>
            OverlapArea(x, y, width, height, m.BoundsX, m.BoundsY, m.Width, m.Height) > 0);

    public double GetVisiblePercent(double x, double y, double width, double height)
    {
        var totalArea = Math.Max(1.0, width * height);
        var visible = GetConnectedMonitors()
            .Sum(m => OverlapArea(x, y, width, height, m.BoundsX, m.BoundsY, m.Width, m.Height));
        // Overlapping monitors would double-count, but real desktops never overlap;
        // clamp defensively so the ratio never exceeds 1.
        return Math.Clamp(visible / totalArea, 0.0, 1.0);
    }

    public (double X, double Y, double Width, double Height) ClampRectToNearestMonitor(
        double x, double y, double width, double height)
    {
        var monitors = GetConnectedMonitors();
        if (monitors.Count == 0) return (x, y, width, height);

        var target = GetMonitorContainingRect(x, y, width, height) ?? NearestMonitor(monitors, x, y, width, height);

        var clampedWidth = Math.Min(width, target.WorkingAreaWidth);
        var clampedHeight = Math.Min(height, target.WorkingAreaHeight);

        var minX = target.WorkingAreaX;
        var maxX = target.WorkingAreaX + target.WorkingAreaWidth - clampedWidth;
        var minY = target.WorkingAreaY;
        var maxY = target.WorkingAreaY + target.WorkingAreaHeight - clampedHeight;

        var clampedX = Math.Clamp(x, minX, Math.Max(minX, maxX));
        var clampedY = Math.Clamp(y, minY, Math.Max(minY, maxY));

        return (clampedX, clampedY, clampedWidth, clampedHeight);
    }

    public MonitorInfo? GetMonitorForWindowHandle(nint hwnd)
    {
        if (hwnd == nint.Zero) return null;

        try
        {
            var hMonitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (hMonitor == nint.Zero) return null;

            var info = new NativeMethods.MONITORINFOEX();
            info.cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>();
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref info)) return null;

            var deviceName = info.szDevice.TrimEnd('\0');
            return GetConnectedMonitors().FirstOrDefault(m => m.DeviceName == deviceName);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "monitor_for_window_lookup_failed");
            return null;
        }
    }

    private static MonitorInfo NearestMonitor(
        IReadOnlyList<MonitorInfo> monitors, double x, double y, double width, double height)
    {
        var centerX = x + width / 2.0;
        var centerY = y + height / 2.0;

        return monitors
            .OrderBy(m =>
            {
                var monitorCenterX = m.BoundsX + m.Width / 2.0;
                var monitorCenterY = m.BoundsY + m.Height / 2.0;
                var dx = centerX - monitorCenterX;
                var dy = centerY - monitorCenterY;
                return dx * dx + dy * dy;
            })
            .First();
    }

    private static double OverlapArea(
        double ax, double ay, double aw, double ah,
        double bx, double by, double bw, double bh)
    {
        var overlapWidth = Math.Max(0, Math.Min(ax + aw, bx + bw) - Math.Max(ax, bx));
        var overlapHeight = Math.Max(0, Math.Min(ay + ah, by + bh) - Math.Max(ay, by));
        return overlapWidth * overlapHeight;
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        public const uint MONITORINFOF_PRIMARY = 0x00000001;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const int MDT_EFFECTIVE_DPI = 0;

        public delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData);

        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(
            nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll")]
        public static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}
