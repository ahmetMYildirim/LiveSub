using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

/// <summary>
/// Owns the lifecycle of the local OPUS-MT translation server
/// (tools/translation/start_translation_server.ps1). Checks health, starts the
/// server in the background when unreachable, and exposes status for the UI.
/// Never kills a server it did not start itself.
/// </summary>
public sealed class MachineTranslationServerManager : IDisposable
{
    private static readonly string DebugDirectory =
        Path.Combine(AppContext.BaseDirectory, "debug");
    private static readonly string StateFilePath =
        Path.Combine(DebugDirectory, "translation_server_state.json");
    private static readonly string StdoutLogPath =
        Path.Combine(DebugDirectory, "translation_server_stdout.log");
    private static readonly string StderrLogPath =
        Path.Combine(DebugDirectory, "translation_server_stderr.log");

    private readonly TranslationSettings _settings;
    private readonly ILogger<MachineTranslationServerManager> _logger;
    private readonly HttpClient _health;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _stateGate = new();

    private Process? _process;

    public event Action? Changed;

    public MachineTranslationServerManager(
        TranslationSettings settings,
        ILogger<MachineTranslationServerManager> logger)
    {
        _settings = settings;
        _logger = logger;
        _health = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }

    // ── Public state ──────────────────────────────────────────────────────────────

    public MachineTranslationServerState State { get; private set; } = MachineTranslationServerState.NotChecked;
    public DateTimeOffset? LastHealthCheckAt { get; private set; }
    public string LastHealthError { get; private set; } = string.Empty;
    public DateTimeOffset? LastStartAttemptAt { get; private set; }
    public string LastStartError { get; private set; } = string.Empty;
    public int? ProcessId => _process is { HasExited: false } ? _process.Id : null;
    public bool StartedByApp => _process is { HasExited: false };
    public string ServerBaseUrl => _settings.MachineTranslationBaseUrl.TrimEnd('/');

    // ── Startup / toggle entry point ────────────────────────────────────────────

    /// <summary>
    /// Call at app startup and whenever Enable Translation is turned on.
    /// Only acts when EnableTranslation is on and the provider actually needs
    /// the machine translation server (MachineTranslation or the hybrid mode).
    /// </summary>
    public async Task EnsureRunningIfEnabledAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.EnableTranslation)
            return;

        if (!_settings.AutoStartOpusServer)
            return;

        if (!ShouldAutoStartForCurrentSelection())
            return;

        await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool ShouldAutoStartForCurrentSelection()
    {
        if (!_settings.StartOpusOnlyWhenSelectedOrFallback)
            return true;

        if (_settings.ProviderChainMode == TranslationProviderChainMode.LocalOnly)
            return true;

        if (_settings.ProviderType is TranslationProviderType.OpusMT or
            TranslationProviderType.MachineTranslation or
            TranslationProviderType.HybridMachineThenOllama)
            return true;

        if (_settings.ProviderChainMode is TranslationProviderChainMode.ProviderChain or
            TranslationProviderChainMode.HybridBalanced)
            return _settings.EnableTranslationProviderFallback;

        return false;
    }

    /// <summary>
    /// Used by "Test Machine Translation" — always attempts to ensure the server
    /// is reachable before the caller runs a real translate call, regardless of
    /// the EnableTranslation/provider gate.
    /// </summary>
    public Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default) =>
        EnsureRunningCoreAsync(cancellationToken);

    // Hot-path guard: EnsureRunningAsync is called for every translated subtitle.
    // When the server was healthy moments ago, skip the HTTP /health round-trip.
    private static readonly TimeSpan HealthyTtl = TimeSpan.FromSeconds(5);
    private DateTimeOffset _lastHealthyAt = DateTimeOffset.MinValue;

    private async Task<bool> EnsureRunningCoreAsync(CancellationToken cancellationToken)
    {
        if (State is MachineTranslationServerState.Running or MachineTranslationServerState.RunningExternal &&
            DateTimeOffset.UtcNow - _lastHealthyAt < HealthyTtl)
            return true;

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is MachineTranslationServerState.Running or MachineTranslationServerState.RunningExternal &&
                DateTimeOffset.UtcNow - _lastHealthyAt < HealthyTtl)
                return true;

            var (healthy, _) = await CheckHealthOnceAsync(cancellationToken).ConfigureAwait(false);
            if (healthy)
            {
                SetState(StartedByApp ? MachineTranslationServerState.Running
                                      : MachineTranslationServerState.RunningExternal);
                return true;
            }

            if (!_settings.AutoStartMachineTranslationServer)
            {
                SetState(MachineTranslationServerState.Unreachable);
                return false;
            }

            return await StartAndWaitCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    // ── Explicit user actions (UI buttons) ─────────────────────────────────────

    /// <summary>"Start Translation Server" button — forces a start attempt.</summary>
    public async Task<bool> StartServerAsync(CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (healthy, _) = await CheckHealthOnceAsync(cancellationToken).ConfigureAwait(false);
            if (healthy)
            {
                SetState(StartedByApp ? MachineTranslationServerState.Running
                                      : MachineTranslationServerState.RunningExternal);
                return true;
            }

            return await StartAndWaitCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>"Stop Translation Server" button — only stops a process this app started.</summary>
    public Task StopServerAsync()
    {
        if (_process is null || _process.HasExited)
        {
            _logger.LogInformation("translation_server_stop_skipped - not started by this app");
            if (State is MachineTranslationServerState.Running or MachineTranslationServerState.RunningExternal)
                SetState(MachineTranslationServerState.RunningExternal);
            return Task.CompletedTask;
        }

        try
        {
            _logger.LogInformation("translation_server_stopping - pid={Pid}", _process.Id);
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5_000);
            _logger.LogInformation("translation_server_stopped");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "translation_server_stop_error");
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }

        SetState(MachineTranslationServerState.Stopped);
        return Task.CompletedTask;
    }

    /// <summary>"Check Translation Server" button — single health probe, no start.</summary>
    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var (healthy, _) = await CheckHealthOnceAsync(cancellationToken).ConfigureAwait(false);
        if (healthy)
        {
            SetState(StartedByApp ? MachineTranslationServerState.Running
                                  : MachineTranslationServerState.RunningExternal);
        }
        else if (State != MachineTranslationServerState.Failed)
        {
            SetState(MachineTranslationServerState.Unreachable);
        }
        return healthy;
    }

    // ── Health check ─────────────────────────────────────────────────────────────

    private async Task<(bool Healthy, string Message)> CheckHealthOnceAsync(CancellationToken cancellationToken)
    {
        var url = ServerBaseUrl + "/health";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            using var response = await _health.GetAsync(url, cts.Token).ConfigureAwait(false);
            LastHealthCheckAt = DateTimeOffset.Now;

            if (!response.IsSuccessStatusCode)
            {
                LastHealthError = $"HTTP {(int)response.StatusCode}";
                SaveStateSnapshot();
                return (false, LastHealthError);
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var modelLoaded =
                (root.TryGetProperty("modelLoaded", out var m1) && m1.ValueKind == JsonValueKind.True) ||
                (root.TryGetProperty("model_loaded", out var m2) && m2.ValueKind == JsonValueKind.True);

            if (!modelLoaded)
            {
                LastHealthError = "Model is not loaded yet.";
                SaveStateSnapshot();
                return (false, LastHealthError);
            }

            LastHealthError = string.Empty;
            _lastHealthyAt = DateTimeOffset.UtcNow;
            SaveStateSnapshot();
            return (true, "OK");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LastHealthCheckAt = DateTimeOffset.Now;
            LastHealthError = exception.Message;
            SaveStateSnapshot();
            return (false, exception.Message);
        }
    }

    // ── Process start ────────────────────────────────────────────────────────────

    private async Task<bool> StartAndWaitCoreAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
        {
            // Already starting/running under our ownership — just wait on health.
            SetState(MachineTranslationServerState.Starting);
            return await PollHealthUntilReadyAsync(cancellationToken).ConfigureAwait(false);
        }

        LastStartAttemptAt = DateTimeOffset.Now;
        LastStartError = string.Empty;

        (string ScriptPath, string WorkingDirectory) resolved;
        try
        {
            resolved = ResolveScript();
        }
        catch (Exception exception)
        {
            LastStartError = exception.Message;
            _logger.LogError(exception, "translation_server_script_not_found");
            SetState(MachineTranslationServerState.Failed);
            return false;
        }

        var shell = ResolveShellExecutable();

        var psi = new ProcessStartInfo
        {
            FileName = shell,
            WorkingDirectory = resolved.WorkingDirectory,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(resolved.ScriptPath);
        if (!string.IsNullOrWhiteSpace(_settings.MachineTranslationModel))
        {
            var modelValue = ResolveModelPath(_settings.MachineTranslationModel);
            psi.ArgumentList.Add("-Model");
            psi.ArgumentList.Add(modelValue);
            psi.Environment["TRANSLATION_MODEL"] = modelValue;
        }

        var showConsole = _settings.ShowTranslationServerConsole;
        psi.CreateNoWindow = !showConsole;
        psi.WindowStyle = showConsole ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden;
        // Only redirect output when the console window is hidden — a visible
        // console already shows the server's own stdout/stderr.
        psi.RedirectStandardOutput = !showConsole;
        psi.RedirectStandardError = !showConsole;

        _logger.LogInformation(
            "translation_server_starting - shell={Shell}, script={Script}, workingDir={WorkingDir}, showConsole={ShowConsole}",
            shell, resolved.ScriptPath, resolved.WorkingDirectory, showConsole);

        SetState(MachineTranslationServerState.Starting);

        try
        {
            Directory.CreateDirectory(DebugDirectory);
            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
            _process = process;
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) =>
                _logger.LogInformation(
                    "translation_server_process_exited - code={Code}", SafeExitCode(_process));

            if (!showConsole)
            {
                AttachLogRedirection(_process);
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
        }
        catch (Exception exception)
        {
            LastStartError = exception.Message;
            _logger.LogError(exception, "translation_server_start_failed");
            SetState(MachineTranslationServerState.Failed);
            return false;
        }

        return await PollHealthUntilReadyAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> PollHealthUntilReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(
            Math.Max(1000, _settings.TranslationServerStartupTimeoutMs));
        var interval = TimeSpan.FromMilliseconds(
            Math.Max(200, _settings.TranslationServerHealthRetryIntervalMs));

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_process is { HasExited: true })
            {
                LastStartError = $"Server process exited prematurely (code {SafeExitCode(_process)}).";
                _logger.LogWarning("translation_server_exited_during_startup - {Error}", LastStartError);
                SetState(MachineTranslationServerState.Failed);
                return false;
            }

            var (healthy, _) = await CheckHealthOnceAsync(cancellationToken).ConfigureAwait(false);
            if (healthy)
            {
                _logger.LogInformation("translation_server_ready - url={Url}", ServerBaseUrl);
                SetState(MachineTranslationServerState.Running);
                return true;
            }

            try { await Task.Delay(interval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }

        LastStartError = $"Server did not become ready within {_settings.TranslationServerStartupTimeoutMs} ms.";
        _logger.LogWarning("translation_server_startup_timeout - {Error}", LastStartError);
        SetState(MachineTranslationServerState.Failed);
        return false;
    }

    // ── Script / shell resolution ────────────────────────────────────────────────

    /// <summary>
    /// Walks up from the app's output directory to find the real repository
    /// root containing the configured script. The script's own $PSScriptRoot
    /// logic assumes it lives at repoRoot/tools/translation, so it must be
    /// launched from its true source location — not a copied build artifact.
    /// </summary>
    private static string ResolveModelPath(string modelSetting)
    {
        // "opus-mt-finetuned" sentinel → absolute path under the app's output directory.
        if (modelSetting.Equals(TranslationModelCatalog.FineTunedModelSentinel, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(AppContext.BaseDirectory, "models", "opus-mt-finetuned");
        }
        return modelSetting;
    }

    private (string ScriptPath, string WorkingDirectory) ResolveScript()
    {
        var configured = string.IsNullOrWhiteSpace(_settings.TranslationServerScriptPath)
            ? "tools/translation/start_translation_server.ps1"
            : _settings.TranslationServerScriptPath.Trim();

        var normalized = configured.Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalized))
        {
            if (!File.Exists(normalized))
                throw new FileNotFoundException($"Translation server script not found: {normalized}");

            // tools/translation/<script> -> repo root is two levels up.
            var scriptDir = new DirectoryInfo(Path.GetDirectoryName(normalized)!);
            var workingDir = scriptDir.Parent?.Parent?.FullName ?? scriptDir.FullName;
            return (normalized, workingDir);
        }

        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            var candidate = Path.Combine(probe.FullName, normalized);
            if (File.Exists(candidate))
                return (Path.GetFullPath(candidate), probe.FullName);
            probe = probe.Parent;
        }

        throw new FileNotFoundException(
            $"Translation server script not found while walking up from {AppContext.BaseDirectory}: {normalized}");
    }

    private string ResolveShellExecutable()
    {
        if (CanRun("pwsh")) return "pwsh";
        return "powershell.exe";
    }

    private static bool CanRun(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "-NoProfile", "-Command", "$PSVersionTable.PSVersion.Major" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit(3_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void AttachLogRedirection(Process process)
    {
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null) return;
            AppendLogLine(StdoutLogPath, args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null) return;
            AppendLogLine(StderrLogPath, args.Data);
        };
    }

    private static void AppendLogLine(string path, string line)
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Best-effort logging only — never let log I/O crash the app.
        }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────────

    private void SetState(MachineTranslationServerState newState)
    {
        lock (_stateGate)
        {
            if (State == newState)
            {
                SaveStateSnapshot();
                Changed?.Invoke();
                return;
            }
            State = newState;
        }

        _logger.LogInformation("translation_server_state_changed - state={State}", newState);
        SaveStateSnapshot();
        Changed?.Invoke();
    }

    private void SaveStateSnapshot()
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            var snapshot = new
            {
                State = State.ToString(),
                ServerBaseUrl,
                LastHealthCheckAt,
                LastHealthError,
                LastStartAttemptAt,
                LastStartError,
                ProcessId,
                StartedByApp,
                ShowConsole = _settings.ShowTranslationServerConsole,
                AutoStart = _settings.AutoStartOpusServer,
                _settings.StartOpusOnlyWhenSelectedOrFallback,
                ShouldAutoStartNow = ShouldAutoStartForCurrentSelection(),
                _settings.ProviderType,
                _settings.ProviderChainMode,
                _settings.EnableTranslationProviderFallback,
            };
            File.WriteAllText(
                StateFilePath,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not save translation server state diagnostics");
        }
    }

    public void Dispose()
    {
        _health.Dispose();
        if (_process is { HasExited: false })
        {
            // Best-effort: do not block app shutdown waiting on the server.
            try { _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }
        _process?.Dispose();
        _startGate.Dispose();
    }
}
