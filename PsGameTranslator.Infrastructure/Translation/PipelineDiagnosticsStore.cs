using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Infrastructure.Translation;

public sealed class PipelineDiagnosticsStore
{
    private static readonly string DiagnosticsPath =
        Path.Combine(AppContext.BaseDirectory, "debug", "pipeline_state.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly PipelineDiagnostics _diagnostics;
    private readonly ILogger<PipelineDiagnosticsStore> _logger;
    private readonly object _gate = new();

    public PipelineDiagnosticsStore(
        PipelineDiagnostics diagnostics,
        ILogger<PipelineDiagnosticsStore> logger)
    {
        _diagnostics = diagnostics;
        _logger = logger;
    }

    // Called several times per subtitle from every pipeline stage — must never
    // block on disk. The background writer flushes the latest state (~300 ms).
    public void Save() => DebugFileWriter.Queue(DiagnosticsPath, _diagnostics);
}
