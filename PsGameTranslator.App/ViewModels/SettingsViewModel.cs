using Microsoft.Extensions.Options;
using PsGameTranslator.Infrastructure.Configuration;
using PsGameTranslator.Ocr;

namespace PsGameTranslator.App.ViewModels;

/// <summary>
/// Holds runtime-mutable application settings and acts as the live
/// <see cref="IOcrSettings"/> source used by <c>PaddleOcrService</c>.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IOcrSettings, IOcrProcessingSettings
{
    private string _pythonExePath;
    private bool _enableOcrCache;
    private int _ocrIntervalMilliseconds;
    private double _minimumConfidenceThreshold;

    public SettingsViewModel(IOptions<AppSettings> options)
    {
        _pythonExePath = options.Value.PythonExePath ?? string.Empty;
        _enableOcrCache = options.Value.EnableOcrCache;
        _ocrIntervalMilliseconds = Math.Max(0, options.Value.OcrIntervalMilliseconds);
        _minimumConfidenceThreshold = Math.Clamp(
            options.Value.MinimumConfidenceThreshold,
            0.0,
            1.0);
    }

    /// <summary>
    /// Full path to the Python executable, e.g.
    /// C:\Users\ahmet\AppData\Local\Programs\Python\Python311\python.exe
    /// Leave empty to auto-detect from PATH.
    /// </summary>
    public string PythonExePath
    {
        get => _pythonExePath;
        set => SetProperty(ref _pythonExePath, value);
    }

    public bool EnableOcrCache
    {
        get => _enableOcrCache;
        set => SetProperty(ref _enableOcrCache, value);
    }

    public int OcrIntervalMilliseconds
    {
        get => _ocrIntervalMilliseconds;
        set => SetProperty(ref _ocrIntervalMilliseconds, Math.Max(0, value));
    }

    public double MinimumConfidenceThreshold
    {
        get => _minimumConfidenceThreshold;
        set => SetProperty(ref _minimumConfidenceThreshold, Math.Clamp(value, 0.0, 1.0));
    }

    // IOcrSettings — returns null when the field is empty so the service auto-detects.
    string? IOcrSettings.PythonExePath =>
        string.IsNullOrWhiteSpace(_pythonExePath) ? null : _pythonExePath.Trim();
}
