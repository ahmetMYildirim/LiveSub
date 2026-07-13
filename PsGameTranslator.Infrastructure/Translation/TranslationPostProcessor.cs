using System.Text.RegularExpressions;
using PsGameTranslator.Core.Translation;

namespace PsGameTranslator.Infrastructure.Translation;

public sealed class TranslationPostProcessor
{
    private readonly GlossaryDictionaryManager _glossary;
    private readonly TranslationSettings _settings;

    public TranslationPostProcessor(GlossaryDictionaryManager glossary, TranslationSettings settings)
    {
        _glossary = glossary;
        _settings = settings;
    }

    public string Process(string sourceText, string translation)
    {
        if (string.IsNullOrWhiteSpace(translation))
            return string.Empty;

        var result = translation.Trim();

        // Built-in corrections
        result = result.Replace("Ejderhenin", "Ejderhanın", StringComparison.Ordinal)
            .Replace("ejderhenin", "ejderhanın", StringComparison.Ordinal);

        if (sourceText.Contains("dragon", StringComparison.OrdinalIgnoreCase))
            result = Regex.Replace(result, @"\byılan\b", "ejderha", RegexOptions.IgnoreCase);

        if (sourceText.Contains("fury", StringComparison.OrdinalIgnoreCase))
            result = Regex.Replace(result, @"\b(?:huzme|fura|furan)\b", "öfke", RegexOptions.IgnoreCase);

        result = ApplyGameTermCorrections(sourceText, result);

        // Glossary corrections — sorted by priority (highest first via GetMergedTerms)
        result = ApplyGlossaryCorrections(sourceText, result);

        return result;
    }

    /// <summary>
    /// Source-aware fixes for common OPUS-MT mistakes on RPG/guild vocabulary
    /// (Part H). Each rule only fires when the English source proves the term is
    /// present, so unrelated Turkish text is never touched.
    /// </summary>
    private static string ApplyGameTermCorrections(string sourceText, string translation)
    {
        var exact = TryApplyPreferredPhrase(sourceText);
        if (!string.IsNullOrWhiteSpace(exact))
            return exact;

        var result = translation;

        if (sourceText.Contains("guild hall", StringComparison.OrdinalIgnoreCase))
        {
            // Untranslated leftovers first.
            result = Regex.Replace(result, @"\bguild hall\b", "lonca salonu", RegexOptions.IgnoreCase);

            // "Welcome to the guild hall." — normalize any suffix/word-order variant
            // ("Lonca salonu hoş geldiniz", "Lonca salonuna hoşgeldin", ...).
            if (sourceText.Contains("welcome to the guild hall", StringComparison.OrdinalIgnoreCase))
            {
                result = Regex.Replace(
                    result,
                    @"[Ll]onca salonu\S*\s+hoş\s?geldin\w*",
                    "lonca salonuna hoş geldiniz");
            }
        }

        if (sourceText.Contains("vocation", StringComparison.OrdinalIgnoreCase))
        {
            // Untranslated leftovers and the common wrong sense "çağrı" (calling).
            result = Regex.Replace(result, @"\bvocations\b", "meslekler", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\bvocation\b", "meslek", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\bvokasyonlar\w*\b", "meslekler", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\bvokasyon\w*\b", "meslek", RegexOptions.IgnoreCase);
        }

        if (sourceText.Contains("procedure", StringComparison.OrdinalIgnoreCase))
        {
            result = Regex.Replace(result, @"\bprosedürler\w*\b", "işlemler", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\bprosedür\w*\b", "işlem", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\bprocedures\b", "işlemler", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\bprocedure\b", "işlem", RegexOptions.IgnoreCase);
        }

        // "differing/different vocations" → "farklı meslekler" (after the vocation
        // fixes above, wrong adjectives like "değişen" may still remain).
        if (sourceText.Contains("differing vocations", StringComparison.OrdinalIgnoreCase) ||
            sourceText.Contains("different vocations", StringComparison.OrdinalIgnoreCase))
        {
            result = Regex.Replace(
                result, @"\b(?:değişik|değişen|farklı)\s+meslek\w*", "farklı meslekler",
                RegexOptions.IgnoreCase);
            result = Regex.Replace(
                result, @"\b(?:differing|different)\s+vocations\b", "farklı meslekler",
                RegexOptions.IgnoreCase);
        }

        // "jack of all trades (is a) master of none" — "trade" here is a vocation,
        // never "ticaret" (commerce). Full proverb first, then the fragments.
        if (sourceText.Contains("jack of all trades", StringComparison.OrdinalIgnoreCase))
        {
            result = Regex.Replace(
                result, @"\bjack of all trades is a master of none\b",
                "her işten anlayan, hiçbirinin ustası değildir", RegexOptions.IgnoreCase);
            result = Regex.Replace(
                result, @"\bjack of all trades\b", "her işten anlayan kişi", RegexOptions.IgnoreCase);
            // Common OPUS mistranslations built on "ticaret".
            result = Regex.Replace(
                result, @"\b(?:tüm|bütün|her)\s+ticaret\w*\s+(?:krikosu|ustası|işçisi)\w*",
                "her işten anlayan kişi", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\bticaretlerin\b", "işlerin", RegexOptions.IgnoreCase);
        }

        if (sourceText.Contains("master of none", StringComparison.OrdinalIgnoreCase))
        {
            result = Regex.Replace(
                result, @"\bmaster of none\b", "hiçbirinin ustası değil", RegexOptions.IgnoreCase);
            result = Regex.Replace(
                result, @"\bhiçbir şeyin ustası(?:\s+değil\w*)?", "hiçbirinin ustası değil",
                RegexOptions.IgnoreCase);
        }

        // "hone our/your skills" → "becerileri geliştirmek" (OPUS often picks
        // "bilemek" = to sharpen a blade, or leaves the phrase untranslated).
        if (sourceText.Contains("hone our skills", StringComparison.OrdinalIgnoreCase))
        {
            result = Regex.Replace(
                result, @"\bhone our skills\b", "becerilerimizi geliştirmek", RegexOptions.IgnoreCase);
            result = Regex.Replace(
                result, @"\bbecerilerimizi bile\w+", "becerilerimizi geliştirmeliyiz", RegexOptions.IgnoreCase);
        }

        if (sourceText.Contains("hone your skills", StringComparison.OrdinalIgnoreCase))
        {
            result = Regex.Replace(
                result, @"\bhone your skills\b", "becerilerini geliştirmek", RegexOptions.IgnoreCase);
            result = Regex.Replace(
                result, @"\bbecerilerini bile\w+", "becerilerini geliştirmelisin", RegexOptions.IgnoreCase);
        }

        // "Brig" (the two-masted ship class, e.g. Assassin's Creed IV naval combat)
        // vs. "bridge" — OPUS-MT has never seen the vessel sense and guesses "köprü"
        // (bridge/overpass) or a generic noun like "kasa" (crate/safe) instead. Only
        // fires when the source doesn't also mention an actual bridge.
        if (Regex.IsMatch(sourceText, @"\bbrigs?\b", RegexOptions.IgnoreCase) &&
            !sourceText.Contains("bridge", StringComparison.OrdinalIgnoreCase))
        {
            var isPlural = sourceText.Contains("brigs", StringComparison.OrdinalIgnoreCase);
            result = Regex.Replace(result, @"\bköprüler\b", "brikler", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\bköprü\b", isPlural ? "brikler" : "brik", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\bkasalar\b", "brikler", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\bkasa\b", isPlural ? "brikler" : "brik", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\brüşvetler\b", "brikler", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\brüşvet\b", isPlural ? "brikler" : "brik", RegexOptions.IgnoreCase);
        }

        // "privateer" (a state-sanctioned pirate captain) — OPUS-MT often drops the
        // noun entirely and translates only "private" (özel), producing "bir özel
        // olarak" instead of "bir korsan olarak".
        if (Regex.IsMatch(sourceText, @"\bprivateers?\b", RegexOptions.IgnoreCase))
        {
            var isPlural = sourceText.Contains("privateers", StringComparison.OrdinalIgnoreCase);
            result = Regex.Replace(
                result, @"\bbir\s+özel(?:\s+olarak)?\b",
                isPlural ? "birer korsan olarak" : "bir korsan olarak", RegexOptions.IgnoreCase);
        }

        return result;
    }

    private static string TryApplyPreferredPhrase(string sourceText)
    {
        var normalized = Regex.Replace(sourceText.Trim().ToLowerInvariant(), @"\s+", " ");

        return normalized switch
        {
            "come to think of it, we're all of differing vocations, aren't we?" =>
                "Düşününce, hepimiz farklı mesleklerdeniz, değil mi?",
            "we must each of us hone our skills; a jack of all trades is a master of none." =>
                "Her birimiz becerilerimizi geliştirmeliyiz; her işten anlayan, hiçbirinin ustası değildir.",
            "greetings! welcome to the guild hall." =>
                "Selamlar! Lonca salonuna hoş geldiniz.",
            "here we conduct all manner of procedures pertaining to vocations." =>
                "Burada mesleklerle ilgili her türlü işlemi yapıyoruz.",
            _ => string.Empty,
        };
    }

    /// <summary>Returns relevant glossary terms for a source text (for Ollama prompt building).</summary>
    public IReadOnlyList<GlossaryTerm> GetRelevantTerms(string sourceText) =>
        _glossary.GetRelevantTerms(sourceText);

    private string ApplyGlossaryCorrections(string sourceText, string translation)
    {
        if (!_settings.EnableGlossaryCorrections)
            return translation;

        var terms = _glossary.GetRelevantTerms(sourceText);
        if (terms.Count == 0) return translation;

        var result = translation;
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term.SourceTerm)) continue;

            if (term.IsProtected && !term.ShouldTranslate)
            {
                result = ReplaceProtectedTerm(result, term.TargetTerm);
            }
            else if (term.ShouldTranslate && !string.IsNullOrWhiteSpace(term.TargetTerm))
            {
                if (term.SourceTerm.Length >= 4)
                    result = ApplyPhraseCorrection(result, term);
            }
        }
        return result;
    }

    private static string ReplaceProtectedTerm(string translation, string targetTerm)
    {
        return Regex.Replace(
            translation,
            @"\b" + Regex.Escape(targetTerm) + @"\b",
            targetTerm,
            RegexOptions.IgnoreCase);
    }

    private static string ApplyPhraseCorrection(string translation, GlossaryTerm term)
    {
        // Only active for user corrections with explicit target term
        // Conservative: only replace when translation literally contains the source term
        // as a whole word/phrase — without \b this also matched substrings inside
        // unrelated words (e.g. a "well" entry corrupting "wellness"), which becomes
        // a real risk once the glossary holds thousands of common-word entries.
        if (translation.Contains(term.SourceTerm, StringComparison.OrdinalIgnoreCase))
        {
            return Regex.Replace(
                translation,
                @"\b" + Regex.Escape(term.SourceTerm) + @"\b",
                term.TargetTerm,
                RegexOptions.IgnoreCase);
        }
        return translation;
    }
}
