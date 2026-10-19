using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Workflows;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.App.ViewModels.Lab;

/// <summary>设计与调试：运行、断点、单步、运行轨迹、节点详情与日志。</summary>
public sealed partial class WorkflowDesignerViewModel
{
    private readonly Dictionary<string, NodeRunState> _runStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nodeStatusTexts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeEdges = new(StringComparer.Ordinal);
    private WorkflowRunHandle? _handle;

    [ObservableProperty]
    private string _runStateText = "未运行";

    [ObservableProperty]
    private string _pauseReasonText = string.Empty;

    [ObservableProperty]
    private bool _isRunActive;

    [ObservableProperty]
    private string _runSummaryText = "尚未运行。运行前会自动校验工作流。";

    [ObservableProperty]
    private string _runInputsJson = "{}";

    [ObservableProperty]
    private WorkflowTestMode _runTestMode = WorkflowTestMode.LocalModel;

    public IReadOnlyList<WorkflowTestMode> RunTestModes { get; } = new[]
    {
        WorkflowTestMode.LocalModel,
        WorkflowTestMode.Simulated,
    };

    public string RunModeHint => RunTestMode == WorkflowTestMode.Simulated
        ? "模拟模式：不会调用模型；决策节点需要模拟响应（可在“测试用例”页配置后用批量测试运行）。"
        : "本地模型模式：真实调用已启动的 llama-server 实例（决策节点需绑定或存在就绪实例）。";

    public string RunModeText => RunTestMode == WorkflowTestMode.Simulated ? "模拟" : "本地模型";

    partial void OnRunTestModeChanged(WorkflowTestMode value)
    {
        OnPropertyChanged(nameof(RunModeHint));
        OnPropertyChanged(nameof(RunModeText));
    }

    [ObservableProperty]
    private int _logsLevelFilter;

    [ObservableProperty]
    private string _logsSearchText = string.Empty;

    [ObservableProperty]
    private string _detailTitle = "未选择节点";

    [ObservableProperty]
    private string? _detailTiming;

    [ObservableProperty]
    private string? _detailInput;

    [ObservableProperty]
    private string? _detailOutput;

    [ObservableProperty]
    private string? _detailVariables;

    [ObservableProperty]
    private string? _detailDiagnostics;

    [ObservableProperty]
    private string? _detailError;

    [ObservableProperty]
    private bool _hasNodeDetail;

    [ObservableProperty]
    private string? _lastRunId;

    public ObservableCollection<NodeStateRow> NodeStates { get; } = new();

    public ObservableCollection<WorkflowLogRow> RunLogs { get; } = new();

    public IReadOnlyDictionary<string, NodeRunState> RunStates => _runStates;

    public IReadOnlyDictionary<string, string> NodeStatusTexts => _nodeStatusTexts;

    public IReadOnlyCollection<string> ActiveEdgeIds => _activeEdges;

    public IReadOnlyList<int> LogLevelFilters { get; } = new[] { 0, 1, 2, 3 };

    partial void OnLogsLevelFilterChanged(int value) => ApplyLogFilter();

    partial void OnLogsSearchTextChanged(string value) => ApplyLogFilter();

    [RelayCommand]
    private void GenerateRunInputsTemplate()
    {
        var template = new JsonObject();
        foreach (var variable in Definition.Inputs)
        {
            JsonNode? value = null;
            if (variable.ExampleJson is not null)
            {
                value = WorkflowJson.TryParse(variable.ExampleJson, out _);
            }
            else if (variable.DefaultValueJson is not null)
            {
                value = WorkflowJson.TryParse(variable.DefaultValueJson, out _);
            }

            template[variable.Name] = value;
        }

        RunInputsJson = WorkflowJson.Serialize(template);
    }

    [RelayCommand]
    private Task StartRunAsync() => StartRunCoreAsync(stepMode: false, startAtNodeId: null);

    [RelayCommand]
    private Task StartStepRunAsync() => StartRunCoreAsync(stepMode: true, startAtNodeId: null);

    [RelayCommand]
    private async Task StartFromNodeAsync()
    {
        if (SelectedNode is not { } node)
        {
            Notifications?.Notify("请先在画布中选择要从其开始的节点。");
            return;
        }

        var snapshot = await (Dialogs?.PromptAsync(
            "从选中节点开始",
            "提供起始上下文快照（{\"variables\":{},\"upstream\":{\"<上游节点ID>\":{\"out\":…}}}）。\n" +
            "上游未包含的节点会被跳过并视为已执行；缺少必要数据时会给出明确错误。",
            """{"variables":{},"upstream":{}}""") ?? Task.FromResult<string?>(null));
        if (snapshot is null)
        {
            return;
        }

        await StartRunCoreAsync(stepMode: false, startAtNodeId: node.Id, startSnapshotJson: snapshot).ConfigureAwait(true);
    }

    private async Task StartRunCoreAsync(bool stepMode, string? startAtNodeId, string? startSnapshotJson = null)
    {
        if (IsRunActive)
        {
            Notifications?.Notify("当前已有运行在进行中。");
            return;
        }

        var inputs = WorkflowJson.TryParse(RunInputsJson, out var inputsError) as JsonObject;
        if (inputs is null)
        {
            await (Dialogs?.ShowErrorAsync("运行输入无效", $"运行输入必须是 JSON 对象：{inputsError}") ?? Task.CompletedTask);
            return;
        }

        var report = await _runner.ValidateAsync(Definition, forRun: true).ConfigureAwait(true);
        Issues.Clear();
        foreach (var issue in report.OrderedIssues)
        {
            Issues.Add(new DesignerValidationRow { Issue = issue });
        }

        IssuesSummaryText = report.Summary;
        if (report.HasErrors)
        {
            RunSummaryText = "运行被阻止：存在校验错误（见校验面板）。";
            Notifications?.Notify("运行被阻止：请先修正校验错误。");
            return;
        }

        ResetRunVisuals();
        RunStateText = "启动中…";
        IsRunActive = true;

        IReadOnlyList<string>? preExecuted = null;
        if (startAtNodeId is not null)
        {
            preExecuted = Definition.Nodes
                .Select(n => n.Id)
                .Where(id => id != startAtNodeId && !IsDescendant(id, startAtNodeId))
                .ToList();
        }

        var options = new WorkflowLaunchOptions
        {
            Definition = Definition.Clone(),
            Inputs = inputs,
            TestMode = RunTestMode,
            Observer = new DesignerObserver(this),
            StartInStepMode = stepMode,
            StartAtNodeId = startAtNodeId,
            StartSnapshotJson = startSnapshotJson,
            PreExecutedNodeIds = preExecuted,
        };

        try
        {
            _handle = _runner.Start(options);
            LastRunId = _handle.RunId;
            RunSummaryText = $"运行中（RunId：{_handle.RunId}）…";
            _ = WatchCompletionAsync(_handle);
            Notifications?.SetStatus("工作流开始运行。");
        }
        catch (Exception ex)
        {
            IsRunActive = false;
            RunSummaryText = "启动失败：" + ex.Message;
            await (Dialogs?.ShowErrorAsync("启动失败", ex.Message) ?? Task.CompletedTask);
        }
    }

    private bool IsDescendant(string nodeId, string ancestorId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(ancestorId);
        visited.Add(ancestorId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in Definition.OutgoingEdges(current))
            {
                if (edge.TargetNodeId == nodeId)
                {
                    return true;
                }

                if (visited.Add(edge.TargetNodeId))
                {
                    queue.Enqueue(edge.TargetNodeId);
                }
            }
        }

        return false;
    }

    private async Task WatchCompletionAsync(WorkflowRunHandle handle)
    {
        try
        {
            var result = await handle.Completion.ConfigureAwait(false);
            Post(() =>
            {
                IsRunActive = false;
                RunStateText = result.State.ToString();
                PauseReasonText = string.Empty;
                var outputPreview = result.Output is null
                    ? "（无输出）"
                    : WorkflowJson.Compact(result.Output);
                if (outputPreview.Length > 300)
                {
                    outputPreview = outputPreview[..300] + "…";
                }

                RunSummaryText =
                    $"运行结束：{result.State} · 耗时 {result.Record.DurationMs:0} ms · 节点 {result.Record.Nodes.Count} 个" +
                    (result.Record.ErrorSummary is null ? string.Empty : $" · {result.Record.ErrorSummary}") +
                    $"\n输出：{outputPreview}";
                RunSummaryText = RunSummaryText.Replace("\n", Environment.NewLine);
                RaiseNodeDetail(SelectedNode?.Id);
                RecomputeActiveEdges();
                RunVisualsChanged?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex)
        {
            Post(() =>
            {
                IsRunActive = false;
                RunStateText = "运行异常";
                RunSummaryText = "运行异常：" + ex.Message;
            });
        }
    }

    [RelayCommand]
    private void PauseRun()
    {
        _handle?.Pause();
    }

    [RelayCommand]
    private void ResumeRun()
    {
        _handle?.Resume();
    }

    [RelayCommand]
    private void StepRun()
    {
        _handle?.Step();
    }

    [RelayCommand]
    private void StopRun()
    {
        _handle?.Cancel();
    }

    [RelayCommand]
    private void ClearRunVisuals()
    {
        ResetRunVisuals();
        RunSummaryText = "已清除运行轨迹。";
    }

    private void ResetRunVisuals()
    {
        _runStates.Clear();
        _nodeStatusTexts.Clear();
        _activeEdges.Clear();
        _nodeRecords.Clear();
        _allLogs.Clear();
        RunLogs.Clear();
        NodeStates.Clear();
        foreach (var node in Definition.Nodes)
        {
            NodeStates.Add(new NodeStateRow { NodeId = node.Id, Name = node.Name });
        }

        DetailTitle = SelectedNode is null ? "未选择节点" : SelectedNode.Name;
        HasNodeDetail = false;
        DetailInput = DetailOutput = DetailVariables = DetailDiagnostics = DetailError = DetailTiming = null;
        RunVisualsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── 运行事件（观察者回调，已切回 UI 线程） ─────────────────

    private void HandleRunState(WorkflowRunState state, WorkflowPauseReason? reason, string? message)
    {
        RunStateText = state switch
        {
            WorkflowRunState.Created => "已创建",
            WorkflowRunState.Validating => "校验中",
            WorkflowRunState.Ready => "就绪",
            WorkflowRunState.Running => "运行中",
            WorkflowRunState.Pausing => "暂停中",
            WorkflowRunState.Paused => "已暂停",
            WorkflowRunState.WaitingForUser => "等待人工确认",
            WorkflowRunState.Succeeded => "成功",
            WorkflowRunState.Failed => "失败",
            WorkflowRunState.Canceling => "取消中",
            WorkflowRunState.Canceled => "已取消",
            WorkflowRunState.TimedOut => "超时",
            _ => state.ToString(),
        };
        PauseReasonText = reason switch
        {
            WorkflowPauseReason.UserRequest => "（用户暂停，可继续或单步）",
            WorkflowPauseReason.Breakpoint => "（命中节点断点）",
            WorkflowPauseReason.StepCompleted => "（单步完成）",
            _ => string.Empty,
        };
        IsRunActive = state is WorkflowRunState.Running
            or WorkflowRunState.Pausing
            or WorkflowRunState.Paused
            or WorkflowRunState.WaitingForUser
            or WorkflowRunState.Validating
            or WorkflowRunState.Canceling;
        if (message is { Length: > 0 })
        {
            RunSummaryText = message;
        }
    }

    private void HandleNodeRecord(NodeRunRecord record)
    {
        _nodeRecords[record.NodeId] = record;
        _runStates[record.NodeId] = record.State;
        _nodeStatusTexts[record.NodeId] = record.State switch
        {
            NodeRunState.Running => "正在执行…",
            NodeRunState.Succeeded => $"成功 · {record.DurationMs:0} ms",
            NodeRunState.Failed => "失败：" + Trim(record.ErrorMessage, 40),
            NodeRunState.Skipped => "已跳过",
            NodeRunState.Canceled => "已取消",
            NodeRunState.TimedOut => "超时",
            NodeRunState.PausedAtBreakpoint => "断点暂停",
            NodeRunState.WaitingForUser => "等待人工确认",
            _ => "等待执行",
        };

        var row = NodeStates.FirstOrDefault(r => r.NodeId == record.NodeId);
        if (row is null)
        {
            row = new NodeStateRow { NodeId = record.NodeId };
            NodeStates.Add(row);
        }

        row.Name = record.Name;
        row.State = record.State;
        row.Started = record.StartedUtc is not null;
        row.StateText = record.State.ToString();
        row.DurationText = record.DurationMs is null ? "—" : $"{record.DurationMs:0} ms";
        row.Retries = record.Retries;
        row.Branch = record.BranchTaken;
        row.Error = record.ErrorMessage;

        if (SelectedNode?.Id == record.NodeId)
        {
            RaiseNodeDetail(record.NodeId);
        }

        RecomputeActiveEdges();
        RunVisualsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleLog(WorkflowLogEntry entry)
    {
        _allLogs.Add(entry);
        if (_allLogs.Count > WorkflowLimits.MaxLogEntriesPerRun * 2)
        {
            _allLogs.RemoveRange(0, _allLogs.Count - WorkflowLimits.MaxLogEntriesPerRun * 2);
        }

        ApplyLogFilter();
    }

    private void ApplyLogFilter()
    {
        var threshold = LogsLevelFilter switch
        {
            1 => WorkflowLogLevel.Info,
            2 => WorkflowLogLevel.Warning,
            3 => WorkflowLogLevel.Error,
            _ => WorkflowLogLevel.Trace,
        };
        var search = LogsSearchText?.Trim() ?? string.Empty;
        RunLogs.Clear();
        foreach (var entry in _allLogs
                     .Where(e => e.Level >= threshold)
                     .Where(e => search.Length == 0
                         || e.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                         || (e.NodeId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                     .TakeLast(2000))
        {
            RunLogs.Add(new WorkflowLogRow { Entry = entry });
        }
    }

    private void RecomputeActiveEdges()
    {
        _activeEdges.Clear();
        foreach (var edge in Definition.Edges)
        {
            if (!_nodeRecords.TryGetValue(edge.SourceNodeId, out var source))
            {
                continue;
            }

            if (source.State != NodeRunState.Succeeded)
            {
                continue;
            }

            if (edge.SourcePort == NodeTypeCatalog.ErrorPort)
            {
                continue;
            }

            var branches = source.BranchTaken?.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (branches is { Length: > 0 })
            {
                if (edge.BranchId is not null && branches.Contains(edge.BranchId, StringComparer.Ordinal))
                {
                    _activeEdges.Add(edge.Id);
                }
            }
            else if (edge.BranchId is null)
            {
                _activeEdges.Add(edge.Id);
            }
        }
    }

    private void RaiseNodeDetail(string? nodeId)
    {
        if (nodeId is null)
        {
            DetailTitle = "未选择节点";
            HasNodeDetail = false;
            DetailInput = DetailOutput = DetailVariables = DetailDiagnostics = DetailError = DetailTiming = null;
            return;
        }

        var node = Definition.FindNode(nodeId);
        DetailTitle = node?.Name ?? nodeId;
        if (!_nodeRecords.TryGetValue(nodeId, out var record))
        {
            HasNodeDetail = false;
            DetailTiming = "尚未执行。";
            DetailInput = DetailOutput = DetailVariables = DetailDiagnostics = DetailError = null;
            return;
        }

        HasNodeDetail = true;
        DetailTiming =
            $"状态：{record.State} · 开始：{(record.StartedUtc?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "—")} · " +
            $"耗时：{(record.DurationMs is null ? "不可用" : $"{record.DurationMs:0} ms")} · 重试：{record.Retries}" +
            (record.BranchTaken is null ? string.Empty : $" · 分支：{record.BranchTaken}");
        DetailInput = Pretty(record.InputJson, record.InputTruncated);
        DetailOutput = Pretty(record.OutputJson, record.OutputTruncated);
        DetailVariables = Pretty(record.VariablesJson, false);
        DetailDiagnostics = Pretty(record.ModelDiagnosticsJson, false);
        DetailError = record.ErrorMessage is null
            ? null
            : record.ErrorMessage + (record.ErrorContextJson is null ? string.Empty : "\n" + Pretty(record.ErrorContextJson, false));
    }

    private static string? Pretty(string? json, bool truncated)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var formatted = json;
        try
        {
            var node = WorkflowJson.TryParse(json, out _);
            if (node is not null)
            {
                formatted = WorkflowJson.Serialize(node);
            }
        }
        catch
        {
            // 保持原文。
        }

        return truncated ? formatted + "\n（内容已截断，完整内容见运行历史）" : formatted;
    }

    [RelayCommand]
    private async Task CopyDetailAsync(string what)
    {
        var text = what switch
        {
            "input" => DetailInput,
            "output" => DetailOutput,
            "variables" => DetailVariables,
            "diagnostics" => DetailDiagnostics,
            "error" => DetailError,
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

    private static string? Trim(string? text, int length)
        => text is null || text.Length <= length ? text : text[..length] + "…";

    private static void Post(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    /// <summary>运行观察者：把引擎事件切回 UI 线程并转交设计器。</summary>
    private sealed class DesignerObserver : IWorkflowRunObserver
    {
        private readonly WorkflowDesignerViewModel _owner;

        public DesignerObserver(WorkflowDesignerViewModel owner) => _owner = owner;

        public void OnRunStateChanged(WorkflowRunState state, WorkflowPauseReason? pauseReason, string? message)
            => Post(() => _owner.HandleRunState(state, pauseReason, message));

        public void OnNodeStateChanged(NodeRunRecord record)
            => Post(() => _owner.HandleNodeRecord(record));

        public void OnLog(WorkflowLogEntry entry)
            => Post(() => _owner.HandleLog(entry));

        public void OnWaitingForUser(WorkflowUserPrompt prompt)
        {
        }
    }
}
