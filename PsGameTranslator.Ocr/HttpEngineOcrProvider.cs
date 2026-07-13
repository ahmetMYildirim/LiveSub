using System.Diagnostics;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Ocr;

namespace PsGameTranslator.Ocr;

/// <summary>
/// OCR provider backed by the local multi-engine OCR server. One instance per
/// engine (paddle/rapid/easy) — the engine key is passed as a query parameter,
/// so all engines share the same server process and port.
/// </summary>
public sealed class HttpEngineOcrProvider : IOcrProvider
{
    private readonly HttpOcrService _service;
    private readonly IOcrServerService _server;
    private readonly string _engineKey;

    public HttpEngineOcrProvider(
        HttpOcrService service,
        IOcrServerService server,
        string name,
        OcrProviderType providerType,
        string engineKey)
    {
        _service = service;
        _server = server;
        Name = name;
        ProviderType = providerType;
        _engineKey = engineKey;
    }

    public string Name { get; }
    public OcrProviderType ProviderType { get; }
    public bool IsAvailable => _server.IsRunning;

    public async Task<OcrProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!_server.IsRunning)
        {
            var (reachable, message) = await _server.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            if (!reachable)
                return new OcrProviderHealth
                {
                    ProviderName = Name,
                    ProviderType = ProviderType,
                    IsAvailable = false,
                    State = OcrProviderState.ServerNotRunning,
                    Message = $"OCR server is not running. {message}",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ServerStatus = "Stopped",
                };
        }

        var engineState = await _service.GetEngineStateAsync(_engineKey, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        var (available, state, detail) = engineState switch
        {
            "loaded" => (true, OcrProviderState.Running, "Engine model is loaded."),
            "available" => (true, OcrProviderState.Available, "Engine installed; model loads on first use."),
            "not_installed" => (false, OcrProviderState.NotInstalled,
                $"Engine '{_engineKey}' is not installed on the OCR server."),
            null => (false, OcrProviderState.Unreachable, "Could not read engine status from /health."),
            _ => (false, OcrProviderState.Failed, engineState),
        };

        return new OcrProviderHealth
        {
            ProviderName = Name,
            ProviderType = ProviderType,
            IsAvailable = available,
            State = state,
            Message = detail,
            DurationMs = stopwatch.ElapsedMilliseconds,
            ServerStatus = "Running",
            RawHealthResult = engineState,
        };
    }

    public async Task<OcrResult> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await _service.RecognizeAsync(
                request.ImageBytes,
                engine: _engineKey,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return new OcrResult
            {
                ProviderName = Name,
                Text = result.Text,
                Confidence = result.Confidence,
                Region = result.Region,
                Lines = result.Lines,
                DurationMs = result.DurationMs > 0 ? result.DurationMs : stopwatch.ElapsedMilliseconds,
                Success = true,
                RawOutput = result.RawOutput,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new OcrResult
            {
                ProviderName = Name,
                Success = false,
                ErrorMessage = exception.Message,
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }
    }
}
