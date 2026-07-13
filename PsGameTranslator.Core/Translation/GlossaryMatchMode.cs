namespace PsGameTranslator.Core.Translation;

public enum GlossaryMatchMode
{
    Phrase = 0,   // Source term appears anywhere inside the subtitle (default)
    Exact = 1,    // Entire subtitle equals source term
    Contains = 2, // Loose case-insensitive substring match
}
