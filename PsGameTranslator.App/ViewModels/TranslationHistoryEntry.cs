namespace PsGameTranslator.App.ViewModels;

public sealed record TranslationHistoryEntry(string SourceText, string TranslatedText, DateTime Timestamp)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss");
}
