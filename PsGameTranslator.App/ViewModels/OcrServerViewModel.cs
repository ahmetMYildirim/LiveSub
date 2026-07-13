using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PsGameTranslator.App.Commands;
using PsGameTranslator.Core.Ocr;
using PsGameTranslator.Ocr;

namespace PsGameTranslator.App.ViewModels;

public sealed class OcrServerViewModel : ObservableObject
{
    private readonly IOcrServerService _server;
    private readonly OcrEngineInstallService _installService;
    private readonly OcrProviderFactory _providerFactory;
    private readonly ILogger<OcrServerViewModel> _logger;

    private readonly AsyncRelayCommand _startCommand;
    private readonly AsyncRelayCommand _stopCommand;
    private readonly AsyncRelayCommand _testCommand;

    private bool _isStarting;
    private string _serverStatusText = "Stopped";
    private string _lastTestResultText = "—";

    public ObservableCollection<OcrEngineRowViewModel> Engines { get; } = [];
    public ICommand RefreshEnginesCommand { get; }

    public OcrServerViewModel(
        IOcrServerService server,
        OcrEngineInstallService installService,
        OcrProviderFactory providerFactory,
        ILogger<OcrServerViewModel> logger)
    {
        _server = server;
        _installService = installService;
        _providerFactory = providerFactory;
        _logger = logger;

        foreach (var descriptor in OcrEngineInstallService.Engines)
            Engines.Add(new OcrEngineRowViewModel(descriptor, installService, server, providerFactory, logger));

        RefreshEnginesCommand = new AsyncRelayCommand(RefreshEnginesAsync);
        _installService.StateChanged += (_, _, _) => PostToUi(() =>
        {
            foreach (var row in Engines) row.RefreshFromCache();
        });
        _ = RefreshEnginesAsync();

        _startCommand = new AsyncRelayCommand(StartServerAsync, () => !_server.IsRunning && !_isStarting);
        _stopCommand  = new AsyncRelayCommand(StopServerAsync,  () => _server.IsRunning);
        _testCommand  = new AsyncRelayCommand(TestConnectionAsync);

        // Keep the tab in sync when the server is started outside this tab
        // (app startup auto-start, provider-selection auto-start).
        _server.StateChanged += OnServerStateChanged;
    }

    private async Task RefreshEnginesAsync()
    {
        await _installService.RefreshAllAsync();
        PostToUi(() =>
        {
            foreach (var row in Engines) row.RefreshFromCache();
        });
        foreach (var row in Engines)
            await row.RefreshRuntimeAsync();
    }

    private static void PostToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private void OnServerStateChanged(OcrProviderState state, string error)
    {
        void Apply()
        {
            ServerStatusText = state switch
            {
                OcrProviderState.Starting => "Starting…",
                OcrProviderState.Running => $"Running — {_server.ServerBaseUrl}",
                OcrProviderState.RunningExternal => $"Running (external) — {_server.ServerBaseUrl}",
                OcrProviderState.Failed => $"Failed to start: {error}",
                OcrProviderState.Stopped => "Stopped",
                _ => state.ToString(),
            };
            NotifyCanExecuteAll();
            OnPropertyChanged(nameof(IsRunning));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply();
        else dispatcher.BeginInvoke(Apply);

        // Server lifecycle changes flip engine readiness (Ready ↔ Server stopped).
        _ = Task.Run(async () =>
        {
            foreach (var row in Engines)
                await row.RefreshRuntimeAsync();
        });
    }

    // ── Status ────────────────────────────────────────────────────────────────

    public bool IsRunning => _server.IsRunning;

    public string ServerBaseUrl => _server.ServerBaseUrl;

    public string ServerStatusText
    {
        get => _serverStatusText;
        private set => SetProperty(ref _serverStatusText, value);
    }

    public string LastTestResultText
    {
        get => _lastTestResultText;
        private set => SetProperty(ref _lastTestResultText, value);
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    public ICommand StartServerCommand => _startCommand;
    public ICommand StopServerCommand  => _stopCommand;
    public ICommand TestConnectionCommand => _testCommand;

    // ── Implementations ───────────────────────────────────────────────────────

    private async Task StartServerAsync()
    {
        _isStarting = true;
        ServerStatusText = "Starting…";
        NotifyCanExecuteAll();

        try
        {
            await _server.StartAsync();
            ServerStatusText = $"Running — {_server.ServerBaseUrl}";
            _logger.LogInformation("OCR server started successfully");
        }
        catch (Exception ex)
        {
            ServerStatusText = $"Failed to start: {ex.Message}";
            _logger.LogError(ex, "OCR server failed to start");
        }
        finally
        {
            _isStarting = false;
            NotifyCanExecuteAll();
            OnPropertyChanged(nameof(IsRunning));
        }
    }

    private async Task StopServerAsync()
    {
        try
        {
            await _server.StopAsync();
            ServerStatusText = "Stopped";
            _logger.LogInformation("OCR server stopped");
        }
        catch (Exception ex)
        {
            ServerStatusText = $"Stop error: {ex.Message}";
            _logger.LogWarning(ex, "Error stopping OCR server");
        }
        finally
        {
            NotifyCanExecuteAll();
            OnPropertyChanged(nameof(IsRunning));
        }
    }

    private async Task TestConnectionAsync()
    {
        LastTestResultText = "Testing…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var (success, message) = await _server.TestConnectionAsync(cts.Token);
            LastTestResultText = success ? $"✓ {message}" : $"✗ {message}";
            _logger.LogInformation("OCR server test: {Result}", LastTestResultText);
        }
        catch (Exception ex)
        {
            LastTestResultText = $"✗ {ex.Message}";
        }
    }

    private void NotifyCanExecuteAll()
    {
        _startCommand.NotifyCanExecuteChanged();
        _stopCommand.NotifyCanExecuteChanged();
        _testCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// One row in the "Installed OCR Engines" panel: install state, runtime
/// readiness (server + engine), and Install/Remove actions per engine.
/// </summary>
public sealed class OcrEngineRowViewModel : ObservableObject
{
    private readonly OcrEngineDescriptor _descriptor;
    private readonly OcrEngineInstallService _installService;
    private readonly IOcrServerService _server;
    private readonly OcrProviderFactory _providerFactory;
    private readonly ILogger _logger;
    private readonly AsyncRelayCommand _installCommand;
    private readonly AsyncRelayCommand _removeCommand;

    private string _statusText = "Checking…";
    private string _progressText = string.Empty;
    private bool _isBusy;

    public OcrEngineRowViewModel(
        OcrEngineDescriptor descriptor,
        OcrEngineInstallService installService,
        IOcrServerService server,
        OcrProviderFactory providerFactory,
        ILogger logger)
    {
        _descriptor = descriptor;
        _installService = installService;
        _server = server;
        _providerFactory = providerFactory;
        _logger = logger;
        _installCommand = new AsyncRelayCommand(InstallAsync, () => CanInstall && !_isBusy);
        _removeCommand = new AsyncRelayCommand(RemoveAsync, () => CanRemove && !_isBusy);
    }

    public string DisplayName => _descriptor.DisplayName;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    public ICommand InstallCommand => _installCommand;
    public ICommand RemoveCommand => _removeCommand;

    public bool CanInstall =>
        _descriptor.PythonModule is not null &&
        _installService.GetCachedState(_descriptor.ProviderType)
            is OcrEngineInstallState.NotInstalled or OcrEngineInstallState.Failed;

    public bool CanRemove =>
        _descriptor.PythonModule is not null &&
        _installService.GetCachedState(_descriptor.ProviderType) == OcrEngineInstallState.Installed;

    /// <summary>Recomputes the install-state part of the status from the service cache.</summary>
    public void RefreshFromCache()
    {
        StatusText = ComposeStatus(runtimeDetail: null);
        RaiseButtons();
    }

    /// <summary>Adds live runtime readiness (server running / engine loaded / health message).</summary>
    public async Task RefreshRuntimeAsync()
    {
        var installState = _installService.GetCachedState(_descriptor.ProviderType);
        if (installState is not OcrEngineInstallState.Installed and not OcrEngineInstallState.BuiltIn)
        {
            Post(() => { StatusText = ComposeStatus(runtimeDetail: null); RaiseButtons(); });
            return;
        }

        string? runtimeDetail = null;
        try
        {
            if (_descriptor.RequiresServer && !_server.IsRunning)
            {
                runtimeDetail = "○ Server stopped";
            }
            else
            {
                var (provider, reason) = _providerFactory.GetBest(_descriptor.ProviderType);
                if (provider is null)
                {
                    runtimeDetail = $"✗ {reason}";
                }
                else
                {
                    var health = await provider.CheckHealthAsync();
                    runtimeDetail = health.IsAvailable
                        ? "✓ Ready"
                        : $"✗ {health.Message}";
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "engine_runtime_refresh_failed - {Engine}", _descriptor.DisplayName);
            runtimeDetail = "✗ Health check failed";
        }

        Post(() => { StatusText = ComposeStatus(runtimeDetail); RaiseButtons(); });
    }

    private string ComposeStatus(string? runtimeDetail)
    {
        var installState = _installService.GetCachedState(_descriptor.ProviderType);
        var installPart = installState switch
        {
            OcrEngineInstallState.Installed => "✓ Installed",
            OcrEngineInstallState.NotInstalled => "✗ Not installed",
            OcrEngineInstallState.Downloading => "⏳ Downloading…",
            OcrEngineInstallState.Failed => "✗ Install failed",
            OcrEngineInstallState.BuiltIn => "✓ Built-in",
            OcrEngineInstallState.NotSupported => "✗ Not supported yet",
            _ => "… Checking",
        };

        var serverPart = _descriptor.RequiresServer && installState == OcrEngineInstallState.Installed
            ? _server.IsRunning ? "  ✓ Server running" : "  ○ Server stopped"
            : string.Empty;

        return runtimeDetail is null
            ? installPart + serverPart
            : $"{installPart}{serverPart}  {runtimeDetail}";
    }

    private async Task InstallAsync()
    {
        _isBusy = true;
        RaiseButtons();
        var progress = new Progress<string>(line => Post(() => ProgressText = line));
        try
        {
            var (success, message) = await _installService.InstallAsync(_descriptor.ProviderType, progress);
            Post(() => ProgressText = message);

            // A newly installed server engine becomes usable once the server
            // (re)starts and reloads its engine registry.
            if (success && _descriptor.RequiresServer && _server.IsRunning)
            {
                Post(() => ProgressText = "Installed — restarting OCR server…");
                await _server.StopAsync();
                await _server.EnsureRunningAsync();
                Post(() => ProgressText = "Installed and server restarted.");
            }
            else if (success && _descriptor.RequiresServer)
            {
                await _server.EnsureRunningAsync();
            }
        }
        finally
        {
            _isBusy = false;
            await RefreshRuntimeAsync();
        }
    }

    private async Task RemoveAsync()
    {
        _isBusy = true;
        RaiseButtons();
        var progress = new Progress<string>(line => Post(() => ProgressText = line));
        try
        {
            var (_, message) = await _installService.UninstallAsync(_descriptor.ProviderType, progress);
            Post(() => ProgressText = message);
        }
        finally
        {
            _isBusy = false;
            await RefreshRuntimeAsync();
        }
    }

    private void RaiseButtons()
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanRemove));
        _installCommand.NotifyCanExecuteChanged();
        _removeCommand.NotifyCanExecuteChanged();
    }

    private static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
