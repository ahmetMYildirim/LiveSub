using System.Text.RegularExpressions;
using PsGameTranslator.Core.Models;
using PsGameTranslator.Core.Ocr;

namespace PsGameTranslator.Ocr;

public sealed class OcrResultScorer
{
    private static readonly HashSet<string> HardRejected = new(StringComparer.OrdinalIgnoreCase)
    {
        "arud i", "m s", "lt", "rt", "lb", "rb", "4k", "60fps",
    };

    private static readonly string[] HudPhrases =
    [
        "sheathe/draw", "switch weapon skill", "front kick", "dash", "grab",
        "jump", "press r", "no commentary", "subscribe", "share", "save",
        "payla", "kaydet", "menu", "map", "inventory",
    ];

    private static readonly string[] SentenceHints =
    [
        "the", "you", "we", "i", "it", "are", "is", "must", "come",
        "think", "welcome", "skills", "vocation", "dragon",
    ];

    public BestOcrSelection SelectBest(
        IEnumerable<OcrResult> results,
        string previousSubtitleKey = "")
    {
        var scored = results
            .Select(result => Score(result, previousSubtitleKey))
            .OrderByDescending(item => item.Score)
            .ToArray();

        var accepted = scored.FirstOrDefault(item => item.Accepted);
        var rejected = scored
            .Where(item => accepted is null || !ReferenceEquals(item.Result, accepted.Result))
            .Select(item => new RejectedOcrResult
            {
                Result = item.Result,
                Score = item.Score,
                Reason = item.Reason,
            })
            .ToArray();

        if (accepted is null || !accepted.Accepted)
        {
            var first = scored.FirstOrDefault();
            return new BestOcrSelection
            {
                BestResult = first?.Result ?? new OcrResult(),
                Score = first?.Score ?? 0,
                RejectedResults = rejected,
                Reasons = scored.Select(item => item.Reason).ToArray(),
            };
        }

        var (speaker, dialogue) = SplitSpeaker(accepted.CandidateText);
        return new BestOcrSelection
        {
            BestResult = accepted.Result,
            CandidateText = accepted.CandidateText,
            SpeakerName = speaker,
            DialogueText = dialogue,
            Score = accepted.Score,
            RejectedResults = rejected,
            Reasons = [accepted.Reason],
        };
    }

    public OcrScoreResult Score(OcrResult result, string previousSubtitleKey = "")
    {
        if (!result.Success)
            return Reject(result, "provider_failed", -100);

        var candidateText = NormalizeCandidate(result);
        var normalized = NormalizeKey(candidateText);
        if (string.IsNullOrWhiteSpace(normalized))
            return Reject(result, "empty", -100);

        if (HardRejected.Contains(normalized))
            return Reject(result, "hard_rejected_ocr_garbage", -100);

        if (normalized.Length <= 2)
            return Reject(result, "too_short", -90);

        if (normalized.All(c => char.IsDigit(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c)))
            return Reject(result, "mostly_digits_or_symbols", -90);

        foreach (var hud in HudPhrases)
        {
            if (normalized.Contains(hud, StringComparison.OrdinalIgnoreCase))
                return Reject(result, $"hud_control_text:{hud}", -80);
        }

        var score = 0.0;
        // Confidence and provider reliability are the only signals that actually
        // reflect real read quality — everything below is a soft heuristic that
        // garbled text can trigger just as easily as clean text (a stray period
        // from misread glyphs still "has subtitle punctuation", a fragment like
        // "heai.d" still contains the substring "i"). Previously confidence*40
        // plus a narrow 3-6 reliability spread were too small to survive a single
        // wrong heuristic swing, so a noisier-but-lucky read from a less reliable
        // engine could outscore a clean, high-confidence read from a better one.
        score += Math.Clamp(result.Confidence, 0, 1) * 55;
        score += Math.Min(25, normalized.Length / 3.0);
        score += result.Lines.Count is >= 1 and <= 3 ? 15 : -10;
        score += HasSubtitlePunctuation(candidateText) ? 8 : 0;
        score += SentenceHints.Any(hint => Regex.IsMatch(normalized, $@"\b{Regex.Escape(hint)}\b")) ? 8 : 0;
        score += SubtitleBandScore(result);
        score += ReliabilityWeight(result.ProviderName);

        if (!string.IsNullOrWhiteSpace(previousSubtitleKey))
        {
            var similarity = Similarity(previousSubtitleKey, normalized);
            if (similarity > 0.98) score -= 8;
            else if (similarity > 0.65) score += 4;
        }

        return new OcrScoreResult(result, candidateText, score, score > 20, "accepted");
    }

    private static OcrScoreResult Reject(OcrResult result, string reason, double score) =>
        new(result, NormalizeCandidate(result), score, false, reason);

    private static string NormalizeCandidate(OcrResult result)
    {
        var lines = result.Lines.Count > 0
            ? result.Lines.Select(line => line.Text)
            : (result.Text ?? string.Empty).Split('\n');

        return string.Join(
            Environment.NewLine,
            lines.Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string NormalizeKey(string text)
    {
        var normalized = text.ToLowerInvariant().Trim();
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized;
    }

    private static bool HasSubtitlePunctuation(string text) =>
        text.IndexOfAny(['.', '?', '!', ',', ';', ':', '\'']) >= 0;

    private static double SubtitleBandScore(OcrResult result)
    {
        var boxed = result.Lines.Where(line => line.HasBoundingBox).ToArray();
        if (boxed.Length == 0) return 0;

        var averageY = boxed.Average(line => line.RelativeCenterY >= 0 ? line.RelativeCenterY : -1);
        if (averageY < 0) return 0;
        if (averageY is >= 0.45 and <= 0.92) return 10;
        if (averageY > 0.92) return -6;
        return -2;
    }

    // Spread widened from the old 3/4/6 range — PaddleOCR has been observed
    // consistently reading stylized game fonts far more accurately than
    // WindowsOCR on this app's captures, and the previous narrow gap let a
    // single noisy heuristic (e.g. a stray punctuation mark) flip the winner.
    private static double ReliabilityWeight(string providerName)
    {
        if (providerName.Contains("Paddle", StringComparison.OrdinalIgnoreCase)) return 14;
        if (providerName.Contains("Rapid", StringComparison.OrdinalIgnoreCase)) return 8;
        if (providerName.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return 6;
        return 0;
    }

    private static (string Speaker, string Dialogue) SplitSpeaker(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length >= 2 && lines[0].Length is > 0 and <= 24 && lines[1].Length > lines[0].Length)
            return (lines[0], string.Join(Environment.NewLine, lines.Skip(1)));
        return (string.Empty, text.Trim());
    }

    private static double Similarity(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1;
        var aTokens = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var bTokens = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (aTokens.Count == 0 || bTokens.Count == 0) return 0;
        var intersection = aTokens.Intersect(bTokens).Count();
        var union = aTokens.Union(bTokens).Count();
        return union == 0 ? 0 : intersection / (double)union;
    }
}

public sealed record OcrScoreResult(
    OcrResult Result,
    string CandidateText,
    double Score,
    bool Accepted,
    string Reason);
