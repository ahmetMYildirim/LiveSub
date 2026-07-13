namespace PsGameTranslator.Infrastructure.Translation;

internal static class TranslationContextWindow
{
    internal const int MaxLines = 3;
    internal const int MaxCharacters = 2000;
    internal static IReadOnlyList<string> Build(IEnumerable<string> history, string currentSource)
    {
        var current = currentSource.Trim();
        var selected = new List<string>(MaxLines);
        var characterCount = 0;
        foreach (var line in history.Reverse())
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || string.Equals(trimmed, current, StringComparison.Ordinal))
                continue;
            var remaining = MaxCharacters - characterCount;
            if (remaining <= 0) break;
            if (trimmed.Length > remaining) trimmed = trimmed[..remaining];
            selected.Add(trimmed);
            characterCount += trimmed.Length;
            if (selected.Count >= MaxLines) break;
        }
        selected.Reverse();
        return selected;
    }
    internal static string Join(IEnumerable<string> lines) =>
        string.Join("\n", lines.Select(line => line.Trim()).Where(line => line.Length > 0));
}