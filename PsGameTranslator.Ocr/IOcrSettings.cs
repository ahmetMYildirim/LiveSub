namespace PsGameTranslator.Ocr;

/// <summary>
/// Runtime-mutable OCR settings resolved from whichever source is authoritative
/// (appsettings.json, UI settings panel, etc.).
/// </summary>
public interface IOcrSettings
{
    /// <summary>
    /// Full path to the Python executable, e.g.
    /// C:\Users\ahmet\AppData\Local\Programs\Python\Python311\python.exe
    /// Null / empty → auto-detect ("python", "python3", "py").
    /// </summary>
    string? PythonExePath { get; }
}
