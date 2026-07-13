using System.Windows;
using PsGameTranslator.App.ViewModels;

namespace PsGameTranslator.App.Views;

public partial class SetupWizardWindow : Window
{
    public SetupWizardWindow(SetupWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += () => Dispatcher.BeginInvoke(Close);
    }
}
