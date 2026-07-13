namespace PsGameTranslator.Core.Ocr;

public enum OcrFailureReason
{
    ProviderUnavailable,
    ServerNotRunning,
    ServerStartupFailed,
    RequestFailed,
    InvalidImage,
    EmptyProviderResult,
    JsonParseError,
    ConfidenceBelowThreshold,
    CandidateRejected,
    DuplicateSkipped,
    Unknown
}
