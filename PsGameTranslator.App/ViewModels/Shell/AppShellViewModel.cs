using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using PsGameTranslator.App.Commands;
using PsGameTranslator.App.Services;
using PsGameTranslator.App.ViewModels.User;
using LearningViewModel = PsGameTranslator.App.ViewModels.LearningViewModel;

namespace PsGameTranslator.App.ViewModels.Shell;

public sealed class AppShellViewModel : ObservableObject
{
    private readonly CapturePageViewModel _capturePageViewModel;
    private readonly LocalizationService _languageService;
    private readonly AppNavItem _learningNavItem;
    private readonly AppNavItem _trainingNavItem;
    private AppNavItem? _selectedNavItem;
    private object? _currentPage;
    private bool _isDeveloperMode;

    public AppShellViewModel(
        HomeViewModel homeViewModel,
        CapturePageViewModel capturePageViewModel,
        TranslationPageViewModel translationPageViewModel,
        SettingsPageViewModel settingsPageViewModel,
        ModelManagerPageViewModel modelManagerPageViewModel,
        GlossaryPageViewModel glossaryPageViewModel,
        ShortcutsPageViewModel shortcutsPageViewModel,
        LearningViewModel learningViewModel,
        TrainingViewModel trainingViewModel,
        ThemeService themeService,
        LocalizationService languageService)
    {
        Home = homeViewModel;
        Theme = themeService;
        _languageService = languageService;
        _capturePageViewModel = capturePageViewModel;

        Theme.PropertyChanged += (_, _) => RaisePropertyChanged(nameof(ThemeIndex));
        _languageService.PropertyChanged += (_, _) =>
        {
            UpdateNavigationTitles();
            RaisePropertyChanged(nameof(LanguageIndex));
            RaisePropertyChanged(nameof(DeveloperModeButtonText));
        };

        _learningNavItem = new("Learning", "L", "#16A34A", _languageService.T("NavLearning"), learningViewModel);
        _trainingNavItem = new("Training", "E", "#DC2626", _languageService.T("NavTraining"), trainingViewModel);
        UserNavigationItems =
        [
            new("Home", "H", "#7C5CFF", _languageService.T("NavHome"), homeViewModel),
            new("Capture", "C", "#2FB6C4", _languageService.T("NavCapture"), capturePageViewModel),
            new("Translation", "T", "#2F6BFF", _languageService.T("NavTranslation"), translationPageViewModel),
            new("Settings", "S", "#8B93A6", _languageService.T("NavSettings"), settingsPageViewModel),
            new("Models", "M", "#6366F1", _languageService.T("NavModels"), modelManagerPageViewModel),
            new("Glossary", "G", "#F0A020", _languageService.T("NavGlossary"), glossaryPageViewModel),
            new("Shortcuts", "K", "#E85D9E", _languageService.T("NavShortcuts"), shortcutsPageViewModel)
        ];

        ToggleDeveloperModeCommand = new AsyncRelayCommand(ToggleDeveloperModeAsync);
        SelectGameCommand = new AsyncRelayCommand(SelectGameAsync);
        SelectedNavItem = UserNavigationItems[0];
    }

    public HomeViewModel Home { get; }
    public ThemeService Theme { get; }
    public ObservableCollection<AppNavItem> UserNavigationItems { get; }
    public ICommand SelectGameCommand { get; }
    public ICommand ToggleDeveloperModeCommand { get; }

    // "Oyunu Seç" jumps to Capture and refreshes the window list so the user
    // can immediately pick a game window.
    private async Task SelectGameAsync()
    {
        SelectedNavItem = UserNavigationItems.First(item => item.Key == "Capture");
        if (_capturePageViewModel.Capture.RefreshCommand.CanExecute(null))
            _capturePageViewModel.Capture.RefreshCommand.Execute(null);

        await Task.CompletedTask;
    }

    public AppNavItem? SelectedNavItem
    {
        get => _selectedNavItem;
        set
        {
            if (!SetProperty(ref _selectedNavItem, value))
                return;

            CurrentPage = value?.Page;
        }
    }

    public object? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public bool IsDeveloperMode
    {
        get => _isDeveloperMode;
        private set
        {
            if (!SetProperty(ref _isDeveloperMode, value))
                return;

            RaisePropertyChanged(nameof(DeveloperModeButtonText));
        }
    }

    public string DeveloperModeButtonText => IsDeveloperMode
        ? _languageService.T("DeveloperModeOn")
        : _languageService.T("DeveloperModeOff");

    // 0 = Dark, 1 = Light. Theme resources are DynamicResource-based, so this switches live.
    public int ThemeIndex
    {
        get => Theme.SelectedTheme == AppTheme.Dark ? 0 : 1;
        set
        {
            if (value < 0)
                return;

            var newTheme = value == 0 ? AppTheme.Dark : AppTheme.Light;
            if (newTheme == Theme.SelectedTheme)
                return;

            Theme.SetTheme(newTheme);
        }
    }

    // 0 = Turkish, 1 = English. Language uses DynamicResource, so it can switch live.
    public int LanguageIndex
    {
        get => _languageService.SelectedLanguage == AppLanguage.Turkish ? 0 : 1;
        set
        {
            if (value < 0)
                return;

            var newLanguage = value == 0 ? AppLanguage.Turkish : AppLanguage.English;
            _languageService.SetLanguage(newLanguage);
        }
    }

    private Task ToggleDeveloperModeAsync()
    {
        IsDeveloperMode = !IsDeveloperMode;

        if (IsDeveloperMode)
        {
            if (!UserNavigationItems.Contains(_learningNavItem))
                UserNavigationItems.Add(_learningNavItem);
            if (!UserNavigationItems.Contains(_trainingNavItem))
                UserNavigationItems.Add(_trainingNavItem);
        }
        else
        {
            if (SelectedNavItem == _learningNavItem || SelectedNavItem == _trainingNavItem)
                SelectedNavItem = UserNavigationItems[0];

            UserNavigationItems.Remove(_learningNavItem);
            UserNavigationItems.Remove(_trainingNavItem);
        }

        return Task.CompletedTask;
    }

    private void UpdateNavigationTitles()
    {
        foreach (var item in UserNavigationItems)
        {
            item.Title = item.Key switch
            {
                "Home" => _languageService.T("NavHome"),
                "Capture" => _languageService.T("NavCapture"),
                "Translation" => _languageService.T("NavTranslation"),
                "Settings" => _languageService.T("NavSettings"),
                "Models" => _languageService.T("NavModels"),
                "Glossary" => _languageService.T("NavGlossary"),
                "Shortcuts" => _languageService.T("NavShortcuts"),
                "Learning" => _languageService.T("NavLearning"),
                "Training" => _languageService.T("NavTraining"),
                _ => item.Title,
            };
        }
    }
}

public sealed class AppNavItem : ObservableObject
{
    private string _title;

    public AppNavItem(string key, string icon, string accentHex, string title, object page)
    {
        Key = key;
        Icon = icon;
        AccentHex = accentHex;
        _title = title;
        Page = page;
    }

    public string Key { get; }
    public string Icon { get; }
    public string AccentHex { get; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public object Page { get; }
}
