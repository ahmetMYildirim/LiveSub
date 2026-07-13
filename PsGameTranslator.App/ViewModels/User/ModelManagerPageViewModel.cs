namespace PsGameTranslator.App.ViewModels.User;

public sealed class ModelManagerPageViewModel : ObservableObject
{
    public ModelManagerPageViewModel(ModelManagerViewModel modelManager)
    {
        ModelManager = modelManager;
    }

    public ModelManagerViewModel ModelManager { get; }
}
