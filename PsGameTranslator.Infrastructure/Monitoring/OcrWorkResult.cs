using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Infrastructure.Monitoring;

/// <summary>Outcome of processing a single <see cref="PendingOcrFrame"/> through OCR.</summary>
public sealed class OcrWorkResult
{
    public required PendingOcrFrame Frame { get; init; }
    public OcrResult? Result { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public long DurationMs { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
}
