using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PsGameTranslator.Ocr;

/// <summary>
/// Runs Python/pip operations against the project's resolved Python environment
/// so users never need a terminal: module detection, package install/uninstall,
/// and arbitrary short Python snippets (e.g. HuggingFace model downloads).
/// </summary>
public sealed class PythonEnvironmentService
{
    private readonly IOcrSettings _settings;
    private readonly ILogger<PythonEnvironmentService> _logger;

    public PythonEnvironmentService(IOcrSettings settings, ILogger<PythonEnvironmentService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Resolved python.exe path, or null when no usable Python exists.</summary>
    public string? TryResolvePython()
    {
        try { return PythonResolver.Resolve(_settings.PythonExePath); }
        catch (OcrSetupException) { return null; }
    }

    public async Task<bool> IsModuleInstalledAsync(string moduleName, CancellationToken ct = default)
    {
        var python = TryResolvePython();
        if (python is null) return false;

        var (exitCode, _, _) = await RunAsync(python,
        [
            "-c",
            $"import importlib.util,sys; sys.exit(0 if importlib.util.find_spec('{moduleName}') else 1)",
        ], null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return exitCode == 0;
    }

    /// <summary>pip install; progress lines are streamed to <paramref name="progress"/>.</summary>
    public async Task<(bool Success, string Message)> InstallPackagesAsync(
        IReadOnlyList<string> packages,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var python = TryResolvePython();
        if (python is null)
            return (false, "Python not found. Install Python 3.11 or set the path in Settings.");

        var args = new List<string> { "-m", "pip", "install", "--no-input" };
        args.AddRange(packages);

        progress?.Report($"pip install {string.Join(' ', packages)} …");
        var (exitCode, stdout, stderr) = await RunAsync(
            python, args, progress, TimeSpan.FromMinutes(20), ct).ConfigureAwait(false);

        if (exitCode == 0)
            return (true, "Installed successfully.");

        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        _logger.LogWarning("pip install failed ({ExitCode}): {Detail}", exitCode, Truncate(detail, 500));
        return (false, $"pip install failed (exit {exitCode}): {Truncate(detail, 400)}");
    }

    public async Task<(bool Success, string Message)> UninstallPackagesAsync(
        IReadOnlyList<string> packages,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var python = TryResolvePython();
        if (python is null)
            return (false, "Python not found.");

        var args = new List<string> { "-m", "pip", "uninstall", "-y" };
        args.AddRange(packages);

        var (exitCode, stdout, stderr) = await RunAsync(
            python, args, progress, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        return exitCode == 0
            ? (true, "Removed.")
            : (false, $"pip uninstall failed: {Truncate(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr, 400)}");
    }

    /// <summary>Runs a short Python snippet (-c) in the environment.</summary>
    public async Task<(bool Success, string Output)> RunSnippetAsync(
        string code,
        IProgress<string>? progress = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var python = TryResolvePython();
        if (python is null)
            return (false, "Python not found. Install Python 3.11 or set the path in Settings.");

        var (exitCode, stdout, stderr) = await RunAsync(
            python, ["-c", code], progress, timeout ?? TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
        var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        return (exitCode == 0, output.Trim());
    }

    /// <summary>
    /// Runs a Python script file with arguments, streaming every stdout/stderr
    /// line to <paramref name="progress"/> as it arrives (not just at the end).
    /// No internal timeout — a long-running job (e.g. model fine-tuning) is
    /// bounded only by the caller's CancellationToken, so cancelling it is a
    /// clean "Stop" button rather than an arbitrary deadline.
    /// </summary>
    public async Task<int> RunScriptAsync(
        string scriptPath,
        IReadOnlyList<string> args,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var python = TryResolvePython();
        if (python is null)
        {
            progress?.Report("Python not found. Install Python 3.11 or set the path in Settings.");
            return -1;
        }

        var fullArgs = new List<string> { scriptPath };
        fullArgs.AddRange(args);
        var (exitCode, _, _) = await RunAsync(
            python, fullArgs, progress, Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        return exitCode;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string exe,
        IReadOnlyList<string> args,
        IProgress<string>? progress,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            progress?.Report(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            progress?.Report(e.Data);
        };

        if (!process.Start())
            return (-1, string.Empty, "Failed to start process.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-2, stdout.ToString(), ct.IsCancellationRequested ? "Cancelled." : "Timed out.");
        }

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
