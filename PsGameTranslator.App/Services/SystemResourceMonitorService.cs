using System.Runtime.InteropServices;
using System.Windows.Threading;
using PsGameTranslator.App.ViewModels;

namespace PsGameTranslator.App.Services;

// Samples system-wide CPU and RAM usage for the status bar. Uses raw kernel32
// calls (GetSystemTimes / GlobalMemoryStatusEx) instead of PerformanceCounter
// so no extra NuGet package or perf-counter category registration is needed.
public sealed class SystemResourceMonitorService : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _timer;
    private (long Idle, long Kernel, long User)? _lastSample;
    private double _cpuUsagePercent;
    private double _usedRamGb;
    private double _totalRamGb;

    public SystemResourceMonitorService()
    {
        Sample();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += (_, _) => Sample();
        _timer.Start();
    }

    public string CpuUsageText => $"CPU {_cpuUsagePercent:F0}%";
    public string RamUsageText => $"RAM {_usedRamGb:F1} / {_totalRamGb:F0} GB";

    private void Sample()
    {
        UpdateCpuUsage();
        UpdateMemoryUsage();
        RaisePropertyChanged(nameof(CpuUsageText));
        RaisePropertyChanged(nameof(RamUsageText));
    }

    private void UpdateCpuUsage()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
            return;

        var idle = ToInt64(idleTime);
        var kernel = ToInt64(kernelTime);
        var user = ToInt64(userTime);

        if (_lastSample is { } previous)
        {
            var idleDelta = idle - previous.Idle;
            var totalDelta = (kernel - previous.Kernel) + (user - previous.User);
            if (totalDelta > 0)
                _cpuUsagePercent = Math.Clamp(100.0 * (1.0 - (double)idleDelta / totalDelta), 0, 100);
        }

        _lastSample = (idle, kernel, user);
    }

    private void UpdateMemoryUsage()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
            return;

        _totalRamGb = status.ullTotalPhys / 1024d / 1024d / 1024d;
        _usedRamGb = (status.ullTotalPhys - status.ullAvailPhys) / 1024d / 1024d / 1024d;
    }

    public void Dispose() => _timer.Stop();

    private static long ToInt64(FILETIME time) =>
        ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
