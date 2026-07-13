using System.Text.RegularExpressions;
using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Core.Subtitles;

/// <summary>
/// Splits OCR subtitle lines into an optional speaker-name line and dialogue lines
/// (Part A/B). Deliberately conservative: a line only becomes a speaker name when it
/// looks like a name plate (short, title-cased, unpunctuated, above real dialogue) and
/// is provably not a HUD/control label, an interjection, or OCR garbage — a false
/// speaker silently deletes dialogue words from the translation, which is worse than
/// occasionally translating a name.
/// </summary>
public sealed partial class SpeakerNameDetector
{
    private const int MaxSpeakerWords = 4;
    private const int MaxSpeakerLength = 32;

    // Control/HUD vocabulary that must never be treated as a speaker name (Part I).
    private static readonly HashSet<string> HudControlLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "lt", "rt", "lb", "rb", "l1", "l2", "r1", "r2", "l3", "r3",
        "grab", "dash", "jump", "sprint", "crouch", "attack", "block", "parry",
        "sheathe/draw", "sheathe / draw", "switch weapon skill", "front kick",
        "menu", "map", "inventory", "switch", "interact", "examine", "talk",
        "to me!", "help!", "wait!", "go!",
    };

    // Verbs that start HUD prompts ("Press R", "Open your inventory", "Hold LT"...).
    private static readonly HashSet<string> HudPromptVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "press", "hold", "tap", "open", "close", "select", "switch", "sheathe",
        "draw", "equip", "use", "view", "toggle", "aim", "climb", "mount",
    };

    /// <summary>Parses already-cleaned, non-empty OCR content lines.</summary>
    public ParsedSubtitleCandidate Parse(IReadOnlyList<string> contentLines, string rawOcrText)
    {
        var lines = contentLines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
        var rejectedCandidates = new List<string>();

        if (lines.Count == 0)
        {
            return new ParsedSubtitleCandidate
            {
                RawOcrText = rawOcrText,
                SourceLines = lines,
                RejectionReason = "empty",
            };
        }

        if (lines.Count == 1)
        {
            // A lone name-plate line ("Klaus") with no dialogue underneath: reject —
            // do not translate a bare name, do not update the overlay (Part B rule 3).
            var (isSpeaker, reason) = IsSpeakerNameLine(lines[0], dialogueBelow: null);
            if (isSpeaker)
            {
                return new ParsedSubtitleCandidate
                {
                    RawOcrText = rawOcrText,
                    SourceLines = lines,
                    SpeakerName = CleanSpeaker(lines[0]),
                    SpeakerLine = lines[0],
                    RejectionReason = "speaker_only_no_dialogue",
                };
            }

            if (reason is not null) rejectedCandidates.Add($"{lines[0]} ({reason})");
            return new ParsedSubtitleCandidate
            {
                RawOcrText = rawOcrText,
                SourceLines = lines,
                DialogueText = lines[0],
                DialogueLines = [lines[0]],
                Confidence = 1.0,
                RejectedSpeakerCandidates = rejectedCandidates,
            };
        }

        var (firstIsSpeaker, firstReason) = IsSpeakerNameLine(lines[0], dialogueBelow: lines[1]);
        if (!firstIsSpeaker && firstReason is not null)
            rejectedCandidates.Add($"{lines[0]} ({firstReason})");

        var dialogueLines = firstIsSpeaker ? lines.Skip(1).ToList() : lines;
        var dialogueText = NormalizeInlineWhitespace(string.Join(" ", dialogueLines));

        return new ParsedSubtitleCandidate
        {
            RawOcrText = rawOcrText,
            SourceLines = lines,
            SpeakerName = firstIsSpeaker ? CleanSpeaker(lines[0]) : null,
            SpeakerLine = firstIsSpeaker ? lines[0] : null,
            DialogueText = dialogueText,
            DialogueLines = dialogueLines,
            Confidence = firstIsSpeaker ? 0.9 : 1.0,
            RejectedSpeakerCandidates = rejectedCandidates,
        };
    }

    /// <summary>
    /// Returns (isSpeaker, rejectionReason). rejectionReason is null when the line was
    /// never a plausible candidate to begin with (e.g. a long sentence).
    /// </summary>
    public (bool IsSpeaker, string? RejectionReason) IsSpeakerNameLine(string line, string? dialogueBelow)
    {
        var candidate = line.Trim();
        // "Klaus:" name plates — the colon is a strong speaker signal, strip it for checks.
        var endsWithNameColon = candidate.EndsWith(':') && !candidate.EndsWith("::", StringComparison.Ordinal);
        var core = endsWithNameColon ? candidate[..^1].TrimEnd() : candidate;

        if (core.Length == 0 || core.Length > MaxSpeakerLength)
            return (false, null);

        // Sentence punctuation → dialogue/interjection ("Go!", "Wait!", "Yes."), never a name.
        if (Regex.IsMatch(core, @"[.!?,;…]"))
            return (false, "sentence_punctuation");

        var words = core.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 1 or > MaxSpeakerWords)
            return (false, words.Length > MaxSpeakerWords ? "too_many_words" : null);

        if (HudControlLabels.Contains(core))
            return (false, "hud_control_label");

        if (HudPromptVerbs.Contains(words[0]))
            return (false, "hud_prompt_verb");

        // Contains '/' or digits → control label ("Sheathe/Draw", "Press R2"), not a name.
        if (core.Contains('/') || core.Any(char.IsDigit))
            return (false, "control_or_numeric");

        // Mostly non-letters → OCR garbage, not a name.
        var letterCount = core.Count(char.IsLetter);
        if (letterCount < core.Count(c => !char.IsWhiteSpace(c)) * 0.8)
            return (false, "mostly_symbols");

        // Every word must be name-like: capitalized first letter followed by lowercase
        // ("Klaus", "Abigail Roberts", "Guild Master") or a particle ("of", "the", "van").
        // Short ALL-CAPS tokens ("LT", "RB", "ARUD I") are controller/garbage, not names.
        foreach (var word in words)
        {
            var isParticle = word is "of" or "the" or "van" or "von" or "de" or "da" or "el";
            var isTitleCase = char.IsUpper(word[0]) && word.Skip(1).All(char.IsLower) && word.Length >= 2;
            var isLongAllCaps = word.Length >= 4 && word.All(char.IsUpper);
            if (!isParticle && !isTitleCase && !isLongAllCaps)
                return (false, "not_name_like");
        }

        // Speaker names sit above real dialogue; the dialogue line should look like a
        // sentence, and the name line should be the shorter of the two.
        if (dialogueBelow is not null)
        {
            if (core.Length >= dialogueBelow.Trim().Length && !endsWithNameColon)
                return (false, "not_shorter_than_dialogue");
            return (true, null);
        }

        // No dialogue below: only treat 1–2 title-case words as a lone name plate.
        return words.Length <= 2 ? (true, null) : (false, "no_dialogue_below");
    }

    private static string CleanSpeaker(string line)
    {
        var cleaned = line.Trim();
        if (cleaned.EndsWith(':')) cleaned = cleaned[..^1].TrimEnd();
        return cleaned;
    }

    private static string NormalizeInlineWhitespace(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ");
}
