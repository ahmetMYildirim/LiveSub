using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Ocr;

namespace PsGameTranslator.Ocr;

public sealed class OcrServerService : IOcrServerService, IDisposable
{
    private const string Host = "127.0.0.1";
    private const int    Port = 8765;

    private static readonly string ScriptPath =
        Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "tools", "ocr", "ocr_server.py"));

    private readonly IOcrSettings _settings;
    private readonly OcrEngineSettings _engineSettings;
    private readonly ILogger<OcrServerService> _logger;
    private readonly HttpClient _health;

    private Process? _process;
    private readonly System.Text.StringBuilder _stderrBuffer = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public OcrServerService(IOcrSettings settings, OcrEngineSettings engineSettings, ILogger<OcrServerService> logger)
    {
        _settings = settings;
        _engineSettings = engineSettings;
        _logger   = logger;
        _health   = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }

    public bool IsRunning =>
        State is OcrProviderState.Running or OcrProviderState.RunningExternal;

    public OcrProviderState State { get; private set; } = OcrProviderState.Stopped;
    
    public bool IsRunningExternally => State == OcrProviderState.RunningExternal;

    public string ServerBaseUrl => $"http://{Host}:{Port}";

    public event Action<OcrProviderState, string>? StateChanged;

    /// <summary>
    /// Provider types whose recognition is served by the local Python OCR server,
    /// i.e. selecting them should trigger an automatic server start.
    /// </summary>
    public static bool IsServerBackedProvider(OcrProviderType providerType) =>
        providerType is OcrProviderType.PaddleOCR
                     or OcrProviderType.RapidOCR
                     or OcrProviderType.EasyOCR;

    private void SetState(OcrProviderState newState, string error = "")
    {
        State = newState;
        SaveDiagnostics(error);
        StateChanged?.Invoke(newState, error);
    }

    /// <summary>
    /// Starts the server if it is not already running. Unlike <see cref="StartAsync"/>,
    /// this never throws: failures are captured in <see cref="State"/> and the returned message.
    /// Concurrent callers (app startup + provider selection) are serialized.
    /// </summary>
    public async Task<(bool Success, string Message)> EnsureRunningAsync(CancellationToken ct = default)
    {
        if (IsRunning)
            return (true, "OCR server is already running.");

        await _startGate.WaitAsync(ct);
        try
        {
            if (IsRunning)
                return (true, "OCR server is already running.");

            await StartAsync(ct);
            return (true, $"OCR server running at {ServerBaseUrl}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OCR server auto-start failed");
            return (false, ex.Message);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private void SaveDiagnostics(string error = "")
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "debug");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "last_ocr_server_state.json");

            string stdout = string.Empty;
            string stderr = string.Empty;
            lock (_stderrBuffer) stderr = _stderrBuffer.ToString();

            var diag = new
            {
                timestamp = DateTimeOffset.Now,
                provider = "PaddleOCR",
                serverState = State.ToString(),
                command = _process?.StartInfo.FileName ?? string.Empty,
                arguments = _process?.StartInfo.Arguments ?? string.Empty,
                workingDirectory = _process?.StartInfo.WorkingDirectory ?? string.Empty,
                port = Port,
                healthUrl = ServerBaseUrl + "/health",
                startedByApp = State == OcrProviderState.Running,
                runningExternal = State == OcrProviderState.RunningExternal,
                stdoutTail = stdout,
                stderrTail = stderr,
                lastError = error
            };

            var json = JsonSerializer.Serialize(diag, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(file, json, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save OCR server diagnostics");
        }
    }

    // ── Start ────────────────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning)
        {
            _logger.LogInformation("OCR server is already running (State {State})", State);
            return;
        }

        // Check if running externally first
        var (externalSuccess, _) = await TestConnectionAsync(ct);
        if (externalSuccess)
        {
            _logger.LogInformation("OCR server is already running externally at {Url}", ServerBaseUrl);
            SetState(OcrProviderState.RunningExternal);
            return;
        }

        SetState(OcrProviderState.Starting);

        if (!File.Exists(ScriptPath))
            throw new OcrSetupException(
                $"OCR server script not found.\n  Script: {ScriptPath}\n\n" +
                "Make sure the project was built so 'tools/ocr/ocr_server.py' is in the output directory.");

        var pythonExe = ResolveOrDetectPython();

        var psi = new ProcessStartInfo
        {
            FileName        = pythonExe,
            UseShellExecute = false,
            CreateNoWindow  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        psi.ArgumentList.Add(ScriptPath);
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add(Host);
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(Port.ToString());
        psi.ArgumentList.Add("--device");
        psi.ArgumentList.Add(_engineSettings.Device switch
        {
            OcrDeviceMode.Cpu => "cpu",
            OcrDeviceMode.Gpu => "gpu",
            _ => "auto",
        });

        _logger.LogInformation(
            "Starting OCR server — python={Python}  script={Script}  url={Url}",
            pythonExe, ScriptPath, ServerBaseUrl);

        _stderrBuffer.Clear();
        _process = Process.Start(psi)
            ?? throw new OcrSetupException("Failed to start the OCR server process.");

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            _logger.LogInformation("OCR server process exited (code {Code})", _process.ExitCode);
            SetState(OcrProviderState.Stopped);
        };

        // Drain stderr continuously so it's available when the process dies.
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _logger.LogDebug("[ocr-server stderr] {Line}", e.Data);
            lock (_stderrBuffer) _stderrBuffer.AppendLine(e.Data);
        };
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logger.LogDebug("[ocr-server stdout] {Line}", e.Data);
        };
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();

        try
        {
            await WaitUntilReadyAsync(ct);
            SetState(OcrProviderState.Running);
            _logger.LogInformation(
                "OCR server ready at {Url} (PID {Pid})", ServerBaseUrl, _process.Id);
        }
        catch (Exception ex)
        {
            SetState(OcrProviderState.Failed, ex.Message);
            throw;
        }
    }

    // ── Stop ─────────────────────────────────────────────────────────────────────

    public Task StopAsync()
    {
        if (_process is null || _process.HasExited)
        {
            _logger.LogInformation("OCR server is not running");
            return Task.CompletedTask;
        }

        try
        {
            if (_process is not null)
            {
                _logger.LogInformation("Stopping OCR server (PID {Pid})", _process.Id);
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }
            SetState(OcrProviderState.Stopped);
            _logger.LogInformation("OCR server stopped");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while stopping OCR server");
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }

        return Task.CompletedTask;
    }

    // ── Test ─────────────────────────────────────────────────────────────────────

    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _health.GetAsync(ServerBaseUrl + "/health", ct);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (true, $"OK — {body}");
            }
            return (false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (OperationCanceledException)
        {
            return (false, "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Error: {ex.Message}");
        }
    }

    // ── Readiness polling ─────────────────────────────────────────────────────────

    private async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var delay    = TimeSpan.FromMilliseconds(500);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_process is { HasExited: true })
            {
                string stderr;
                lock (_stderrBuffer) stderr = _stderrBuffer.ToString().Trim();
                var detail = string.IsNullOrEmpty(stderr) ? string.Empty : $"\n\nPython error output:\n{stderr}";
                throw new OcrRuntimeException(
                    $"OCR server process exited prematurely (code {_process.ExitCode}).{detail}");
            }

            try
            {
                using var cts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));

                var resp = await _health.GetAsync(ServerBaseUrl + "/health", cts.Token);
                if (resp.IsSuccessStatusCode)
                    return;
            }
            catch
            {
                // Not ready yet — keep polling
            }

            await Task.Delay(delay, ct);
        }

        throw new OcrRuntimeException(
            $"OCR server did not become ready within 30 seconds at {ServerBaseUrl}.");
    }

    // ── Python resolution ──────────────────────────────────────────────────────────

    private string ResolveOrDetectPython() =>
        PythonResolver.Resolve(_settings.PythonExePath);

    // ── IDisposable ────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _process?.Dispose();
        _health.Dispose();
    }
}
