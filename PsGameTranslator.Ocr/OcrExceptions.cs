namespace PsGameTranslator.Ocr;

/// <summary>Environment / configuration problem (Python not found, script missing, etc.).</summary>
public sealed class OcrSetupException : Exception
{
    public OcrSetupException(string message) : base(message) { }
    public OcrSetupException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The OCR script ran but produced unexpected / error output.</summary>
public sealed class OcrRuntimeException : Exception
{
    public OcrRuntimeException(string message) : base(message) { }
    public OcrRuntimeException(string message, Exception inner) : base(message, inner) { }
}
