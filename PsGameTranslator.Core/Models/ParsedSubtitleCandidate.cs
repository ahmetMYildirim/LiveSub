namespace PsGameTranslator.Core.Models;

/// <summary>
/// Result of splitting a raw OCR subtitle block into speaker-name metadata and
/// dialogue text (Part B). Only <see cref="DialogueText"/> may ever be sent to
/// translation, memory lookup, or the learning dataset; <see cref="SpeakerName"/>
/// is display/metadata only.
/// </summary>
public sealed class ParsedSubtitleCandidate
{
    public string? SpeakerName { get; init; }
    public string DialogueText { get; init; } = string.Empty;
    public string RawOcrText { get; init; } = string.Empty;
    public IReadOnlyList<string> SourceLines { get; init; } = [];
    public string? SpeakerLine { get; init; }
    public IReadOnlyList<string> DialogueLines { get; init; } = [];
    public OverlayRectangle? OriginalSubtitleRect { get; init; }
    public OverlayRectangle? SpeakerRect { get; init; }
    public OverlayRectangle? DialogueRect { get; init; }
    public double Confidence { get; init; }
    public string? RejectionReason { get; init; }

    /// <summary>Speaker-name candidates that were considered but rejected, with reasons.</summary>
    public IReadOnlyList<string> RejectedSpeakerCandidates { get; init; } = [];

    public bool IsRejected => !string.IsNullOrEmpty(RejectionReason);
    public bool HasSpeaker => !string.IsNullOrWhiteSpace(SpeakerName);
}
