using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using PsGameTranslator.App.ViewModels;

namespace PsGameTranslator.App.Services;

public enum AppLanguage
{
    Turkish,
    English,
}

public sealed class LocalizationService : ObservableObject
{
    private static readonly string SettingsPath =
        Path.Combine(AppContext.BaseDirectory, "language_settings.json");

    private static readonly IReadOnlyDictionary<string, (string Turkish, string English)> Strings =
        new Dictionary<string, (string Turkish, string English)>
        {
            ["NavHome"] = ("Ana Sayfa", "Home"),
            ["NavCapture"] = ("Yakalama", "Capture"),
            ["NavTranslation"] = ("Çeviri", "Translation"),
            ["NavSettings"] = ("Ayarlar", "Settings"),
            ["NavModels"] = ("Model Yönetici", "Model Manager"),
            ["NavGlossary"] = ("Sözlük", "Glossary"),
            ["NavShortcuts"] = ("Kısayollar", "Shortcuts"),
            ["NavLearning"] = ("Öğrenme", "Learning"),
            ["NavTraining"] = ("Eğitim", "Training"),
            ["DeveloperModeOn"] = ("Geliştirici Modu: Açık", "Developer Mode: On"),
            ["DeveloperModeOff"] = ("Geliştirici Modu: Kapalı", "Developer Mode: Off"),
            ["Start"] = ("Başlat", "Start"),
            ["Stop"] = ("Durdur", "Stop"),
            ["Running"] = ("Çalışıyor", "Running"),
            ["Stopped"] = ("Durdu", "Stopped"),
            ["NoGameSelected"] = ("Oyun seçilmedi", "No game selected"),
            ["NoWindowSelected"] = ("Pencere seçilmedi", "No window selected"),
            ["OcrRunning"] = ("OCR çalışıyor", "OCR running"),
            ["Ready"] = ("Hazır", "Ready"),
            ["TranslationDisabled"] = ("Çeviri devre dışı", "Translation disabled"),
            ["TranslationEnabled"] = ("Çeviri etkin", "Translation enabled"),
            ["RefreshingModels"] = ("Model listeleri yenileniyor…", "Refreshing model lists…"),
            ["NotChecked"] = ("Henüz denetlenmedi", "Not checked yet"),
            // Home dashboard widgets
            ["WidgetStatusCards"] = ("Durum kartları", "Status cards"),
            ["WidgetStatusCardsDesc"] = ("OCR, çeviri, overlay ve sistem kaynakları", "OCR, translation, overlay, and system resources"),
            ["WidgetControls"] = ("Hızlı kontroller", "Quick controls"),
            ["WidgetControlsDesc"] = ("Başlat/durdur, OCR ve çeviri motoru/modu", "Start/stop, OCR and translation engine/mode"),
            ["WidgetPerformance"] = ("Performans grafikleri", "Performance charts"),
            ["WidgetPerformanceDesc"] = ("Gecikme ve FPS takibi", "Latency and FPS tracking"),
            ["WidgetStatistics"] = ("İstatistikler", "Statistics"),
            ["WidgetStatisticsDesc"] = ("Seçtiğiniz canlı ölçümler", "The live metrics you selected"),
            ["WidgetHistory"] = ("Son çeviriler", "Recent translations"),
            ["WidgetHistoryDesc"] = ("Son çevrilen altyazılar", "Recently translated subtitles"),
            ["WidgetHealth"] = ("Sağlık", "Health"),
            ["WidgetHealthDesc"] = ("OCR/çeviri/overlay sağlık özeti", "OCR/translation/overlay health summary"),
            ["WidgetLog"] = ("Canlı günlük", "Live log"),
            ["WidgetLogDesc"] = ("Son log satırları", "Latest log lines"),
            ["WidgetGlossary"] = ("Hızlı sözlük", "Quick glossary"),
            ["WidgetGlossaryDesc"] = ("Terim ekle", "Add a term"),
            // Statistics widget rows
            ["StatCpu"] = ("CPU kullanımı", "CPU usage"),
            ["StatRam"] = ("RAM kullanımı", "RAM usage"),
            ["StatFps"] = ("Yakalama FPS", "Capture FPS"),
            ["StatLatency"] = ("Ortalama gecikme", "Average latency"),
            ["StatOcrStatus"] = ("OCR durumu", "OCR status"),
            ["StatOcrCrop"] = ("OCR kırpma boyutu", "OCR crop size"),
            ["StatTranslationStatus"] = ("Çeviri durumu", "Translation status"),
            ["StatTranslationProvider"] = ("Çeviri sağlayıcısı", "Translation provider"),
            ["StatOverlayStatus"] = ("Overlay durumu", "Overlay status"),
            ["StatSessionCount"] = ("Oturumdaki çeviri sayısı", "Translations this session"),
            // Health widget
            ["HealthNotRun"] = ("Henüz çalıştırılmadı.", "Not run yet."),
            ["HealthChecking"] = ("Kontrol ediliyor…", "Checking…"),
            ["HealthAllOk"] = ("✓ Tümü sağlıklı", "✓ All healthy"),
            ["HealthProblem"] = ("⚠ Sorun var", "⚠ Problems"),
            ["HealthFailed"] = ("✗ Kontrol başarısız", "✗ Check failed"),
            ["HealthOcrProvider"] = ("OCR sağlayıcı", "OCR provider"),
            ["HealthOcrServer"] = ("OCR sunucu", "OCR server"),
            ["HealthTranslationProvider"] = ("Çeviri sağlayıcı", "Translation provider"),
            ["HealthTranslationServer"] = ("Çeviri sunucu", "Translation server"),
            ["HealthOverlay"] = ("Overlay", "Overlay"),
            // Model Manager
            ["RefreshingModelStates"] = ("Model durumları yenileniyor…", "Refreshing model states…"),
            ["ReadyOllamaInstalled"] = ("Hazır — Ollama: {0} model kurulu.", "Ready — Ollama: {0} model(s) installed."),
            ["ReadyOllamaUnreachable"] = ("Hazır — Ollama'ya ulaşılamıyor (önerilen modeller gösteriliyor; çekmek için Ollama'yı başlatın).", "Ready — Ollama not reachable (suggested models shown; start Ollama to pull)."),
            ["CheckingEllipsis"] = ("Kontrol ediliyor…", "Checking…"),
            ["InstalledActive"] = ("✓ Kuruldu (aktif)", "✓ Installed (active)"),
            ["InstalledShort"] = ("✓ Kuruldu", "✓ Installed"),
            ["NotInstalledShort"] = ("✗ Kurulu değil", "✗ Not installed"),
            ["DownloadingEllipsis"] = ("⏳ İndiriliyor…", "⏳ Downloading…"),
            ["FailedShort"] = ("✗ Başarısız", "✗ Failed"),
            ["NotPulledShort"] = ("✗ Çekilmedi", "✗ Not pulled"),
            ["ModelInstalledMsg"] = ("{0} kuruldu.", "{0} installed."),
            ["ModelPulledMsg"] = ("{0} çekildi.", "Pulled {0}."),
            ["MachineModelSetMsg"] = ("Makine çevirisi modeli {0} olarak ayarlandı. Uygulamak için çeviri sunucusunu yeniden başlatın.", "Machine translation model set to {0}. Restart the translation server to apply."),
            ["OllamaModelSetMsg"] = ("Ollama modeli {0} olarak ayarlandı.", "Ollama model set to {0}."),
        };

    private AppLanguage _selectedLanguage;

    public LocalizationService()
    {
        _selectedLanguage = LoadSavedLanguage();
    }

    public AppLanguage SelectedLanguage
    {
        get => _selectedLanguage;
        private set => SetProperty(ref _selectedLanguage, value);
    }

    public string T(string key)
    {
        if (!Strings.TryGetValue(key, out var value))
            return key;

        return SelectedLanguage == AppLanguage.Turkish ? value.Turkish : value.English;
    }

    public void ApplyStartupLanguage() => SwapDictionary(_selectedLanguage);

    public void SetLanguage(AppLanguage language)
    {
        if (language == _selectedLanguage)
            return;

        SelectedLanguage = language;
        Save(language);
        SwapDictionary(language);
    }

    private static void SwapDictionary(AppLanguage language)
    {
        var uri = language == AppLanguage.Turkish
            ? new Uri("Resources/Strings.tr.xaml", UriKind.Relative)
            : new Uri("Resources/Strings.en.xaml", UriKind.Relative);

        var newDictionary = new ResourceDictionary { Source = uri };
        var dictionaries = Application.Current.Resources.MergedDictionaries;

        // Dictionary 0 is reserved for the theme. Dictionary 1 is language.
        if (dictionaries.Count > 1)
            dictionaries[1] = newDictionary;
        else
            dictionaries.Add(newDictionary);
    }

    private static AppLanguage LoadSavedLanguage()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return AppLanguage.Turkish;

            var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            var saved = JsonSerializer.Deserialize<LanguageSettings>(json);
            return saved?.Language ?? AppLanguage.Turkish;
        }
        catch
        {
            return AppLanguage.Turkish;
        }
    }

    private static void Save(AppLanguage language)
    {
        try
        {
            var json = JsonSerializer.Serialize(new LanguageSettings(language), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json, Encoding.UTF8);
        }
        catch
        {
            // Non-critical: worst case the language choice does not survive a restart.
        }
    }

    private sealed record LanguageSettings(AppLanguage Language);
}
