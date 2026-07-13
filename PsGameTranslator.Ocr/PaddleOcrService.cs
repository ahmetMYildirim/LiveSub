using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Ocr;

public sealed class PaddleOcrService : IOcrService
{
    private readonly IOcrSettings _settings;
    private readonly ILogger<PaddleOcrService> _logger;

    private static readonly string ScriptPath =
        Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "tools", "ocr", "paddle_ocr.py"));

    public PaddleOcrService(IOcrSettings settings, ILogger<PaddleOcrService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<OcrResult> RecognizeAsync(
        ReadOnlyMemory<byte> imageData,
        CaptureRegion? region = null,
        CancellationToken cancellationToken = default)
    {
        // Write image bytes to a temp file so the Python script can read it.
        var tempPath = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), $"psg_ocr_{Guid.NewGuid():N}.png"));

        try
        {
            await File.WriteAllBytesAsync(tempPath, imageData.ToArray(), cancellationToken);
            return await RunOcrScriptAsync(tempPath, cancellationToken);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not delete temp OCR file {Path}", tempPath);
            }
        }
    }

    // ── Pre-flight validation ────────────────────────────────────────────────

    private void ValidatePaths(string pythonExe, string imagePath)
    {
        if (!File.Exists(ScriptPath))
            throw new OcrSetupException(
                $"OCR script not found.\n  Script : {ScriptPath}\n\n" +
                "Make sure the project was built so 'tools/ocr/paddle_ocr.py' " +
                "is copied to the output directory.");

        if (!File.Exists(imagePath))
            throw new OcrSetupException(
                $"OCR input image not found.\n  Image : {imagePath}\n\n" +
                "Select and crop a region in the Region Selection tab first.");
    }

    // ── Process execution ────────────────────────────────────────────────────

    private async Task<OcrResult> RunOcrScriptAsync(
        string imagePath, CancellationToken cancellationToken)
    {
        var pythonExe = ResolveOrDetectPython();

        ValidatePaths(pythonExe, imagePath);

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add(ScriptPath);
        psi.ArgumentList.Add(imagePath);

        _logger.LogInformation(
            "OCR starting — python={Python}  script={Script}  image={Image}",
            pythonExe, ScriptPath, imagePath);

        using var process = Process.Start(psi)
            ?? throw new OcrSetupException("Failed to start the Python process.");

        // Read stdout and stderr concurrently to avoid pipe-buffer deadlocks.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(cancellationToken));

        var stdout = stdoutTask.Result.Trim();
        var stderr = stderrTask.Result.Trim();
        var exitCode = process.ExitCode;

        _logger.LogDebug(
            "OCR process exited {Code} — stdout={Stdout}  stderr={Stderr}",
            exitCode, stdout, stderr);

        if (exitCode != 0)
        {
            var diagnostics = BuildDiagnostics(pythonExe, ScriptPath, imagePath,
                exitCode, stdout, stderr);

            _logger.LogError("OCR script failed:\n{Diagnostics}", diagnostics);

            // Exit code 2 → PaddleOCR import error (see paddle_ocr.py).
            if (exitCode == 2)
                throw new OcrSetupException(
                    "PaddleOCR is not installed.\n" +
                    "Run: pip install paddleocr paddlepaddle\n\n" +
                    diagnostics);

            throw new OcrRuntimeException(
                $"OCR script exited with code {exitCode}.\n\n" +
                diagnostics);
        }

        return ParseJson(stdout, pythonExe, ScriptPath, imagePath);
    }

    // ── Diagnostics helper ───────────────────────────────────────────────────

    private static string BuildDiagnostics(
        string python, string script, string image,
        int exitCode, string stdout, string stderr)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Exit code : {exitCode}");
        sb.AppendLine($"Python    : {python}");
        sb.AppendLine($"Script    : {script}");
        sb.AppendLine($"Image     : {image}");
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            sb.AppendLine("--- stdout ---");
            sb.AppendLine(stdout);
        }
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            sb.AppendLine("--- stderr ---");
            sb.AppendLine(stderr);
        }
        return sb.ToString().TrimEnd();
    }

    // ── JSON parsing ─────────────────────────────────────────────────────────

    private OcrResult ParseJson(string json,
        string pythonExe, string scriptPath, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new OcrRuntimeException(
                "OCR script produced no output.\n\n" +
                BuildDiagnostics(pythonExe, scriptPath, imagePath, 0, json, string.Empty));

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new OcrRuntimeException(
                $"OCR script output is not valid JSON.\n\n" +
                BuildDiagnostics(pythonExe, scriptPath, imagePath, 0, json, string.Empty),
                ex);
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorEl))
            {
                var detail = BuildDiagnostics(
                    pythonExe, scriptPath, imagePath, 0, json, string.Empty);
                throw new OcrRuntimeException(
                    $"OCR script reported an error: {errorEl.GetString()}\n\n{detail}");
            }

            var text = root.GetProperty("text").GetString() ?? string.Empty;
            var confidence = root.GetProperty("confidence").GetDouble();

            var lines = new List<OcrLine>();
            if (root.TryGetProperty("lines", out var linesEl))
            {
                foreach (var lineEl in linesEl.EnumerateArray())
                {
                    lines.Add(new OcrLine
                    {
                        Text = lineEl.GetProperty("text").GetString() ?? string.Empty,
                        Confidence = lineEl.GetProperty("confidence").GetDouble()
                    });
                }
            }

            _logger.LogInformation(
                "OCR succeeded — {LineCount} line(s), confidence {Confidence:F2}",
                lines.Count, confidence);

            return new OcrResult { Text = text, Confidence = confidence, Lines = lines, RawOutput = json };
        }
    }

    // ── Python resolution ────────────────────────────────────────────────────

    private string ResolveOrDetectPython() =>
        PythonResolver.Resolve(_settings.PythonExePath);
}
