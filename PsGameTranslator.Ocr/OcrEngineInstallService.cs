using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Ocr;

namespace PsGameTranslator.Ocr;

public enum OcrEngineInstallState
{
    Unknown,
    NotInstalled,
    Installed,
    Downloading,
    Failed,
    /// <summary>Engine ships with the OS / app — nothing to install (WindowsOCR, MockOCR).</summary>
    BuiltIn,
    /// <summary>Engine is not supported yet (OneOCR).</summary>
    NotSupported,
}

public sealed record OcrEngineDescriptor(
    OcrProviderType ProviderType,
    string DisplayName,
    string? PythonModule,
    IReadOnlyList<string> PipPackages,
    bool RequiresServer);

/// <summary>
/// Install/uninstall/status management for OCR engines so the user never needs
/// a terminal. Python-based engines are installed into the project environment
/// via pip; state transitions are pushed through <see cref="StateChanged"/>.
/// </summary>
public sealed class OcrEngineInstallService
{
    public static readonly IReadOnlyList<OcrEngineDescriptor> Engines =
    [
        new(OcrProviderType.WindowsOCR, "Windows OCR", null, [], RequiresServer: false),
        new(OcrProviderType.PaddleOCR, "PaddleOCR", "paddleocr",
            ["paddleocr==3.7.0", "paddlepaddle==3.3.1"], RequiresServer: true),
        new(OcrProviderType.RapidOCR, "RapidOCR", "rapidocr_onnxruntime",
            ["rapidocr-onnxruntime>=1.3.0"], RequiresServer: true),
        new(OcrProviderType.EasyOCR, "EasyOCR", "easyocr",
            ["easyocr"], RequiresServer: true),
        new(OcrProviderType.OneOCR, "OneOCR", null, [], RequiresServer: false),
        new(OcrProviderType.MockOCR, "MockOCR", null, [], RequiresServer: false),
    ];

    private readonly PythonEnvironmentService _python;
    private readonly ILogger<OcrEngineInstallService> _logger;
    private readonly Dictionary<OcrProviderType, OcrEngineInstallState> _states = new();
    private readonly object _gate = new();

    /// <summary>(engine, newState, detail) — raised on background threads.</summary>
    public event Action<OcrProviderType, OcrEngineInstallState, string>? StateChanged;

    public OcrEngineInstallService(
        PythonEnvironmentService python,
        ILogger<OcrEngineInstallService> logger)
    {
        _python = python;
        _logger = logger;
    }

    public static OcrEngineDescriptor? Describe(OcrProviderType providerType) =>
        Engines.FirstOrDefault(engine => engine.ProviderType == providerType);

    public OcrEngineInstallState GetCachedState(OcrProviderType providerType)
    {
        lock (_gate)
            return _states.TryGetValue(providerType, out var state) ? state : OcrEngineInstallState.Unknown;
    }

    public async Task<OcrEngineInstallState> RefreshStateAsync(
        OcrProviderType providerType, CancellationToken ct = default)
    {
        var descriptor = Describe(providerType);
        OcrEngineInstallState state;
        if (descriptor is null)
            state = OcrEngineInstallState.Unknown;
        else if (providerType == OcrProviderType.OneOCR)
            state = OcrEngineInstallState.NotSupported;
        else if (descriptor.PythonModule is null)
            state = OcrEngineInstallState.BuiltIn;
        else if (GetCachedState(providerType) == OcrEngineInstallState.Downloading)
            state = OcrEngineInstallState.Downloading; // don't clobber an active install
        else
            state = await _python.IsModuleInstalledAsync(descriptor.PythonModule, ct).ConfigureAwait(false)
                ? OcrEngineInstallState.Installed
                : OcrEngineInstallState.NotInstalled;

        SetState(providerType, state, string.Empty);
        return state;
    }

    public async Task<IReadOnlyDictionary<OcrProviderType, OcrEngineInstallState>> RefreshAllAsync(
        CancellationToken ct = default)
    {
        foreach (var engine in Engines)
            await RefreshStateAsync(engine.ProviderType, ct).ConfigureAwait(false);
        lock (_gate) return new Dictionary<OcrProviderType, OcrEngineInstallState>(_states);
    }

    public async Task<(bool Success, string Message)> InstallAsync(
        OcrProviderType providerType,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var descriptor = Describe(providerType);
        if (descriptor?.PythonModule is null)
            return (false, $"{providerType} has nothing to install.");

        if (GetCachedState(providerType) == OcrEngineInstallState.Downloading)
            return (false, $"{descriptor.DisplayName} install is already in progress.");

        SetState(providerType, OcrEngineInstallState.Downloading, "Installing…");
        _logger.LogInformation("ocr_engine_install_started - {Engine}", descriptor.DisplayName);

        var (success, message) = await _python
            .InstallPackagesAsync(descriptor.PipPackages, progress, ct)
            .ConfigureAwait(false);

        if (success)
        {
            SetState(providerType, OcrEngineInstallState.Installed, message);
            _logger.LogInformation("ocr_engine_install_completed - {Engine}", descriptor.DisplayName);
        }
        else
        {
            SetState(providerType, OcrEngineInstallState.Failed, message);
            _logger.LogWarning("ocr_engine_install_failed - {Engine}: {Message}", descriptor.DisplayName, message);
        }
        return (success, message);
    }

    public async Task<(bool Success, string Message)> UninstallAsync(
        OcrProviderType providerType,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var descriptor = Describe(providerType);
        if (descriptor?.PythonModule is null)
            return (false, $"{providerType} has nothing to remove.");

        // Uninstall only the top-level packages, without version pins.
        var packageNames = descriptor.PipPackages
            .Select(package => package.Split(["==", ">="], StringSplitOptions.None)[0])
            .ToArray();
        var (success, message) = await _python
            .UninstallPackagesAsync(packageNames, progress, ct)
            .ConfigureAwait(false);

        SetState(providerType,
            success ? OcrEngineInstallState.NotInstalled : OcrEngineInstallState.Failed, message);
        return (success, message);
    }

    private void SetState(OcrProviderType providerType, OcrEngineInstallState state, string detail)
    {
        lock (_gate) _states[providerType] = state;
        StateChanged?.Invoke(providerType, state, detail);
    }
}
