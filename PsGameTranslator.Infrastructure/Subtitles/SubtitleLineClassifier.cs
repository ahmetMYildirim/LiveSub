using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Subtitles;

namespace PsGameTranslator.Infrastructure.Subtitles;

/// <summary>
/// Classifies OCR lines into dialogue subtitle candidates vs tutorial/HUD prompts.
/// Example: selects "Shit, no!" and rejects
/// "Press R near the O'Driscoll to hogtie them".
/// </summary>
public sealed class SubtitleLineClassifier
{
    private static readonly string[] ActionVerbs =
    [
        "hogtie", "open", "pick up", "loot", "mount", "reload",
        "interact", "inspect", "aim", "shoot", "press", "hold",
    ];

    // Multi-character controller tokens reject on their own; single letters
    // (R, L, B, X, Y) only count together with another prompt indicator,
    // otherwise normal dialogue like "I saw a man" would be destroyed.
    // "A" was deliberately removed: it is a standalone capital only when it
    // starts a sentence as the article ("A friend once told me...") — an
    // extremely common way for real dialogue to open, so it produced false
    // "button:A" signals on plain narration. Genuine "Press A to ..." prompts
    // are still caught independently by the contains_press rule above.
    private static readonly string[] StrongButtonTokens =
        ["RB", "LB", "RT", "LT", "R1", "R2", "L1", "L2", "□", "△", "○", "×"];
    private static readonly string[] WeakButtonTokens = ["R", "L", "B", "X", "Y"];

    private static readonly Regex ToActionVerbRegex = new(
        @"\bto\s+(hogtie|open|pick\s+up|loot|mount|reload|interact|inspect|aim|shoot|press|hold)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<SubtitleLineClassifier> _logger;
    private readonly SubtitleCandidateValidator _validator;

    public SubtitleLineClassifier(
        ILogger<SubtitleLineClassifier> logger,
        SubtitleCandidateValidator validator)
    {
        _logger = logger;
        _validator = validator;
    }

    public SubtitleLineSelectionResult Classify(
        IReadOnlyList<OcrLine> lines,
        int cropWidth,
        int cropHeight,
        GameProfile profile,
        SubtitleFilterSettings settings,
        bool rejectHudControlText = true,
        bool useCandidateScoring = true)
    {
        if (lines.Count == 0)
        {
            return new SubtitleLineSelectionResult
            {
                HasSubtitleCandidate = false,
                FilteringApplied = false,
                RejectionReasons = "no_ocr_lines",
            };
        }

        if (!settings.EnableSubtitleLineFiltering || !profile.EnableSubtitleLineFiltering)
        {
            return PassThrough(lines, "filtering_disabled");
        }

        foreach (var line in lines)
            line.ComputeRelative(cropWidth, cropHeight);

        var hasGeometry = cropWidth > 0 && cropHeight > 0 && lines.Any(l => l.HasBoundingBox);
        var (bandTop, bandBottom) = ResolveBand(settings);

        var selected = new List<OcrLine>();
        var rejected = new List<OcrLine>();
        var reasons = new StringBuilder();

        foreach (var line in lines)
        {
            var text = line.Text.Trim();
            if (text.Length == 0) continue;

            var validation = _validator.IsValidForReplacementMode(text);
            if (rejectHudControlText && !validation.IsValid)
            {
                rejected.Add(line);
                AppendReason(reasons, text, validation.Reason);
                continue;
            }

            if (rejectHudControlText && IsTutorialOrHudPrompt(text, profile, out var promptReason))
            {
                rejected.Add(line);
                AppendReason(reasons, text, promptReason);
                continue;
            }

            // Band is a soft preference in the scoring path, not a hard gate:
            // when it hard-rejected here, a game that renders its dialogue in the
            // lower part of the user's crop (with only a short SPEAKER name plate
            // up top inside the band) had every real dialogue line dropped, so
            // just the name plate survived and then formatted to empty —
            // producing an endless "translation_not_enqueued_empty_ocr" with no
            // translation at all. ScoreCandidate still rewards in-band lines, so
            // in-band dialogue keeps winning; out-of-band dialogue is only picked
            // when nothing better is in band. HUD/tutorial text stays hard-
            // rejected by the validator and prompt rules above. The non-scoring
            // path keeps the strict band gate (it has no scoring to fall back on).
            if (!useCandidateScoring && rejectHudControlText && hasGeometry && line.HasBoundingBox &&
                (line.RelativeCenterY < bandTop || line.RelativeCenterY > bandBottom))
            {
                rejected.Add(line);
                AppendReason(reasons, text,
                    $"outside_band (centerY={line.RelativeCenterY:F2}, band={bandTop:F2}-{bandBottom:F2})");
                continue;
            }

            if (hasGeometry && line.HasBoundingBox &&
                line.CenterX / cropWidth > 0.72 &&
                (text.Length < 45 || !HasSentencePunctuation(text)))
            {
                rejected.Add(line);
                AppendReason(reasons, text, "right_hud_area");
                continue;
            }

            selected.Add(line);
        }

        // Dialogue preference: keep reading order (top to bottom) and limit to
        // the first few lines — real subtitles appear as one or two lines.
        var anchor = useCandidateScoring
            ? selected.OrderByDescending(line => ScoreCandidate(line, cropWidth, cropHeight, bandTop, bandBottom)).FirstOrDefault()
            : null;
        var subtitleBlock = !useCandidateScoring
            ? selected.ToList()
            : anchor is null
                ? new List<OcrLine>()
                : selected.Where(line => IsSameSubtitleBlock(anchor, line, cropHeight)).ToList();
        foreach (var unrelated in selected.Except(subtitleBlock))
        {
            rejected.Add(unrelated);
            AppendReason(reasons, unrelated.Text, "not_best_subtitle_block");
        }

        var ordered = OrderReadingOrder(subtitleBlock);
        if (ordered.Count > 3)
        {
            foreach (var extra in ordered.Skip(3))
            {
                rejected.Add(extra);
                AppendReason(reasons, extra.Text, "exceeds_max_subtitle_lines");
            }
            ordered = ordered.Take(3).ToList();
        }

        var selectedText = string.Join("\n", ordered.Select(l => l.Text.Trim()));

        _logger.LogInformation(
            "subtitle_line_classification - total={Total}, selected={Selected}, rejected={Rejected}, band={BandTop:F2}-{BandBottom:F2}",
            lines.Count, ordered.Count, rejected.Count, bandTop, bandBottom);

        return new SubtitleLineSelectionResult
        {
            SelectedSubtitleLines = ordered,
            RejectedHudLines = rejected,
            RejectionReasons = reasons.ToString(),
            SelectedText = selectedText,
            HasSubtitleCandidate = ordered.Count > 0,
            FilteringApplied = true,
        };
    }

    /// <summary>
    /// PaddleOCR returns boxes in its own internal detection order, not
    /// guaranteed left-to-right/top-to-bottom — a plain sort by Y alone leaves
    /// same-row fragments (or two lines that sit at nearly identical height)
    /// in detection order, which can jumble word order within a line (e.g.
    /// "Captain s Ship ours. and make" instead of "Captain and make his Ship
    /// ours."). Bucket boxes into visual rows by Y-proximity first, then read
    /// each row strictly left-to-right.
    /// </summary>
    private static List<OcrLine> OrderReadingOrder(List<OcrLine> lines)
    {
        var withBoxes = lines.Where(l => l.HasBoundingBox).OrderBy(l => l.Y).ToList();
        var withoutBoxes = lines.Where(l => !l.HasBoundingBox).ToList();

        var rows = new List<List<OcrLine>>();
        foreach (var line in withBoxes)
        {
            var row = rows.Count > 0 ? rows[^1] : null;
            var rowThreshold = row is not null ? row[0].Height * 0.6 : 0;
            if (row is not null && Math.Abs(line.CenterY - row[0].CenterY) <= rowThreshold)
                row.Add(line);
            else
                rows.Add([line]);
        }

        var result = rows.SelectMany(row => row.OrderBy(l => l.X)).ToList();
        result.AddRange(withoutBoxes);
        return result;
    }

    private static double ScoreCandidate(
        OcrLine line,
        int cropWidth,
        int cropHeight,
        double bandTop,
        double bandBottom)
    {
        var text = line.Text.Trim();
        var score = Math.Clamp(line.Confidence, 0, 1) * 4;
        if (text.Length is >= 4 and <= 140) score += 2;
        if (text.Contains(' ')) score += 1.5;
        if (HasSentencePunctuation(text)) score += 1.5;
        if (Regex.IsMatch(text, @"\b(i|you|we|they|he|she|it|is|are|was|were|have|has|will|can|must|do|did)\b", RegexOptions.IgnoreCase))
            score += 1.5;

        if (line.HasBoundingBox && cropWidth > 0 && cropHeight > 0)
        {
            if (line.CenterX / cropWidth > 0.72) score -= 5;
            // In-band lines are preferred; out-of-band ones are penalised but
            // still eligible, so real dialogue below the band can win when no
            // in-band dialogue exists (only a name plate sits in the band).
            score += (line.RelativeCenterY >= bandTop && line.RelativeCenterY <= bandBottom) ? 2 : -2;
        }

        // Name-plate penalty: a bare "EDWARD KENWAY" / "ARTHUR MORGAN" style
        // speaker label is 1-3 all-caps words — a character-length cap (used
        // previously) missed real names right at the boundary (e.g. "EDWARD
        // KENWAY" is 13 chars, one past a 12-char cap), letting the name plate
        // outscore the real dialogue line next to it and get picked as the
        // subtitle anchor instead. Word count is a more reliable signal here.
        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount <= 3 && text.All(c => !char.IsLetter(c) || char.IsUpper(c))) score -= 5;
        return score;
    }

    private static bool IsSameSubtitleBlock(OcrLine anchor, OcrLine candidate, int cropHeight)
    {
        if (!anchor.HasBoundingBox || !candidate.HasBoundingBox || cropHeight <= 0)
            return true;

        var maxVerticalDistance = Math.Max(60, cropHeight * 0.22);
        return Math.Abs(candidate.CenterY - anchor.CenterY) <= maxVerticalDistance;
    }

    private static bool HasSentencePunctuation(string text) =>
        text.Contains('.') || text.Contains('!') || text.Contains('?') || text.Contains(':');

    // ── Tutorial / HUD prompt rules ──────────────────────────────────────────────

    private static bool IsTutorialOrHudPrompt(string text, GameProfile profile, out string reason)
    {
        reason = string.Empty;

        if (text.StartsWith("Press ", StringComparison.OrdinalIgnoreCase))
        {
            reason = "starts_with_press";
            return true;
        }

        if (text.Contains("Press ", StringComparison.OrdinalIgnoreCase))
        {
            reason = "contains_press";
            return true;
        }

        if (text.Contains("near the", StringComparison.OrdinalIgnoreCase))
        {
            reason = "contains_near_the";
            return true;
        }

        if (ToActionVerbRegex.IsMatch(text))
        {
            reason = "to_action_verb";
            return true;
        }

        foreach (var pattern in profile.HudNoisePatterns)
        {
            if (pattern.Length > 0 &&
                text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"hud_noise_pattern:{pattern}";
                return true;
            }
        }

        var hasSentencePunctuation =
            text.Contains('.') || text.Contains('!') || text.Contains('?');

        var promptSignals = 0;
        var matchedSignals = new List<string>();

        foreach (var token in StrongButtonTokens)
        {
            if (ContainsStandaloneToken(text, token))
            {
                reason = $"controller_button:{token}";
                return true;
            }
        }

        foreach (var token in WeakButtonTokens)
        {
            if (ContainsStandaloneToken(text, token, caseSensitive: true))
            {
                promptSignals++;
                matchedSignals.Add($"button:{token}");
                break;
            }
        }

        foreach (var verb in ActionVerbs)
        {
            if (ContainsWord(text, verb))
            {
                promptSignals++;
                matchedSignals.Add($"action_verb:{verb}");
                break;
            }
        }

        foreach (var pattern in profile.TutorialPromptPatterns)
        {
            if (pattern.Length > 0 &&
                text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                promptSignals++;
                matchedSignals.Add($"profile_pattern:{pattern}");
                if (promptSignals >= 2) break;
            }
        }

        // Real UI prompts are short fragments without sentence punctuation
        // ("R to hogtie them"). Any properly punctuated line ("A man should
        // hold his tongue.") is real dialogue regardless of how many weak
        // signals it happens to contain — punctuation alone is enough to
        // clear it, rather than only mattering when there is exactly one
        // signal (that used to let a 2-signal combination reject a fully
        // punctuated sentence, which is what silently ate real dialogue
        // lines mid-conversation).
        if (promptSignals >= 1 && !hasSentencePunctuation)
        {
            reason = "prompt_signals:" + string.Join("+", matchedSignals);
            return true;
        }

        return false;
    }

    private static bool ContainsStandaloneToken(string text, string token, bool caseSensitive = false)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var index = 0;
        while ((index = text.IndexOf(token, index, comparison)) >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterIndex = index + token.Length;
            var afterOk = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);
            if (beforeOk && afterOk) return true;
            index += token.Length;
        }
        return false;
    }

    private static bool ContainsWord(string text, string word) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static (double Top, double Bottom) ResolveBand(SubtitleFilterSettings settings) =>
        settings.SubtitleBandMode switch
        {
            SubtitleBandMode.FullCrop => (0.0, 1.0),
            SubtitleBandMode.CenterBand => (0.25, 0.80),
            // UpperBand and Custom both honor the configured percentages
            // (UpperBand defaults: 0.00 - 0.55).
            _ => (Math.Clamp(settings.SubtitleBandTopPercent, 0, 1),
                  Math.Clamp(settings.SubtitleBandBottomPercent, 0, 1)),
        };

    private static SubtitleLineSelectionResult PassThrough(
        IReadOnlyList<OcrLine> lines, string reason)
    {
        var text = string.Join("\n",
            lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0));
        return new SubtitleLineSelectionResult
        {
            SelectedSubtitleLines = lines,
            SelectedText = text,
            HasSubtitleCandidate = text.Length > 0,
            FilteringApplied = false,
            RejectionReasons = reason,
        };
    }

    private static void AppendReason(StringBuilder reasons, string text, string reason)
    {
        if (reasons.Length > 0) reasons.Append("; ");
        var preview = text.Length <= 40 ? text : text[..40] + "…";
        reasons.Append($"\"{preview}\" -> {reason}");
    }
}
