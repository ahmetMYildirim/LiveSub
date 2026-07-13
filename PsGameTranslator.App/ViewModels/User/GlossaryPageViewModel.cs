namespace PsGameTranslator.App.ViewModels.User;

public sealed class GlossaryPageViewModel : ObservableObject
{
    public GlossaryPageViewModel(GlossaryViewModel glossary)
    {
        Glossary = glossary;
    }

    public GlossaryViewModel Glossary { get; }
}
