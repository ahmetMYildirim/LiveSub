using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Ocr;
using PsGameTranslator.Ocr;
using OcrResult = PsGameTranslator.Core.Models.OcrResult;
using OcrLine = PsGameTranslator.Core.Models.OcrLine;

namespace PsGameTranslator.App.Services;

/// <summary>
/// Native Windows OCR provider (Windows.Media.Ocr). No server process needed;
/// availability depends on an installed OCR language pack for the requested language.
/// </summary>
public sealed class WindowsOcrProvider : IOcrProvider
{
    private OcrEngine? _engine;
    private string _engineLanguage = string.Empty;

    public string Name => "WindowsOCR";
    public OcrProviderType ProviderType => OcrProviderType.WindowsOCR;

    public bool IsAvailable
    {
        get
        {
            try { return OcrEngine.AvailableRecognizerLanguages.Count > 0; }
            catch { return false; }
        }
    }

    public Task<OcrProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var languages = OcrEngine.AvailableRecognizerLanguages
                .Select(language => language.LanguageTag)
                .ToArray();
            return Task.FromResult(new OcrProviderHealth
            {
                ProviderName = Name,
                ProviderType = ProviderType,
                IsAvailable = languages.Length > 0,
                State = languages.Length > 0 ? OcrProviderState.Available : OcrProviderState.NotConfigured,
                Message = languages.Length > 0
                    ? $"Windows OCR ready. Languages: {string.Join(", ", languages)}"
                    : "No Windows OCR language packs installed. Add one via Settings → Time & Language.",
            });
        }
        catch (Exception exception)
        {
            return Task.FromResult(new OcrProviderHealth
            {
                ProviderName = Name,
                ProviderType = ProviderType,
                IsAvailable = false,
                State = OcrProviderState.Failed,
                Message = $"Windows OCR is not usable on this system: {exception.Message}",
            });
        }
    }

    public async Task<OcrResult> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var engine = ResolveEngine(request.Language);
            if (engine is null)
            {
                return new OcrResult
                {
                    ProviderName = Name,
                    Success = false,
                    ErrorMessage =
                        $"No Windows OCR language pack matches '{request.Language}'. " +
                        "Install an English language pack (Settings → Time & Language → Language).",
                };
            }

            using var softwareBitmap = await DecodeAsync(request.ImageBytes);
            var ocrResult = await engine.RecognizeAsync(softwareBitmap);
            stopwatch.Stop();

            var lines = new List<OcrLine>(ocrResult.Lines.Count);
            foreach (var line in ocrResult.Lines)
            {
                double minX = double.MaxValue, minY = double.MaxValue, maxX = -1, maxY = -1;
                foreach (var word in line.Words)
                {
                    var rect = word.BoundingRect;
                    minX = Math.Min(minX, rect.X);
                    minY = Math.Min(minY, rect.Y);
                    maxX = Math.Max(maxX, rect.X + rect.Width);
                    maxY = Math.Max(maxY, rect.Y + rect.Height);
                }

                var hasBox = maxX > 0 && maxY > 0;
                lines.Add(new OcrLine
                {
                    Text = line.Text,
                    // Windows OCR exposes no per-line confidence; use a fixed
                    // optimistic value so threshold gating stays meaningful.
                    Confidence = 0.9,
                    X = hasBox ? (int)minX : -1,
                    Y = hasBox ? (int)minY : -1,
                    Right = hasBox ? (int)maxX : -1,
                    Bottom = hasBox ? (int)maxY : -1,
                });
            }

            var text = string.Join("\n", lines.Select(line => line.Text));
            return new OcrResult
            {
                ProviderName = Name,
                Text = text,
                Confidence = lines.Count > 0 ? 0.9 : 0.0,
                Lines = lines,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Success = true,
                RawOutput = ocrResult.Text,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new OcrResult
            {
                ProviderName = Name,
                Success = false,
                ErrorMessage = $"Windows OCR failed: {exception.Message}",
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }
    }

    private OcrEngine? ResolveEngine(string language)
    {
        var requested = string.IsNullOrWhiteSpace(language) ? "en" : language;
        if (_engine is not null && _engineLanguage == requested)
            return _engine;

        var engine = OcrEngine.TryCreateFromLanguage(new Language(requested))
                     ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is not null)
        {
            _engine = engine;
            _engineLanguage = requested;
        }
        return engine;
    }

    private static async Task<SoftwareBitmap> DecodeAsync(ReadOnlyMemory<byte> imageBytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(imageBytes.ToArray().AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
}
