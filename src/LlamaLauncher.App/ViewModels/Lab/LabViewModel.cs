using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.App.Services;

namespace LlamaLauncher.App.ViewModels.Lab;

/// <summary>实验室子页签。</summary>
public sealed record LabTab(string Title, string Glyph, string Description, object Page);

/// <summary>决策模型实验室：容器页（模型调试 / 工作流设计与调试 / 测试用例 / 批量测试 / 运行历史）。</summary>
public sealed partial class LabViewModel : ViewModelBase, IPageViewModel
{
    public LabViewModel(
        ModelDebugViewModel modelDebug,
        WorkflowDesignerViewModel designer,
        TestCaseViewModel testCases,
        BatchTestViewModel batchTest,
        RunHistoryViewModel history)
    {
        Tabs = new ObservableCollection<LabTab>
        {
            new("模型调试", "◈", "单次输入调试决策模型，查看结构化输出与校验报告", modelDebug),
            new("工作流设计与调试", "⑂", "可视化编辑工作流、运行、断点与运行轨迹", designer),
            new("测试用例", "☑", "维护单场景测试数据、模拟响应与断言", testCases),
            new("批量测试", "≣", "批量执行测试集，统计成功/失败与耗时", batchTest),
            new("运行历史", "◷", "查询运行记录、比较差异、复现与导出诊断", history),
        };
        CurrentTab = Tabs[0];
    }

    public ObservableCollection<LabTab> Tabs { get; }

    [ObservableProperty]
    private LabTab? _currentTab;

    public object? CurrentPage => CurrentTab?.Page;

    partial void OnCurrentTabChanged(LabTab? value)
    {
        OnPropertyChanged(nameof(CurrentPage));
        if (value?.Page is IPageViewModel page)
        {
            _ = page.OnActivatedAsync();
        }
    }

    [RelayCommand]
    private void SelectTab(LabTab? tab)
    {
        if (tab is not null)
        {
            CurrentTab = tab;
        }
    }

    public Task OnActivatedAsync()
    {
        if (CurrentPage is IPageViewModel page)
        {
            return page.OnActivatedAsync();
        }

        return Task.CompletedTask;
    }
}
