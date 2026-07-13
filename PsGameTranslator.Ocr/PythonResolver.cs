using System.Diagnostics;

namespace PsGameTranslator.Ocr;

/// <summary>
/// Locates a Python executable that is compatible with PaddleOCR (requires Python 3.9–3.12).
/// PaddleOCR does not currently publish wheels for Python 3.13+, so this resolver
/// prefers well-known 3.11 / 3.12 / 3.10 / 3.9 installations over whatever "python"
/// resolves to on PATH (which may be a newer, incompatible version).
/// </summary>
internal static class PythonResolver
{
    // Ordered from most-preferred to least-preferred minor version.
    private static readonly string[] PreferredVersionFolders =
        ["Python311", "Python312", "Python310", "Python39"];

    /// <summary>
    /// Returns the Python executable to use.
    /// 1. Configured path (from Settings tab) — if provided and functional.
    /// 2. Nearest .venv\Scripts\python.exe walking up from the output directory.
    /// 3. Well-known local-AppData Python installations (3.11, 3.12, 3.10, 3.9).
    /// 4. "python" / "python3" / "py" on PATH as a last resort.
    /// </summary>
    public static string Resolve(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var path = configuredPath.Trim();
            if (CanRun(path)) return path;
            throw new OcrSetupException(
                $"Configured Python path is not usable.\n  Python: {path}\n\n" +
                "Check the path in the Settings tab and ensure the executable exists.");
        }

        // .venv in the output tree
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var venv = Path.Combine(dir.FullName, ".venv", "Scripts", "python.exe");
            if (CanRun(venv)) return venv;
            dir = dir.Parent;
        }

        // Well-known per-user Python installations (PaddleOCR-compatible versions first).
        // This avoids accidentally picking up Python 3.13+ from PATH which PaddleOCR
        // does not yet support.
        var localApp = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        foreach (var folder in PreferredVersionFolders)
        {
            var p = Path.Combine(localApp, "Programs", "Python", folder, "python.exe");
            if (File.Exists(p) && CanRun(p))
                return p;
        }

        // Also check per-machine installs under Program Files
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            foreach (var folder in PreferredVersionFolders)
            {
                var p = Path.Combine(root, "Python", folder, "python.exe");
                if (File.Exists(p) && CanRun(p))
                    return p;
            }
        }

        // Generic PATH fallback — may resolve to an incompatible version
        foreach (var candidate in new[] { "python", "python3", "py" })
        {
            if (CanRun(candidate))
                return candidate;
        }

        throw new OcrSetupException(
            "Python executable not found.\n" +
            "PaddleOCR requires Python 3.9 – 3.12. " +
            "Install Python 3.11 from https://www.python.org or set an explicit path in the Settings tab.");
    }

    internal static bool CanRun(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = exe,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            psi.ArgumentList.Add("--version");

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(3_000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }
}
