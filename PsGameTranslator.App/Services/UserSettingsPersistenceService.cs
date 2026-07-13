using System.IO;
using System.Text.Json;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Ocr;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.App.Services;

/// <summary>
/// Persists the subset of TranslationSettings/OcrEngineSettings the UI actually
/// lets the user change (engine/model picks, API keys, OCR device) to
/// config/user_settings.json, layered on top of appsettings.json by
/// JsonConfigurationHelper. Without this, values like GoogleTranslateApiKey
/// only ever lived in memory and were lost on every restart — the settings
/// ViewModels edit these settings objects directly, but nothing ever wrote
/// them back to disk. Both sections are written together on every Save() call
/// so neither overwrites the other's persisted values.
/// </summary>
public sealed class UserSettingsPersistenceService
{
    private static readonly string SettingsPath =
        Path.Combine(AppContext.BaseDirectory, "config", "user_settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly TranslationSettings _settings;
    private readonly OcrEngineSettings _ocrEngineSettings;

    public UserSettingsPersistenceService(TranslationSettings settings, OcrEngineSettings ocrEngineSettings)
    {
        _settings = settings;
        _ocrEngineSettings = ocrEngineSettings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(BuildPayload(includeApiKeys: true), JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Non-critical: worst case the choice does not survive a restart.
        }
    }

    /// <summary>Writes the current settings to a user-chosen file, deliberately
    /// blanking API keys so an exported profile can be shared safely.</summary>
    public void ExportSafe(string path)
    {
        var json = JsonSerializer.Serialize(BuildPayload(includeApiKeys: false), JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>Applies a previously exported settings file back onto the live
    /// settings objects and persists them. Blank API-key fields (as written by
    /// ExportSafe) are skipped so importing a shared profile never wipes locally
    /// entered keys.</summary>
    public void ImportSafe(string path)
    {
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("Translation", out var t))
        {
            if (t.TryGetProperty(nameof(_settings.ProviderType), out var pv) && pv.TryGetInt32(out var provider))
                _settings.ProviderType = (TranslationProviderType)provider;
            ApplyString(t, nameof(_settings.SourceLanguage), v => _settings.SourceLanguage = v);
            ApplyString(t, nameof(_settings.TargetLanguage), v => _settings.TargetLanguage = v);
            ApplyString(t, nameof(_settings.OllamaModel), v => _settings.OllamaModel = v);
            ApplyString(t, nameof(_settings.MachineTranslationModel), v => _settings.MachineTranslationModel = v);
            ApplyString(t, nameof(_settings.LmStudioModel), v => _settings.LmStudioModel = v);
            ApplyString(t, nameof(_settings.LmStudioBaseUrl), v => _settings.LmStudioBaseUrl = v);
            ApplyBool(t, nameof(_settings.EnableGlossaryCorrections), v => _settings.EnableGlossaryCorrections = v);
            ApplyApiKey(t, nameof(_settings.GoogleTranslateApiKey), v => _settings.GoogleTranslateApiKey = v);
            ApplyApiKey(t, nameof(_settings.DeepLApiKey), v => _settings.DeepLApiKey = v);
            ApplyApiKey(t, nameof(_settings.GeminiApiKey), v => _settings.GeminiApiKey = v);
            ApplyString(t, nameof(_settings.GeminiModel), v => _settings.GeminiModel = v);
            ApplyApiKey(t, nameof(_settings.GroqApiKey), v => _settings.GroqApiKey = v);
            ApplyString(t, nameof(_settings.GroqModel), v => _settings.GroqModel = v);
            ApplyInt(t, nameof(_settings.SubtitleStabilizationMs), v => _settings.SubtitleStabilizationMs = v);
            ApplyInt(t, nameof(_settings.MaxSubtitleMergeWindowMs), v => _settings.MaxSubtitleMergeWindowMs = v);
            ApplyBool(t, nameof(_settings.EnableVisionGameDetection), v => _settings.EnableVisionGameDetection = v);
            ApplyString(t, nameof(_settings.OllamaVisionModel), v => _settings.OllamaVisionModel = v);
            ApplyBool(t, nameof(_settings.EnableSecondarySpeakerOcr), v => _settings.EnableSecondarySpeakerOcr = v);
            ApplyBool(t, nameof(_settings.ShowSpeakerNameInOverlay), v => _settings.ShowSpeakerNameInOverlay = v);
            if (t.TryGetProperty(nameof(_settings.SecondaryOcrRegions), out var regions) && regions.ValueKind == JsonValueKind.Array)
                _settings.SecondaryOcrRegions = JsonSerializer.Deserialize<List<SecondaryOcrRegionSettings>>(regions.GetRawText()) ?? [];
        }

        if (root.TryGetProperty("OcrEngine", out var o)
            && o.TryGetProperty(nameof(_ocrEngineSettings.Device), out var dv) && dv.TryGetInt32(out var device))
            _ocrEngineSettings.Device = (OcrDeviceMode)device;

        Save();
    }

    private object BuildPayload(bool includeApiKeys) => new
    {
        Translation = new
        {
            _settings.ProviderType,
            _settings.SourceLanguage,
            _settings.TargetLanguage,
            _settings.OllamaModel,
            _settings.MachineTranslationModel,
            _settings.LmStudioModel,
            _settings.LmStudioBaseUrl,
            _settings.EnableGlossaryCorrections,
            GoogleTranslateApiKey = includeApiKeys ? _settings.GoogleTranslateApiKey : "",
            DeepLApiKey = includeApiKeys ? _settings.DeepLApiKey : "",
            GeminiApiKey = includeApiKeys ? _settings.GeminiApiKey : "",
            _settings.GeminiModel,
            GroqApiKey = includeApiKeys ? _settings.GroqApiKey : "",
            _settings.GroqModel,
            _settings.SubtitleStabilizationMs,
            _settings.MaxSubtitleMergeWindowMs,
            _settings.EnableVisionGameDetection,
            _settings.OllamaVisionModel,
            _settings.EnableSecondarySpeakerOcr,
            _settings.ShowSpeakerNameInOverlay,
            _settings.SecondaryOcrRegions,
        },
        OcrEngine = new
        {
            _ocrEngineSettings.Device,
        },
    };

    private static void ApplyString(JsonElement parent, string name, Action<string> set)
    {
        if (parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            set(v.GetString() ?? string.Empty);
    }

    private static void ApplyApiKey(JsonElement parent, string name, Action<string> set)
    {
        if (parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var key = v.GetString();
            if (!string.IsNullOrWhiteSpace(key)) set(key);
        }
    }

    private static void ApplyBool(JsonElement parent, string name, Action<bool> set)
    {
        if (parent.TryGetProperty(name, out var v)
            && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
            set(v.GetBoolean());
    }

    private static void ApplyInt(JsonElement parent, string name, Action<int> set)
    {
        if (parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            set(n);
    }
}
