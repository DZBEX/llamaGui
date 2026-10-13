using System.Windows.Controls;
using LlamaLauncher.App.ViewModels;

namespace LlamaLauncher.App.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ProfilesViewModel oldViewModel)
        {
            oldViewModel.EditRequested -= OnEditRequested;
            oldViewModel.ImportPreviewRequested -= OnImportPreviewRequested;
        }

        if (e.NewValue is ProfilesViewModel newViewModel)
        {
            newViewModel.EditRequested += OnEditRequested;
            newViewModel.ImportPreviewRequested += OnImportPreviewRequested;
        }
    }

    private void OnEditRequested(ProfileEditorViewModel editor)
    {
        var window = new ProfileEditorWindow(editor)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        window.ShowDialog();
    }

    private async void OnImportPreviewRequested(LlamaLauncher.Application.Exchange.ImportPlan plan)
    {
        if (DataContext is not ProfilesViewModel viewModel)
        {
            return;
        }

        var window = new ImportPreviewWindow(plan)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        if (window.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var (added, overwritten, skipped) = await viewModel.ApplyImportAsync(plan, window.SelectedIndexes);
            System.Windows.MessageBox.Show(
                System.Windows.Application.Current!.MainWindow,
                $"导入完成：新增 {added}，覆盖 {overwritten}，跳过 {skipped}。",
                "导入完成",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                System.Windows.Application.Current!.MainWindow,
                "导入失败：" + ex.Message,
                "导入失败",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }
}
