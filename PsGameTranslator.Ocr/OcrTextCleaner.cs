using System.Text;
using System.Text.RegularExpressions;

namespace PsGameTranslator.Ocr;

public sealed partial class OcrTextCleaner
{
    // Short subtitles that look like noise but are valid game dialogue.
    private static readonly HashSet<string> ValidShortSubtitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "No!", "No.", "Go!", "Go.", "Run!", "Hi!", "Hi.", "OK.", "OK!", "Yes.", "Yes!",
        "Hey!", "Hey.", "Wait!", "Help!", "Stop!", "Duck!", "Jump!", "Fire!", "Down!",
    };

    public OcrNoiseFilterResult? LastNoiseFilterResult { get; private set; }

    public string Clean(string? rawText, bool rejectOcrNoise = true)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return string.Empty;

        if (rejectOcrNoise)
        {
            var noiseResult = CheckOcrNoise(rawText.Trim());
            LastNoiseFilterResult = noiseResult;
            if (noiseResult.IsNoise)
                return string.Empty;
        }

        var normalized = rawText
            .Normalize(NormalizationForm.FormKC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\u200B", string.Empty, StringComparison.Ordinal)
            .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
            .Replace("\u00AD", string.Empty, StringComparison.Ordinal)
            .Replace('\u00A0', ' ');

        var cleanedLines = new List<string>();
        var previousLineWasEmpty = false;

        foreach (var sourceLine in normalized.Split('\n'))
        {
            var line = MultipleSpacesRegex().Replace(sourceLine.Trim(), " ");
            line = SpaceBeforePunctuationRegex().Replace(line, "$1");

            if (line.Length == 0)
            {
                if (!previousLineWasEmpty && cleanedLines.Count > 0)
                    cleanedLines.Add(string.Empty);
                previousLineWasEmpty = true;
                continue;
            }

            cleanedLines.Add(line);
            previousLineWasEmpty = false;
        }

        while (cleanedLines.Count > 0 && cleanedLines[^1].Length == 0)
            cleanedLines.RemoveAt(cleanedLines.Count - 1);

        return string.Join(Environment.NewLine, cleanedLines);
    }

    private static OcrNoiseFilterResult CheckOcrNoise(string text)
    {
        // Check for valid short subtitles first \u2014 these look like noise but are not.
        if (ValidShortSubtitles.Contains(text))
            return OcrNoiseFilterResult.Accepted(text, "valid_short_subtitle");

        var trimmed = text.Trim();

        // Reject single character
        if (trimmed.Length <= 1)
            return OcrNoiseFilterResult.Rejected(text, "single_character");

        // Reject single letter (no punctuation)
        if (trimmed.Length == 1 && char.IsLetter(trimmed[0]))
            return OcrNoiseFilterResult.Rejected(text, "single_letter");

        // Reject strings < 3 chars that aren't in the valid list
        if (trimmed.Length < 3)
            return OcrNoiseFilterResult.Rejected(text, "too_short_below_3");

        // Count meaningful letters
        var letterCount = trimmed.Count(char.IsLetter);
        var digitCount = trimmed.Count(char.IsDigit);
        var symbolCount = trimmed.Length - letterCount - digitCount - trimmed.Count(char.IsWhiteSpace);
        var total = trimmed.Length;

        // Reject if fewer than 2 meaningful letters and not a valid short subtitle
        if (letterCount < 2)
            return OcrNoiseFilterResult.Rejected(text, "fewer_than_2_letters");

        // Reject if mostly symbols (>60% symbols)
        if (total > 0 && (double)symbolCount / total > 0.6)
            return OcrNoiseFilterResult.Rejected(text, "mostly_symbols");

        // Reject if mostly digits (>70% digits)
        if (total > 0 && (double)digitCount / total > 0.7)
            return OcrNoiseFilterResult.Rejected(text, "mostly_digits");

        return OcrNoiseFilterResult.Accepted(text, "passed_all_filters");
    }

    [GeneratedRegex(@"[^\S\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex MultipleSpacesRegex();

    [GeneratedRegex(@"\s+([,.;:!?])", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceBeforePunctuationRegex();
}

public sealed class OcrNoiseFilterResult
{
    public string Text { get; init; } = string.Empty;
    public bool IsNoise { get; init; }
    public string Reason { get; init; } = string.Empty;

    public static OcrNoiseFilterResult Rejected(string text, string reason) =>
        new() { Text = text, IsNoise = true, Reason = reason };

    public static OcrNoiseFilterResult Accepted(string text, string reason) =>
        new() { Text = text, IsNoise = false, Reason = reason };
}
