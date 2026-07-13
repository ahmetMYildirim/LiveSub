using System.Text.RegularExpressions;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Subtitles;

namespace PsGameTranslator.Infrastructure.Subtitles;

public sealed class SubtitleFormatter : ISubtitleFormatter
{
    private readonly SubtitleFormatterSettings _settings;
    private readonly SpeakerNameDetector _speakerNameDetector;

    public SubtitleFormatter(SubtitleFormatterSettings settings, SpeakerNameDetector speakerNameDetector)
    {
        _settings = settings;
        _speakerNameDetector = speakerNameDetector;
    }

    public Task<FormattedSubtitle> FormatAsync(
        string cleanedOcrText,
        double confidence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawText = cleanedOcrText ?? string.Empty;
        var normalizedLines = NormalizeLines(rawText);

        if (_settings.RemoveHudNoise)
        {
            var noiseWords = new HashSet<string>(
                _settings.HudNoiseWords.Where(word => !string.IsNullOrWhiteSpace(word)),
                StringComparer.OrdinalIgnoreCase);
            normalizedLines = normalizedLines
                .Where(line => string.IsNullOrEmpty(line) || !noiseWords.Contains(line))
                .ToList();
            TrimEmptyEdges(normalizedLines);
        }

        var cleanedText = string.Join(Environment.NewLine, normalizedLines);
        var contentLines = normalizedLines.Where(line => line.Length > 0).ToList();
        if (contentLines.Count == 0)
        {
            return Task.FromResult(new FormattedSubtitle
            {
                RawText = rawText,
                CleanedText = cleanedText,
                IsEmpty = true,
                Confidence = confidence,
            });
        }

        if (!_settings.EnableSubtitleFormatter)
        {
            return Task.FromResult(new FormattedSubtitle
            {
                RawText = rawText,
                CleanedText = cleanedText,
                MainText = string.Join(" ", contentLines),
                Lines = contentLines,
                DisplayText = cleanedText,
                IsEmpty = false,
                Confidence = confidence,
            });
        }

        // Speaker-aware parsing (Part A/B): only DialogueText continues toward
        // translation; the speaker name is carried as metadata.
        var parsed = _speakerNameDetector.Parse(contentLines, rawText);
        if (parsed.IsRejected)
        {
            // e.g. a lone name plate with no dialogue — never translate, never
            // update the overlay with just a name.
            return Task.FromResult(new FormattedSubtitle
            {
                RawText = rawText,
                CleanedText = cleanedText,
                SpeakerName = parsed.SpeakerName ?? string.Empty,
                IsEmpty = true,
                Confidence = confidence,
            });
        }

        var speakerName = parsed.SpeakerName ?? string.Empty;
        var mainText = parsed.DialogueText;

        var maxLines = Math.Max(1, _settings.MaxSubtitleLines);
        var maxCharacters = Math.Max(8, _settings.MaxCharactersPerLine);
        var displayLines = new List<string>(maxLines);

        if (_settings.ShowSpeakerName && speakerName.Length > 0)
            displayLines.Add(speakerName + ":");

        var availableMainLines = Math.Max(0, maxLines - displayLines.Count);
        if (availableMainLines > 0 && mainText.Length > 0)
            displayLines.AddRange(WrapWords(mainText, maxCharacters, availableMainLines));

        return Task.FromResult(new FormattedSubtitle
        {
            RawText = rawText,
            CleanedText = cleanedText,
            SpeakerName = speakerName,
            MainText = mainText,
            Lines = displayLines,
            DisplayText = string.Join(Environment.NewLine, displayLines),
            IsEmpty = displayLines.Count == 0,
            Confidence = confidence,
        });
    }

    private static List<string> NormalizeLines(string text)
    {
        var result = new List<string>();
        var previousWasEmpty = false;
        foreach (var sourceLine in text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var line = NormalizeInlineWhitespace(sourceLine);
            var isEmpty = line.Length == 0;
            if (isEmpty && previousWasEmpty) continue;
            result.Add(line);
            previousWasEmpty = isEmpty;
        }
        TrimEmptyEdges(result);
        return result;
    }

    private static void TrimEmptyEdges(List<string> lines)
    {
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
    }

    private static IReadOnlyList<string> WrapWords(string text, int maxCharacters, int maxLines)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>(maxLines);
        var current = string.Empty;
        var truncated = false;

        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (candidate.Length <= maxCharacters || current.Length == 0)
            {
                current = candidate;
                continue;
            }

            lines.Add(current);
            current = word;
            if (lines.Count == maxLines)
            {
                truncated = true;
                break;
            }
        }

        if (!truncated && current.Length > 0 && lines.Count < maxLines)
            lines.Add(current);
        else if (truncated && lines.Count > 0)
            lines[^1] = AddEllipsis(lines[^1], maxCharacters);

        if (!truncated && lines.Count == maxLines &&
            string.Join(" ", lines).Length < text.Length)
            lines[^1] = AddEllipsis(lines[^1], maxCharacters);

        return lines;
    }

    private static string AddEllipsis(string line, int maxCharacters)
    {
        const string ellipsis = "…";
        if (line.Length + ellipsis.Length <= maxCharacters) return line + ellipsis;
        var available = Math.Max(1, maxCharacters - ellipsis.Length);
        var shortened = line[..Math.Min(line.Length, available)].TrimEnd();
        var lastSpace = shortened.LastIndexOf(' ');
        if (lastSpace > 0) shortened = shortened[..lastSpace];
        return shortened + ellipsis;
    }

    private static string NormalizeInlineWhitespace(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ");
}
