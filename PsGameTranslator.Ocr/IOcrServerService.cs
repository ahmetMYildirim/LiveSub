using PsGameTranslator.Core.Ocr;

namespace PsGameTranslator.Ocr;

public interface IOcrServerService
{
    bool IsRunning { get; }
    OcrProviderState State { get; }
    bool IsRunningExternally { get; }

    string ServerBaseUrl { get; }

    /// <summary>Raised whenever the server lifecycle state changes (state, last error).</summary>
    event Action<OcrProviderState, string>? StateChanged;

    Task StartAsync(CancellationToken ct = default);

    /// <summary>Starts the server if not running; never throws — returns success + message.</summary>
    Task<(bool Success, string Message)> EnsureRunningAsync(CancellationToken ct = default);

    Task StopAsync();

    Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default);
}
