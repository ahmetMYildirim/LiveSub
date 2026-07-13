namespace PsGameTranslator.Core.Models;

public sealed class TranslationRequest
{
    public string SourceText { get; init; } = string.Empty;
    public string SourceLanguage { get; init; } = "en";
    public string TargetLanguage { get; init; } = "tr";
    public string? SpeakerName { get; init; }
    public string? GameProfileName { get; init; }
    public string? Genre { get; init; }
    public IReadOnlyList<string> PreviousContextLines { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> RelevantGlossaryTerms { get; init; } =
        new Dictionary<string, string>();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    // Compatibility aliases used by the existing Ollama implementation.
    public string GameProfile
    {
        get => GameProfileName ?? "default";
        init => GameProfileName = value;
    }

    public List<string> GlossaryTerms
    {
        get => RelevantGlossaryTerms.Keys.ToList();
        init => RelevantGlossaryTerms = value.ToDictionary(term => term, term => term);
    }

    public List<string> ContextLines
    {
        get => PreviousContextLines.ToList();
        init => PreviousContextLines = value;
    }
}
