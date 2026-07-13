using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Core.Subtitles;

public interface ISubtitleFormatter
{
    Task<FormattedSubtitle> FormatAsync(
        string cleanedOcrText,
        double confidence,
        CancellationToken cancellationToken);
}
