using System.Windows;
using System.Windows.Controls;
using LlamaLauncher.App.ViewModels.Lab;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.App.Views;

/// <summary>运行历史视图：双选（Ctrl）同步到比较功能。</summary>
public partial class RunHistoryView : UserControl
{
    public RunHistoryView() => InitializeComponent();

    private void OnClearWorkflowFilter(object sender, RoutedEventArgs e)
    {
        if (DataContext is RunHistoryViewModel vm)
        {
            vm.FilterWorkflow = null;
            vm.RefreshCommand.Execute(null);
        }
    }

    private void OnRunSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not RunHistoryViewModel vm)
        {
            return;
        }

        var selected = RunList.SelectedItems.OfType<WorkflowRunSummary>().ToList();
        if (selected.Count > 0)
        {
            vm.SelectedRun = selected[0];
        }

        vm.SetCompareSecond(selected.Count > 1 ? selected[1] : null);
    }
}
