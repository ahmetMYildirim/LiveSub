namespace PsGameTranslator.App.ViewModels.User;

public sealed class TranslationPageViewModel : ObservableObject
{
    public TranslationPageViewModel(TranslationViewModel translation)
    {
        Translation = translation;
    }

    public TranslationViewModel Translation { get; }
}
