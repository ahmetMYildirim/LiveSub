using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Overlay;

public sealed class OverlaySettingsService : IOverlaySettingsService
{
    private static readonly string ConfigPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "config", "overlay.json"));

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<OverlaySettingsService> _logger;

    public OverlaySettingsService(ILogger<OverlaySettingsService> logger)
    {
        _logger = logger;
    }

    public async Task<OverlaySettings> LoadAsync()
    {
        if (!File.Exists(ConfigPath))
        {
            _logger.LogInformation("Overlay config not found at {Path}, using defaults", ConfigPath);
            return new OverlaySettings();
        }

        try
        {
            var json = await File.ReadAllTextAsync(ConfigPath, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<OverlaySettings>(json, JsonOptions);
            _logger.LogInformation("Overlay settings loaded from {Path}", ConfigPath);
            return settings ?? new OverlaySettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load overlay settings from {Path}, using defaults", ConfigPath);
            return new OverlaySettings();
        }
    }

    public async Task SaveAsync(OverlaySettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(ConfigPath, json, Encoding.UTF8);
            _logger.LogInformation("Overlay settings saved to {Path}", ConfigPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save overlay settings to {Path}", ConfigPath);
            throw;
        }
    }
}
