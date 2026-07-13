using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PsGameTranslator.App.Services;

public sealed record SystemRequirementCheck(
    string Name,
    string RequiredText,
    string ActualText,
    bool IsMet,
    bool IsOptional)
{
    /// <summary>Missing and actually needed (OCR/translation won't work well) — shown as a red X.</summary>
    public bool ShowUnmetRequired => !IsMet && !IsOptional;

    /// <summary>Missing but not needed (e.g. no GPU — CPU still works) — shown as a muted dash.</summary>
    public bool ShowUnmetOptional => !IsMet && IsOptional;
}

/// <summary>
/// Detects the current machine's hardware/software and compares it against
/// what PsGameTranslator needs (RAM/disk for the OCR + translation models,
/// Python for the local OCR/translation servers) or can optionally use
/// (an NVIDIA GPU for faster PaddleOCR). Shown as a checklist in Settings so
/// the user doesn't have to guess whether their machine can run everything.
/// </summary>
public sealed class SystemRequirementsService
{
    private const long MinRamGb = 4;
    private const long RecommendedRamGb = 8;
    private const long MinFreeDiskGb = 3;

    public async Task<IReadOnlyList<SystemRequirementCheck>> CheckAllAsync()
    {
        var checks = new List<SystemRequirementCheck>
        {
            CheckRam(),
            CheckDiskSpace(),
            CheckCpuCores(),
            CheckPython(),
        };

        checks.Add(await CheckGpuAsync());
        return checks;
    }

    // ── RAM ──────────────────────────────────────────────────────────────────

    private static SystemRequirementCheck CheckRam()
    {
        var totalGb = GetTotalPhysicalMemoryGb();
        if (totalGb <= 0)
            return new SystemRequirementCheck("RAM", $"{MinRamGb}+ GB", "Tespit edilemedi", false, false);

        var met = totalGb >= MinRamGb;
        var note = totalGb >= RecommendedRamGb ? "" : totalGb >= MinRamGb ? " (asgari karsilaniyor)" : "";
        return new SystemRequirementCheck(
            "RAM", $"{MinRamGb}+ GB (onerilen {RecommendedRamGb} GB)", $"{totalGb} GB{note}", met, false);
    }

    private static long GetTotalPhysicalMemoryGb()
    {
        try
        {
            var status = new MEMORYSTATUSEX();
            status.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            if (!GlobalMemoryStatusEx(ref status)) return -1;
            return (long)(status.ullTotalPhys / (1024 * 1024 * 1024));
        }
        catch { return -1; }
    }

    [StructLayout(LayoutKind.Sequential)]
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ── Disk ─────────────────────────────────────────────────────────────────

    private static SystemRequirementCheck CheckDiskSpace()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\");
            var freeGb = drive.AvailableFreeSpace / (1024 * 1024 * 1024);
            return new SystemRequirementCheck(
                "Bos Disk Alani", $"{MinFreeDiskGb}+ GB (modeller icin)", $"{freeGb} GB",
                freeGb >= MinFreeDiskGb, false);
        }
        catch
        {
            return new SystemRequirementCheck("Bos Disk Alani", $"{MinFreeDiskGb}+ GB", "Tespit edilemedi", false, false);
        }
    }

    // ── CPU ──────────────────────────────────────────────────────────────────

    private static SystemRequirementCheck CheckCpuCores()
    {
        var cores = Environment.ProcessorCount;
        return new SystemRequirementCheck(
            "Islemci Cekirdegi", "4+ cekirdek (onerilen)", $"{cores} cekirdek", cores >= 4, true);
    }

    // ── Python ───────────────────────────────────────────────────────────────

    private static SystemRequirementCheck CheckPython()
    {
        var found = TryFindPython(out var detail);
        return new SystemRequirementCheck(
            "Python (3.9 - 3.12)", "OCR ve ceviri sunuculari icin gerekli",
            found ? detail : "Bulunamadi", found, false);
    }

    private static bool TryFindPython(out string detail)
    {
        // Mirrors PsGameTranslator.Ocr.PythonResolver's priority order closely
        // enough for a display check: project .venv first, then PATH.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var venvPython = Path.Combine(dir.FullName, ".venv", "Scripts", "python.exe");
            if (File.Exists(venvPython) && TryGetPythonVersion(venvPython, out detail))
                return true;
            dir = dir.Parent;
        }

        foreach (var candidate in new[] { "python", "py" })
        {
            if (TryGetPythonVersion(candidate, out detail))
                return true;
        }

        detail = string.Empty;
        return false;
    }

    private static bool TryGetPythonVersion(string exe, out string detail)
    {
        detail = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--version");

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            var output = (proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd()).Trim();
            proc.WaitForExit(3000);
            if (proc.ExitCode != 0) return false;
            detail = string.IsNullOrWhiteSpace(output) ? "Bulundu" : output;
            return true;
        }
        catch { return false; }
    }

    // ── GPU ──────────────────────────────────────────────────────────────────

    private static async Task<SystemRequirementCheck> CheckGpuAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--query-gpu=name,memory.total");
            psi.ArgumentList.Add("--format=csv,noheader");

            using var proc = Process.Start(psi);
            if (proc is null) throw new InvalidOperationException("nvidia-smi could not start.");

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(firstLine))
            {
                return new SystemRequirementCheck(
                    "GPU (NVIDIA/CUDA)", "Istege bagli — daha hizli OCR icin", firstLine, true, true);
            }
        }
        catch
        {
            // nvidia-smi not found/failed → no usable NVIDIA GPU.
        }

        return new SystemRequirementCheck(
            "GPU (NVIDIA/CUDA)", "Istege bagli — daha hizli OCR icin", "Bulunamadi (CPU kullanilir)", false, true);
    }
}
