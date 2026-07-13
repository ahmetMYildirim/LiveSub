using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Ocr;

/// <summary>
/// OCR service that calls the persistent local OCR server at http://127.0.0.1:8765/ocr.
/// The server must be started via <see cref="IOcrServerService"/> before use.
/// Timeout is controlled entirely by the caller's <see cref="CancellationToken"/>.
/// </summary>
public sealed class HttpOcrService : IOcrService
{
    private readonly IOcrServerService _server;
    private readonly ILogger<HttpOcrService> _logger;
    private readonly HttpClient _http;

    public HttpOcrService(IOcrServerService server, ILogger<HttpOcrService> logger)
    {
        _server = server;
        _logger = logger;
        _http   = new HttpClient
        {
            // No default timeout — callers supply a CancellationToken with deadline.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    public Task<OcrResult> RecognizeAsync(
        ReadOnlyMemory<byte> imageData,
        CaptureRegion? region = null,
        CancellationToken cancellationToken = default) =>
        RecognizeAsync(imageData, engine: "paddle", cancellationToken);

    /// <summary>
    /// Runs OCR on the multi-engine server. <paramref name="engine"/> selects the
    /// engine ("paddle", "rapid", "easy"); the server answers 501 with an install
    /// hint when the engine's Python package is missing.
    /// </summary>
    public async Task<OcrResult> RecognizeAsync(
        ReadOnlyMemory<byte> imageData,
        string engine,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_server.ServerBaseUrl}/ocr?engine={Uri.EscapeDataString(engine)}";

        using var content  = new MultipartFormDataContent();
        var imageContent   = new ByteArrayContent(imageData.ToArray());
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(imageContent, "file", "image.png");

        _logger.LogInformation("HTTP OCR request → {Url}", url);

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(url, content, cancellationToken);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "OCR HTTP request was cancelled.", cancellationToken);
        }
        catch (TaskCanceledException ex)
        {
            throw new OperationCanceledException(
                "OCR HTTP request timed out.", ex, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new OcrRuntimeException(
                $"OCR server is unreachable at {url}. " +
                "Start the OCR Server in the OCR Server tab first.\n" +
                $"Detail: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new OcrRuntimeException(
                $"OCR server returned HTTP {(int)response.StatusCode}: {ExtractDetail(body)}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseResponse(json);
    }

    /// <summary>
    /// Reads the server's /health JSON and returns the state string of the given
    /// engine ("loaded", "available", "not_installed", "failed: …"), or null when
    /// the server is unreachable or reports no engine map (legacy server).
    /// </summary>
    public async Task<string?> GetEngineStateAsync(string engine, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var json = await _http.GetStringAsync(_server.ServerBaseUrl + "/health", timeout.Token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("engines", out var engines) &&
                engines.TryGetProperty(engine, out var state))
                return state.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractDetail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var detail))
                return detail.GetString() ?? body;
        }
        catch (JsonException)
        {
            // Not JSON — return raw body.
        }
        return body;
    }

    // ── JSON parsing ────────────────────────────────────────────────────────────

    private OcrResult ParseResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new OcrRuntimeException("OCR server returned an empty response.");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new OcrRuntimeException(
                $"OCR server response is not valid JSON.\n\nRaw: {json}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("detail", out var detail))
                throw new OcrRuntimeException($"OCR server error: {detail.GetString()}");

            if (!root.TryGetProperty("text", out var textEl))
                throw new OcrRuntimeException(
                    $"OCR server response is missing the 'text' field.\n\nRaw: {json}");

            var text       = textEl.GetString() ?? string.Empty;
            var confidence = root.TryGetProperty("confidence", out var confEl)
                ? confEl.GetDouble()
                : 0.0;

            var lines = new List<OcrLine>();
            if (root.TryGetProperty("lines", out var linesEl))
            {
                foreach (var l in linesEl.EnumerateArray())
                {
                    int x = -1, y = -1, right = -1, bottom = -1;
                    if (l.TryGetProperty("box", out var boxEl) &&
                        boxEl.ValueKind == JsonValueKind.Array &&
                        boxEl.GetArrayLength() == 4)
                    {
                        x      = boxEl[0].GetInt32();
                        y      = boxEl[1].GetInt32();
                        right  = boxEl[2].GetInt32();
                        bottom = boxEl[3].GetInt32();
                    }

                    lines.Add(new OcrLine
                    {
                        Text       = l.GetProperty("text").GetString() ?? string.Empty,
                        Confidence = l.GetProperty("confidence").GetDouble(),
                        X          = x,
                        Y          = y,
                        Right      = right,
                        Bottom     = bottom,
                    });
                }
            }

            _logger.LogInformation(
                "HTTP OCR succeeded — {Count} line(s), confidence {Confidence:F2}",
                lines.Count, confidence);

            return new OcrResult { Text = text, Confidence = confidence, Lines = lines, RawOutput = json };
        }
    }
}
