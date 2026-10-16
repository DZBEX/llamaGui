using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Workflows;
using LlamaLauncher.Application.Workflows.Testing;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain.Workflows;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.ViewModels.Lab;

/// <summary>历史节点行。</summary>
public sealed class HistoryNodeRow
{
    public required NodeRunRecord Record { get; init; }

    public string Name => Record.Name;

    public string StateText => Record.State.ToString();

    public string DurationText => Record.DurationMs is null ? "—" : $"{Record.DurationMs:0} ms";

    public int Retries => Record.Retries;

    public string? Branch => Record.BranchTaken;

    public string? Error => Record.ErrorMessage;
}

/// <summary>
/// 运行历史：查询与过滤、详情（节点/日志/输入输出/变量）、两次运行比较（字段级差异）、
/// 复制为新用例、导出诊断包与按保留策略清理。
/// </summary>
public sealed partial class RunHistoryViewModel : ViewModelBase, IPageViewModel
{
    private readonly IWorkflowRunStore _runs;
    private readonly IWorkflowDefinitionStore _definitions;
    private readonly IWorkflowTestCaseStore _testCases;
    private readonly ILogger<RunHistoryViewModel> _logger;
    private WorkflowRunRecord? _detailRecord;
    private WorkflowRunSummary? _compareSecond;

    public RunHistoryViewModel(
        IWorkflowRunStore runs,
        IWorkflowDefinitionStore definitions,
        IWorkflowTestCaseStore testCases,
        INotificationService notifications,
        IDialogService dialogs,
        IClipboardService clipboard,
        ILogger<RunHistoryViewModel> logger)
    {
        _runs = runs;
        _definitions = definitions;
        _testCases = testCases;
        _logger = logger;
        Notifications = notifications;
        Dialogs = dialogs;
        Clipboard = clipboard;
    }

    private IClipboardService Clipboard { get; }

    public ObservableCollection<WorkflowRunSummary> Runs { get; } = new();

    public ObservableCollection<WorkflowDefinitionSummary> WorkflowChoices { get; } = new();

    public ObservableCollection<HistoryNodeRow> DetailNodes { get; } = new();

    public ObservableCollection<WorkflowLogRow> DetailLogs { get; } = new();

    public ObservableCollection<JsonFieldChange> ComparisonChanges { get; } = new();

    public ObservableCollection<string> ComparisonNotes { get; } = new();

    public IReadOnlyList<string> StateFilters { get; } = new[]
    {
        "全部", "Succeeded", "Failed", "Canceled", "TimedOut",
    };

    [ObservableProperty]
    private WorkflowDefinitionSummary? _filterWorkflow;

    [ObservableProperty]
    private string _stateFilter = "全部";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private WorkflowRunSummary? _selectedRun;

    [ObservableProperty]
    private string _detailSummaryText = "选择一条运行记录查看详情。";

    [ObservableProperty]
    private string? _detailInput;

    [ObservableProperty]
    private string? _detailOutput;

    [ObservableProperty]
    private string? _detailVariables;

    [ObservableProperty]
    private string? _detailModelContext;

    [ObservableProperty]
    private string _comparisonSummaryText = "在列表中选择两条运行记录后点击“比较”。";

    public Task OnActivatedAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var workflows = await _definitions.ListAsync().ConfigureAwait(true);
            WorkflowChoices.Clear();
            foreach (var workflow in workflows)
            {
                WorkflowChoices.Add(workflow);
            }

            var query = new WorkflowRunQuery(
                WorkflowId: FilterWorkflow?.WorkflowId,
                State: StateFilter == "全部"
                    ? null
                    : Enum.TryParse<WorkflowRunState>(StateFilter, out var state) ? state : null,
                SearchText: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                Limit: 500);
            var runs = await _runs.ListAsync(query).ConfigureAwait(true);
            Runs.Clear();
            foreach (var run in runs)
            {
                Runs.Add(run);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查询运行历史失败");
            Notifications?.Notify("查询失败：" + ex.Message);
        }
    }

    partial void OnSelectedRunChanged(WorkflowRunSummary? value) => _ = LoadDetailAsync(value);

    private async Task LoadDetailAsync(WorkflowRunSummary? summary)
    {
        DetailNodes.Clear();
        DetailLogs.Clear();
        _detailRecord = null;
        if (summary is null)
        {
            DetailSummaryText = "选择一条运行记录查看详情。";
            DetailInput = DetailOutput = DetailVariables = DetailModelContext = null;
            return;
        }

        var record = await _runs.GetAsync(summary.RunId).ConfigureAwait(true);
        if (record is null)
        {
            DetailSummaryText = "运行记录已不存在。";
            return;
        }

        _detailRecord = record;
        DetailSummaryText =
            $"RunId：{record.RunId} · 工作流：{record.WorkflowNameSnapshot}（{record.WorkflowId}）" +
            $"\n状态：{record.State} · 模式：{record.TestMode} · 应用版本：{record.AppVersion}" +
            $"\n开始：{record.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 耗时：{(record.DurationMs is null ? "不可用" : $"{record.DurationMs:0} ms")}" +
            (record.VersionLabel is null ? string.Empty : $"\n版本快照：{record.VersionLabel}") +
            (record.ErrorSummary is null ? string.Empty : $"\n错误：{record.ErrorSummary}") +
            (record.ValidationSummary is null ? string.Empty : $"\n{record.ValidationSummary}");
        DetailSummaryText = DetailSummaryText.Replace("\n", Environment.NewLine);
        foreach (var node in record.Nodes)
        {
            DetailNodes.Add(new HistoryNodeRow { Record = node });
        }

        foreach (var entry in record.Logs.TakeLast(500))
        {
            DetailLogs.Add(new WorkflowLogRow { Entry = entry });
        }

        DetailInput = Pretty(record.InputSnapshotJson);
        DetailOutput = Pretty(record.OutputJson);
        DetailVariables = Pretty(record.FinalVariablesJson);
        DetailModelContext = Pretty(record.ModelContextJson);
    }

    private static string? Pretty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return WorkflowJson.Serialize(WorkflowJson.TryParse(json, out _) ?? JsonValue.Create(json));
        }
        catch
        {
            return json;
        }
    }

    /// <summary>视图多选时同步第二条选择。</summary>
    public void SetCompareSecond(WorkflowRunSummary? second) => _compareSecond = second;

    [RelayCommand]
    private async Task CompareAsync()
    {
        if (SelectedRun is null || _compareSecond is null)
        {
            Notifications?.Notify("请在列表中选择两条运行记录（按住 Ctrl 多选）后重试。");
            return;
        }

        var left = await _runs.GetAsync(SelectedRun.RunId).ConfigureAwait(true);
        var right = await _runs.GetAsync(_compareSecond.RunId).ConfigureAwait(true);
        if (left is null || right is null)
        {
            Notifications?.Notify("运行记录已被删除，无法比较。");
            return;
        }

        var comparison = WorkflowRunComparer.Compare(left, right);
        ComparisonChanges.Clear();
        foreach (var change in comparison.OutputChanges)
        {
            ComparisonChanges.Add(change);
        }

        ComparisonNotes.Clear();
        ComparisonNotes.Add($"状态：{comparison.LeftState} → {comparison.RightState}");
        ComparisonNotes.Add(comparison.TimingSummary);
        foreach (var note in comparison.NodeStateChanges)
        {
            ComparisonNotes.Add("节点变化：" + note);
        }

        if (comparison.OutputChanges.Count == 0)
        {
            ComparisonNotes.Add("输出字段没有差异。");
        }

        ComparisonSummaryText =
            $"比较 {comparison.LeftRunId} → {comparison.RightRunId}：输出差异 {comparison.OutputChanges.Count} 项，" +
            $"节点状态变化 {comparison.NodeStateChanges.Count} 项。";
    }

    [RelayCommand]
    private async Task CopyAsTestCaseAsync()
    {
        if (_detailRecord is not { } record)
        {
            Notifications?.Notify("请先选择一条运行记录。");
            return;
        }

        var name = await (Dialogs?.PromptAsync("复制为新用例", "用例名称：", $"{record.WorkflowNameSnapshot} 复现用例") ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var testCase = new WorkflowTestCase
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = name!,
            Description = $"由运行 {record.RunId} 复制（模式：{record.TestMode}）。",
            WorkflowId = record.WorkflowId,
            WorkflowVersionLabel = record.VersionLabel,
            Mode = record.TestMode,
            Inputs = WorkflowJson.TryParse(record.InputSnapshotJson, out _) as JsonObject ?? new JsonObject(),
            Tags = { "来自运行历史", record.RunId },
        };
        await _testCases.SaveAsync(testCase).ConfigureAwait(true);
        Notifications?.Notify($"已创建用例“{testCase.Name}”（可在“测试用例”页完善断言）。");
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        if (_detailRecord is not { } record)
        {
            Notifications?.Notify("请先选择一条运行记录。");
            return;
        }

        var path = Dialogs?.PickSaveFile(
            "导出运行诊断",
            "JSON 文件 (*.json)|*.json",
            $"run-{record.RunId}.json");
        if (path is null)
        {
            return;
        }

        await File.WriteAllTextAsync(path, WorkflowJson.Serialize(record)).ConfigureAwait(true);
        Notifications?.Notify($"已导出：{path}");
    }

    [RelayCommand]
    private async Task DeleteRunAsync()
    {
        if (SelectedRun is not { } summary)
        {
            return;
        }

        var confirmed = await (Dialogs?.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "删除运行记录",
            Message = $"确定删除运行 {summary.RunId}（{summary.WorkflowNameSnapshot}）？",
            PrimaryButtonText = "删除",
            IsDestructive = true,
        }) ?? Task.FromResult(false)).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await _runs.DeleteAsync(summary.RunId).ConfigureAwait(true);
        Notifications?.Notify("运行记录已删除。");
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PruneAsync()
    {
        var countText = await (Dialogs?.PromptAsync(
            "清理历史",
            $"保留最近多少条运行记录？（默认 {WorkflowLimits.DefaultHistoryRetention}）",
            WorkflowLimits.DefaultHistoryRetention.ToString()) ?? Task.FromResult<string?>(null));
        if (!int.TryParse(countText, out var keep) || keep < 0)
        {
            return;
        }

        var removed = await _runs.PruneAsync(keep).ConfigureAwait(true);
        Notifications?.Notify($"已清理 {removed} 条运行记录（保留最近 {keep} 条）。");
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CopyDetailAsync(string what)
    {
        var text = what switch
        {
            "input" => DetailInput,
            "output" => DetailOutput,
            "variables" => DetailVariables,
            "context" => DetailModelContext,
            "summary" => DetailSummaryText,
            _ => null,
        };
        if (string.IsNullOrEmpty(text))
        {
            Notifications?.Notify("没有可复制的内容。");
            return;
        }

        var (success, error) = await Clipboard.SetTextAsync(text).ConfigureAwait(true);
        Notifications?.Notify(success ? "已复制。" : error ?? "复制失败。");
    }
}
