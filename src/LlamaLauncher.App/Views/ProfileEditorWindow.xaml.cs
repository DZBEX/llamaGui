using System.Windows;
using LlamaLauncher.App.ViewModels;

namespace LlamaLauncher.App.Views;

public partial class ProfileEditorWindow : Window
{
    public ProfileEditorWindow(ProfileEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += () => Close();
    }
}
