namespace PsGameTranslator.Core.Translation;

public sealed class TranslationRefinementRequest
{
    public string SourceText { get; init; } = string.Empty;
    public string MachineTranslatedText { get; init; } = string.Empty;
    public string SourceLanguage { get; init; } = "en";
    public string TargetLanguage { get; init; } = "tr";
    public string GameProfileName { get; init; } = string.Empty;
    public string Genre { get; init; } = string.Empty;
    public IReadOnlyList<GlossaryTerm> RelevantGlossaryTerms { get; init; } = [];
    public IReadOnlyList<string> PreviousContextLines { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}
