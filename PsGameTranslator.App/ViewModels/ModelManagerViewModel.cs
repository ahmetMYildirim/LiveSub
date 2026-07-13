using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using PsGameTranslator.App.Commands;
using PsGameTranslator.App.Services;
using PsGameTranslator.Core.Translation;
using PsGameTranslator.Infrastructure.Translation;

namespace PsGameTranslator.App.ViewModels;

internal static partial class ModelProgressParser
{
    // Both install paths (huggingface_hub's tqdm output, Ollama's pull status
    // JSON) report progress as free-text with an "NN%" substring somewhere in
    // it — reuse that instead of plumbing a numeric progress channel through
    // the whole install service.
    [GeneratedRegex(@"(\d{1,3})\s*%")]
    private static partial Regex PercentPattern();

    public static double? TryParsePercent(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = PercentPattern().Match(text);
        return match.Success && double.TryParse(match.Groups[1].Value, out var value)
            ? Math.Clamp(value, 0, 100)
            : null;
    }
}

/// <summary>
/// Model Manager tab: install / verify / remove translation models
/// (HuggingFace for the local machine-translation server, Ollama pulls)
/// without ever needing a terminal.
/// </summary>
public sealed class ModelManagerViewModel : ObservableObject
{
    private readonly ModelInstallService _installService;
    private readonly TranslationSettings _settings;
    private readonly LocalizationService _language;
    private readonly ILogger<ModelManagerViewModel> _logger;

    private string _statusText;

    public ObservableCollection<HfModelRowViewModel> MachineModels { get; } = [];
    public ObservableCollection<OllamaModelRowViewModel> OllamaModels { get; } = [];

    public ICommand RefreshCommand { get; }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public ModelManagerViewModel(
        ModelInstallService installService,
        TranslationSettings settings,
        LocalizationService languageService,
        ILogger<ModelManagerViewModel> logger)
    {
        _installService = installService;
        _settings = settings;
        _language = languageService;
        _logger = logger;
        _statusText = $"{_language.T("Ready")}.";

        foreach (var model in TranslationModelCatalog.MachineTranslationModels)
            MachineModels.Add(new HfModelRowViewModel(model, installService, settings, _language, SetStatus));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        _ = RefreshAsync();
    }

    private void SetStatus(string text) => Post(() => StatusText = text);

    private async Task RefreshAsync()
    {
        SetStatus(_language.T("RefreshingModelStates"));

        foreach (var row in MachineModels)
            await row.RefreshStateAsync();

        // Ollama: installed models live from the server + suggested catalog entries.
        var installed = await _installService.GetOllamaInstalledModelsAsync();
        var installedSet = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        var names = installed
            .Concat(TranslationModelCatalog.SuggestedOllamaModels.Select(m => m.ModelId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Post(() =>
        {
            OllamaModels.Clear();
            foreach (var name in names)
                OllamaModels.Add(new OllamaModelRowViewModel(
                    name, installedSet.Contains(name), _installService, _settings, _language, SetStatus, RefreshAsync));
            StatusText = installed.Count > 0
                ? string.Format(_language.T("ReadyOllamaInstalled"), installed.Count)
                : _language.T("ReadyOllamaUnreachable");
        });
    }

    private static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

/// <summary>Catalog HuggingFace model row with Install / Verify / Remove / Use.</summary>
public sealed class HfModelRowViewModel : ObservableObject
{
    private readonly ModelInstallService _installService;
    private readonly TranslationSettings _settings;
    private readonly LocalizationService _language;
    private readonly Action<string> _setGlobalStatus;
    private readonly AsyncRelayCommand _installCommand;
    private readonly AsyncRelayCommand _removeCommand;
    private readonly AsyncRelayCommand _verifyCommand;
    private readonly AsyncRelayCommand _useCommand;

    private ModelInstallState _state = ModelInstallState.Unknown;
    private string _statusText;
    private string _progressText = string.Empty;
    private bool _isBusy;

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public double? ProgressPercent => ModelProgressParser.TryParsePercent(ProgressText);
    public bool IsProgressIndeterminate => ProgressPercent is null;

    public HfModelRowViewModel(
        TranslationModelInfo info,
        ModelInstallService installService,
        TranslationSettings settings,
        LocalizationService languageService,
        Action<string> setGlobalStatus)
    {
        Info = info;
        _installService = installService;
        _settings = settings;
        _language = languageService;
        _setGlobalStatus = setGlobalStatus;
        _statusText = _language.T("CheckingEllipsis");
        _installCommand = new AsyncRelayCommand(InstallAsync, () => !_isBusy && _state is ModelInstallState.NotInstalled or ModelInstallState.Failed);
        _removeCommand = new AsyncRelayCommand(RemoveAsync, () => !_isBusy && _state == ModelInstallState.Installed);
        _verifyCommand = new AsyncRelayCommand(VerifyAsync, () => !_isBusy && _state == ModelInstallState.Installed);
        _useCommand = new AsyncRelayCommand(UseAsync, () => _state == ModelInstallState.Installed);
    }

    public TranslationModelInfo Info { get; }
    public string DisplayName => Info.DisplayName;
    public string ModelId => Info.ModelId;
    public string Notes => Info.Notes;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ProgressText
    {
        get => _progressText;
        private set
        {
            if (!SetProperty(ref _progressText, value)) return;
            RaisePropertyChanged(nameof(ProgressPercent));
            RaisePropertyChanged(nameof(IsProgressIndeterminate));
        }
    }

    public ICommand InstallCommand => _installCommand;
    public ICommand RemoveCommand => _removeCommand;
    public ICommand VerifyCommand => _verifyCommand;
    public ICommand UseCommand => _useCommand;

    public bool IsActiveModel =>
        string.Equals(_settings.MachineTranslationModel, Info.ModelId, StringComparison.OrdinalIgnoreCase);

    public async Task RefreshStateAsync()
    {
        var state = await _installService.GetHuggingFaceStateAsync(Info.ModelId);
        Post(() =>
        {
            _state = state;
            StatusText = state switch
            {
                ModelInstallState.Installed => _language.T(IsActiveModel ? "InstalledActive" : "InstalledShort"),
                ModelInstallState.NotInstalled => _language.T("NotInstalledShort"),
                ModelInstallState.Downloading => _language.T("DownloadingEllipsis"),
                ModelInstallState.Failed => _language.T("FailedShort"),
                _ => _language.T("CheckingEllipsis"),
            };
            RaiseButtons();
            OnPropertyChanged(nameof(IsActiveModel));
        });
    }

    private async Task InstallAsync()
    {
        IsBusy = true;
        Post(() => { _state = ModelInstallState.Downloading; StatusText = _language.T("DownloadingEllipsis"); RaiseButtons(); });
        var progress = new Progress<string>(line => Post(() => ProgressText = line));
        try
        {
            var (success, message) = await _installService.InstallHuggingFaceAsync(Info.ModelId, progress);
            Post(() => ProgressText = message);
            _setGlobalStatus(success ? string.Format(_language.T("ModelInstalledMsg"), Info.ModelId) : $"{Info.ModelId}: {message}");
        }
        finally
        {
            IsBusy = false;
            await RefreshStateAsync();
        }
    }

    private async Task RemoveAsync()
    {
        IsBusy = true;
        RaiseButtons();
        try
        {
            var (_, message) = await _installService.RemoveHuggingFaceAsync(Info.ModelId);
            Post(() => ProgressText = message);
        }
        finally
        {
            IsBusy = false;
            await RefreshStateAsync();
        }
    }

    private async Task VerifyAsync()
    {
        IsBusy = true;
        RaiseButtons();
        var progress = new Progress<string>(line => Post(() => ProgressText = line));
        try
        {
            var (_, message) = await _installService.VerifyHuggingFaceAsync(Info.ModelId, progress);
            Post(() => ProgressText = message);
        }
        finally
        {
            IsBusy = false;
            RaiseButtons();
        }
    }

    private Task UseAsync()
    {
        _settings.MachineTranslationModel = Info.ModelId;
        _setGlobalStatus(string.Format(_language.T("MachineModelSetMsg"), Info.ModelId));
        Post(() => { StatusText = _language.T("InstalledActive"); OnPropertyChanged(nameof(IsActiveModel)); });
        return Task.CompletedTask;
    }

    private void RaiseButtons()
    {
        _installCommand.NotifyCanExecuteChanged();
        _removeCommand.NotifyCanExecuteChanged();
        _verifyCommand.NotifyCanExecuteChanged();
        _useCommand.NotifyCanExecuteChanged();
    }

    private static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

/// <summary>Ollama model row with Pull / Remove / Use.</summary>
public sealed class OllamaModelRowViewModel : ObservableObject
{
    private readonly ModelInstallService _installService;
    private readonly TranslationSettings _settings;
    private readonly LocalizationService _language;
    private readonly Action<string> _setGlobalStatus;
    private readonly Func<Task> _refreshAll;
    private readonly AsyncRelayCommand _pullCommand;
    private readonly AsyncRelayCommand _removeCommand;
    private readonly AsyncRelayCommand _useCommand;

    private string _progressText = string.Empty;
    private bool _isBusy;

    public OllamaModelRowViewModel(
        string name,
        bool isInstalled,
        ModelInstallService installService,
        TranslationSettings settings,
        LocalizationService languageService,
        Action<string> setGlobalStatus,
        Func<Task> refreshAll)
    {
        Name = name;
        IsInstalled = isInstalled;
        _installService = installService;
        _settings = settings;
        _language = languageService;
        _setGlobalStatus = setGlobalStatus;
        _refreshAll = refreshAll;
        _pullCommand = new AsyncRelayCommand(PullAsync, () => !IsInstalled && !_isBusy);
        _removeCommand = new AsyncRelayCommand(RemoveAsync, () => IsInstalled && !_isBusy);
        _useCommand = new AsyncRelayCommand(UseAsync, () => IsInstalled);
    }

    public string Name { get; }
    public bool IsInstalled { get; }
    public string StatusText => IsInstalled
        ? string.Equals(_settings.OllamaModel, Name, StringComparison.OrdinalIgnoreCase)
            ? _language.T("InstalledActive") : _language.T("InstalledShort")
        : _language.T("NotPulledShort");
    public string ProgressText
    {
        get => _progressText;
        private set
        {
            if (!SetProperty(ref _progressText, value)) return;
            RaisePropertyChanged(nameof(ProgressPercent));
            RaisePropertyChanged(nameof(IsProgressIndeterminate));
        }
    }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public double? ProgressPercent => ModelProgressParser.TryParsePercent(ProgressText);
    public bool IsProgressIndeterminate => ProgressPercent is null;

    public ICommand PullCommand => _pullCommand;
    public ICommand RemoveCommand => _removeCommand;
    public ICommand UseCommand => _useCommand;

    private async Task PullAsync()
    {
        IsBusy = true;
        _pullCommand.NotifyCanExecuteChanged();
        var progress = new Progress<string>(line => Post(() => ProgressText = line));
        try
        {
            var (success, message) = await _installService.PullOllamaModelAsync(Name, progress);
            Post(() => ProgressText = message);
            _setGlobalStatus(success ? string.Format(_language.T("ModelPulledMsg"), Name) : $"{Name}: {message}");
            if (success) await _refreshAll();
        }
        finally
        {
            IsBusy = false;
            Post(() => _pullCommand.NotifyCanExecuteChanged());
        }
    }

    private async Task RemoveAsync()
    {
        IsBusy = true;
        _removeCommand.NotifyCanExecuteChanged();
        try
        {
            var (success, message) = await _installService.RemoveOllamaModelAsync(Name);
            Post(() => ProgressText = message);
            if (success) await _refreshAll();
        }
        finally
        {
            IsBusy = false;
            Post(() => _removeCommand.NotifyCanExecuteChanged());
        }
    }

    private Task UseAsync()
    {
        _settings.OllamaModel = Name;
        _setGlobalStatus(string.Format(_language.T("OllamaModelSetMsg"), Name));
        OnPropertyChanged(nameof(StatusText));
        return Task.CompletedTask;
    }

    private static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
