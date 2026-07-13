using System.Text;
using System.Text.RegularExpressions;

namespace PsGameTranslator.Core.Subtitles;

public static partial class SubtitleTextNormalizer
{
    public static string NormalizeKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Trim();
        normalized = normalized
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace('„', '"')
            .Replace('’', '\'')
            .Replace('‘', '\'')
            .Replace('`', '\'');

        normalized = CollapseWhitespaceRegex().Replace(normalized, " ");
        normalized = CollapsePunctuationRuns(normalized);
        return normalized.Trim().ToLowerInvariant();
    }

    private static string CollapsePunctuationRuns(string text)
    {
        var builder = new StringBuilder(text.Length);
        char? previous = null;
        foreach (var character in text)
        {
            var isRepeatablePunctuation = character is '.' or '!' or '?' or ',' or ':' or ';';
            if (isRepeatablePunctuation && previous == character)
                continue;

            builder.Append(character);
            previous = character;
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespaceRegex();
}
