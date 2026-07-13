using System.IO;
using System.Text.Json;
using System.Windows;
using PsGameTranslator.App.ViewModels;

namespace PsGameTranslator.App.Services;

public enum AppTheme
{
    Dark,
    Light,
}

// Live theme switching relies on every themed brush/style being consumed via
// {DynamicResource ...} (never StaticResource) in XAML. Given that, replacing
// dictionaries[0] wholesale is enough — WPF's resource system notifies every
// DynamicResource consumer app-wide when a merged dictionary is swapped.
public sealed class ThemeService : ObservableObject
{
    private static readonly string SettingsPath =
        Path.Combine(AppContext.BaseDirectory, "theme_settings.json");

    private AppTheme _selectedTheme;

    public ThemeService()
    {
        _selectedTheme = LoadSavedTheme();
    }

    public AppTheme SelectedTheme
    {
        get => _selectedTheme;
        private set => SetProperty(ref _selectedTheme, value);
    }

    public string CurrentThemeName => SelectedTheme == AppTheme.Dark ? "Karanlık" : "Açık";

    public void ApplyStartupTheme() => SwapDictionary(_selectedTheme);

    // Swaps the live dictionary immediately — every themed brush/style in the
    // app is consumed via DynamicResource, so this repaints every open window.
    public void SetTheme(AppTheme theme)
    {
        if (theme == _selectedTheme)
            return;

        SelectedTheme = theme;
        RaisePropertyChanged(nameof(CurrentThemeName));
        Save(theme);
        SwapDictionary(theme);
    }

    private static void SwapDictionary(AppTheme theme)
    {
        var uri = theme == AppTheme.Dark
            ? new Uri("Themes/ThemeResources.xaml", UriKind.Relative)
            : new Uri("Themes/LightThemeResources.xaml", UriKind.Relative);

        var newDictionary = new ResourceDictionary { Source = uri };
        var dictionaries = Application.Current.Resources.MergedDictionaries;

        if (dictionaries.Count > 0)
            dictionaries[0] = newDictionary;
        else
            dictionaries.Add(newDictionary);
    }

    private static AppTheme LoadSavedTheme()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return AppTheme.Dark;

            var json = File.ReadAllText(SettingsPath);
            var saved = JsonSerializer.Deserialize<ThemeSettings>(json);
            return saved?.Theme ?? AppTheme.Dark;
        }
        catch
        {
            return AppTheme.Dark;
        }
    }

    private static void Save(AppTheme theme)
    {
        try
        {
            var json = JsonSerializer.Serialize(new ThemeSettings(theme));
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Non-critical: worst case the choice does not survive a restart.
        }
    }

    private sealed record ThemeSettings(AppTheme Theme);
}
