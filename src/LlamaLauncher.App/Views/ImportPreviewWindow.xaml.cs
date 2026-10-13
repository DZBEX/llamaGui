using System.Collections.ObjectModel;
using System.Windows;
using LlamaLauncher.Application.Exchange;

namespace LlamaLauncher.App.Views;

/// <summary>配置导入预览窗口。</summary>
public partial class ImportPreviewWindow : Window
{
    public ImportPreviewWindow(ImportPlan plan)
    {
        InitializeComponent();
        Rows = new ObservableCollection<ImportPreviewRow>(plan.Items.Select(i => new ImportPreviewRow(i)));
        Warnings = new ObservableCollection<string>(plan.Warnings);
        if (!plan.SchemaSupported && plan.SchemaError is not null)
        {
            Warnings.Insert(0, plan.SchemaError);
        }

        DataContext = this;
        ItemsGrid.ItemsSource = Rows;
    }

    public ObservableCollection<ImportPreviewRow> Rows { get; }

    public ObservableCollection<string> Warnings { get; }

    public IReadOnlySet<int>? SelectedIndexes => Rows
        .Where(r => r.Apply && r.Item.Action is ImportItemAction.Add or ImportItemAction.Overwrite && !r.Item.HasErrors)
        .Select(r => r.Item.Index)
        .ToHashSet();

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (SelectedIndexes is { Count: 0 })
        {
            MessageBox.Show(this, "没有选择任何可导入的条目。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>导入预览行。</summary>
public sealed class ImportPreviewRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private bool _apply;

    public ImportPreviewRow(ImportPlanItem item)
    {
        Item = item;
        _apply = item.Action is ImportItemAction.Add or ImportItemAction.Overwrite && !item.HasErrors;
    }

    public ImportPlanItem Item { get; }

    public bool Apply
    {
        get => _apply;
        set => SetProperty(ref _apply, value);
    }

    public bool CanApply => Item.Action is ImportItemAction.Add or ImportItemAction.Overwrite && !Item.HasErrors;

    public string ProfileName => Item.ProfileName;

    public string ActionText => Item.Action switch
    {
        ImportItemAction.Add => "新增",
        ImportItemAction.Overwrite => "覆盖同名的现有配置",
        ImportItemAction.Skip => "跳过",
        _ => "无效",
    };

    public string ModelStatusText => Item.ModelFileExists
        ? "文件存在，将自动登记"
        : "模型缺失（导入后可重新定位）";

    public string IssuesText => Item.Issues.Count == 0
        ? string.Empty
        : string.Join("；", Item.Issues.Select(i => i.Message));
}
