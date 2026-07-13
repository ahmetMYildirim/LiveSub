using System.Text.Json.Serialization;

namespace PsGameTranslator.Core.Translation;

public sealed class GlossaryTerm
{
    public string SourceTerm { get; set; } = string.Empty;
    public string TargetTerm { get; set; } = string.Empty;

    // Primary field — array of category tags.
    public string[] Categories { get; set; } = [];

    // Backward-compatible singular field present in older JSON files.
    // Written only when not null so new saves don't include it.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }

    public bool ShouldTranslate { get; set; } = true;
    public bool IsProtected { get; set; }
    public GlossaryMatchMode MatchMode { get; set; } = GlossaryMatchMode.Phrase;
    public bool CaseSensitive { get; set; } = false;
    public int Priority { get; set; } = 0;
    public string Notes { get; set; } = string.Empty;

    // Computed display string — not serialised.
    [JsonIgnore]
    public string CategoriesDisplay =>
        Categories.Length > 0
            ? string.Join(", ", Categories)
            : Category ?? string.Empty;

    // Source-type tag set by GlossaryDictionaryManager — not serialised.
    [JsonIgnore]
    public string SourceDictionary { get; set; } = string.Empty;
}
