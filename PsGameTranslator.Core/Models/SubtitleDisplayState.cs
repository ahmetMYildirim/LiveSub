namespace PsGameTranslator.Core.Models;

public enum SubtitleDisplayState
{
    Empty = 0,
    ShowingSourcePendingTranslation = 1,
    ShowingTranslated = 2,
    ShowingSourceFallback = 3,
    HoldingPrevious = 4,
}
