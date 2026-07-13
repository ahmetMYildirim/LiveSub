using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PsGameTranslator.App.Commands;
using PsGameTranslator.App.Services;

namespace PsGameTranslator.App.ViewModels.User;

public sealed class SettingsPageViewModel : ObservableObject
{
    private readonly ThemeService _themeService;
    private readonly UserSettingsPersistenceService _persistence;
    private bool _isAdvancedMode;
    private int _selectedPresetIndex = 1;
    private string _operationStatus = string.Empty;

    public SettingsPageViewModel(MonitoringViewModel monitoring, OverlayViewModel overlay,
        TranslationViewModel translation, OcrViewModel ocr, ThemeService themeService,
        UserSettingsPersistenceService persistence)
    {
        Monitoring = monitoring; Overlay = overlay; Translation = translation; Ocr = ocr;
        _themeService = themeService; _persistence = persistence;
        _themeService.PropertyChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(IsDarkThemeSelected));
            RaisePropertyChanged(nameof(IsLightThemeSelected));
        };
        SelectDarkThemeCommand = new AsyncRelayCommand(() => SetThemeAsync(AppTheme.Dark));
        SelectLightThemeCommand = new AsyncRelayCommand(() => SetThemeAsync(AppTheme.Light));
        ApplyPresetCommand = new AsyncRelayCommand(ApplyPresetAsync);
        ExportSettingsCommand = new AsyncRelayCommand(ExportSettingsAsync);
        ImportSettingsCommand = new AsyncRelayCommand(ImportSettingsAsync);
    }

    public MonitoringViewModel Monitoring { get; }
    public OverlayViewModel Overlay { get; }
    public TranslationViewModel Translation { get; }
    public OcrViewModel Ocr { get; }
    public bool IsDarkThemeSelected => _themeService.SelectedTheme == AppTheme.Dark;
    public bool IsLightThemeSelected => _themeService.SelectedTheme == AppTheme.Light;
    public bool IsAdvancedMode { get => _isAdvancedMode; set { if (SetProperty(ref _isAdvancedMode, value)) RaisePropertyChanged(nameof(AdvancedVisibility)); } }
    public Visibility AdvancedVisibility => IsAdvancedMode ? Visibility.Visible : Visibility.Collapsed;
    public int SelectedPresetIndex { get => _selectedPresetIndex; set => SetProperty(ref _selectedPresetIndex, value); }
    public string OperationStatus { get => _operationStatus; private set => SetProperty(ref _operationStatus, value); }
    public ICommand SelectDarkThemeCommand { get; }
    public ICommand SelectLightThemeCommand { get; }
    public ICommand ApplyPresetCommand { get; }
    public ICommand ExportSettingsCommand { get; }
    public ICommand ImportSettingsCommand { get; }

    private Task SetThemeAsync(AppTheme theme)
    {
        _themeService.SetTheme(theme);
        OperationStatus = theme == AppTheme.Dark ? "Karanlık tema uygulandı." : "Açık tema uygulandı.";
        return Task.CompletedTask;
    }

    private Task ApplyPresetAsync()
    {
        if (SelectedPresetIndex == 0)
        {
            Monitoring.CaptureIntervalMs = 100; Monitoring.MinOcrIntervalMs = 220;
            Monitoring.EnableFastOcrMode = true; Translation.TranslationProfileIndex = 1;
            OperationStatus = "Hızlı profil uygulandı.";
        }
        else if (SelectedPresetIndex == 2)
        {
            Monitoring.CaptureIntervalMs = 180; Monitoring.MinOcrIntervalMs = 500;
            Monitoring.EnableFastOcrMode = false; Translation.TranslationProfileIndex = 2;
            OperationStatus = "Kaliteli profil uygulandı.";
        }
        else
        {
            Monitoring.CaptureIntervalMs = 120; Monitoring.MinOcrIntervalMs = 350;
            Monitoring.EnableFastOcrMode = true; Translation.TranslationProfileIndex = 0;
            OperationStatus = "Dengeli profil uygulandı.";
        }
        _persistence.Save();
        return Task.CompletedTask;
    }

    private Task ExportSettingsAsync()
    {
        var dialog = new SaveFileDialog { Filter = "PS Game Translator ayarları (*.json)|*.json", FileName = "psgt-settings.json" };
        if (dialog.ShowDialog() == true)
        {
            _persistence.ExportSafe(dialog.FileName);
            OperationStatus = "Ayarlar dışa aktarıldı. API anahtarları dosyaya eklenmedi.";
        }
        return Task.CompletedTask;
    }

    private Task ImportSettingsAsync()
    {
        var dialog = new OpenFileDialog { Filter = "PS Game Translator ayarları (*.json)|*.json" };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                _persistence.ImportSafe(dialog.FileName);
                OperationStatus = "Ayarlar içe aktarıldı. Değerler sonraki açılışta tamamen uygulanacak.";
            }
            catch (Exception ex) { OperationStatus = $"İçe aktarma başarısız: {ex.Message}"; }
        }
        return Task.CompletedTask;
    }
}
