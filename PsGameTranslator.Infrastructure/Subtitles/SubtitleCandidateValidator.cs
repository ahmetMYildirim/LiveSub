using PsGameTranslator.Core.Subtitles;
using System.Text.RegularExpressions;

namespace PsGameTranslator.Infrastructure.Subtitles;

public sealed class SubtitleCandidateValidationResult
{
    public bool IsValid { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string NormalizedText { get; init; } = string.Empty;
}

/// <summary>
/// Rejects OCR garbage and platform/UI noise before a candidate subtitle ever
/// reaches the capture queue, the translation queue, the overlay, or the
/// learning dataset. Pure/stateless — callers own diagnostics counters.
/// </summary>
public sealed class SubtitleCandidateValidator
{
    private static readonly HashSet<string> KnownOcrGarbage = new(StringComparer.OrdinalIgnoreCase)
    {
        "s", "t", "a", "m", "m s", "arud i", "16", "49 b", "7", "0",
    };

    private static readonly HashSet<string> ControllerAndHudLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "lt", "rt", "rb", "lb", "grab", "dash", "jump", "switch weapon skill",
        "sheathe/draw", "front kick", "menu", "map", "inventory", "switch",
        "help!", "wait!", "to me!", "go!", "press r",
    };

    private static readonly string[] UiPlatformNoisePhrases =
    [
        "share", "save", "search", "subscribe", "payla", "kaydet", "soru",
        "4k", "60fps", "no commentary", "full game", "full gameplay",
    ];

    private static readonly HashSet<string> ValidShortSubtitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "no!", "go!", "run!", "stop!", "wait!", "ok.", "yes.",
    };

    public SubtitleCandidateValidationResult Validate(string? rawText)
    {
        var text = (rawText ?? string.Empty).Trim();
        var normalized = SubtitleTextNormalizer.NormalizeKey(text);

        if (normalized.Length == 0)
            return Reject(normalized, "empty");

        // Allow-list short but real dialogue interjections before any length/garbage check.
        if (ValidShortSubtitles.Contains(normalized))
            return Accept(normalized);

        if (normalized.Length < 3)
            return Reject(normalized, "too_short");

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // One or two isolated letters, e.g. "s", "t", "a", "m s".
        if (tokens.Length is 1 or 2 && tokens.All(t => t.Length == 1 && char.IsLetter(t[0])))
            return Reject(normalized, "isolated_letters");

        if (KnownOcrGarbage.Contains(normalized))
            return Reject(normalized, "known_ocr_garbage");

        // Mostly digits/symbols (e.g. "16", "49 B", "7", "0").
        var letterCount = normalized.Count(char.IsLetter);
        var nonSpaceCount = normalized.Count(c => !char.IsWhiteSpace(c));
        if (nonSpaceCount > 0 && letterCount < nonSpaceCount * 0.4)
            return Reject(normalized, "mostly_digits_or_symbols");

        // Only reject when the whole line IS basically the UI label/tag itself
        // (short, e.g. a bare "Save" button or "No Commentary" watermark) — a
        // plain substring check with no length guard also matched real dialogue
        // that merely contains one of these words, e.g. "God save him,"
        // wrongly rejected as ui_platform_noise:save.
        if (normalized.Length <= 24)
        {
            foreach (var phrase in UiPlatformNoisePhrases)
            {
                if (normalized.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    return Reject(normalized, $"ui_platform_noise:{phrase}");
            }
        }

        return Accept(normalized);
    }

    /// <summary>
    /// Strict replacement-mode gate. Anything rejected here must never create a
    /// mask, enter translation/memory, or reach the overlay.
    /// </summary>
    public SubtitleCandidateValidationResult IsValidForReplacementMode(string? candidate)
    {
        var baseResult = Validate(candidate);
        if (!baseResult.IsValid)
            return baseResult;

        var normalized = baseResult.NormalizedText;
        if (ControllerAndHudLabels.Contains(normalized))
            return Reject(normalized, "hud_or_controller_label");

        if (Regex.IsMatch(normalized, @"^(sheathe\s*/\s*draw|switch weapon skill|front kick|grab|dash|jump)\b", RegexOptions.IgnoreCase))
            return Reject(normalized, "hud_control_command");

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length <= 2 && normalized.Length <= 8 &&
            normalized.All(c => char.IsUpper(c) || char.IsWhiteSpace(c) || char.IsDigit(c)))
            return Reject(normalized, "short_all_caps_fragment");

        if (LooksLikeRepeatedNonsense(tokens))
            return Reject(normalized, "repeated_nonsense");

        return baseResult;
    }

    private static bool LooksLikeRepeatedNonsense(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 3) return false;
        var distinct = tokens.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return distinct <= Math.Max(1, tokens.Count / 3);
    }

    private static SubtitleCandidateValidationResult Accept(string normalized) => new()
    {
        IsValid = true,
        Reason = "accepted",
        NormalizedText = normalized,
    };

    private static SubtitleCandidateValidationResult Reject(string normalized, string reason) => new()
    {
        IsValid = false,
        Reason = reason,
        NormalizedText = normalized,
    };
}
