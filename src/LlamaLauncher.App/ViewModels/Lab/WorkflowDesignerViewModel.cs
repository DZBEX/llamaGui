using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Workflows;
using LlamaLauncher.App.Controls;
using LlamaLauncher.App.Services;
using LlamaLauncher.Domain.Workflows;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.App.ViewModels.Lab;

/// <summary>模型服务下拉项（供节点属性面板绑定）。</summary>
public sealed record ModelChoice(string InstanceId, string DisplayName);

/// <summary>
/// 工作流设计与调试：画布编辑（节点/连线/属性）、撤销重做、校验、保存/导入导出/版本快照，
/// 以及运行、断点、单步、暂停/继续/停止与运行轨迹、节点详情与日志。
/// </summary>
public sealed partial class WorkflowDesignerViewModel : ViewModelBase, IPageViewModel
{
    private readonly IWorkflowDefinitionStore _store;
    private readonly WorkflowRunnerService _runner;
    private readonly ILogger<WorkflowDesignerViewModel> _logger;
    private readonly List<(string Json, string Label)> _undoStack = new();
    private readonly List<(string Json, string Label)> _redoStack = new();
    private readonly Dictionary<string, NodeRunRecord> _nodeRecords = new(StringComparer.Ordinal);
    private readonly List<WorkflowLogEntry> _allLogs = new();
    private DesignerContext _context = DesignerContext.Empty;
    private bool _suppressEditorChange;

    public WorkflowDesignerViewModel(
        IWorkflowDefinitionStore store,
        WorkflowRunnerService runner,
        INotificationService notifications,
        IDialogService dialogs,
        IClipboardService clipboard,
        ILogger<WorkflowDesignerViewModel> logger)
    {
        _store = store;
        _runner = runner;
        _logger = logger;
        Notifications = notifications;
        Dialogs = dialogs;
        Clipboard = clipboard;
        Definition = CreateEmptyWorkflow();
        RebuildLibrary();
        RefreshNodeEditors();
    }

    private IClipboardService Clipboard { get; }

    // ── 编辑状态 ─────────────────────────────────────────────

    [ObservableProperty]
    private WorkflowDefinition _definition;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private WorkflowDefinitionSummary? _selectedWorkflow;

    [ObservableProperty]
    private WorkflowNode? _selectedNode;

    [ObservableProperty]
    private NodeConfigEditor? _nodeEditor;

    [ObservableProperty]
    private WorkflowEdge? _selectedEdge;

    [ObservableProperty]
    private string _edgeBranchId = string.Empty;

    [ObservableProperty]
    private string _edgeLabelText = string.Empty;

    [ObservableProperty]
    private string _workflowNameText = "新工作流";

    [ObservableProperty]
    private string _workflowDescriptionText = string.Empty;

    [ObservableProperty]
    private string _workflowTagsText = string.Empty;

    [ObservableProperty]
    private string _workflowNotesText = string.Empty;

    [ObservableProperty]
    private string _nodeSearchText = string.Empty;

    [ObservableProperty]
    private string _nodeCategoryFilter = "全部";

    [ObservableProperty]
    private string _issuesSummaryText = "尚未校验。";

    [ObservableProperty]
    private string _snapshotInfoText = "尚无版本快照。";

    /// <summary>属性面板模式（workflow / node / edge），供视图切换显示。</summary>
    [ObservableProperty]
    private string _propertyPanelMode = "workflow";

    public ObservableCollection<WorkflowDefinitionSummary> Workflows { get; } = new();

    public ObservableCollection<NodeLibraryGroup> NodeLibrary { get; } = new();

    public ObservableCollection<DesignerValidationRow> Issues { get; } = new();

    public ObservableCollection<WorkflowVariableRow> InputVariables { get; } = new();

    public ObservableCollection<WorkflowVariableRow> OutputVariables { get; } = new();

    public IReadOnlyList<string> CategoryFilters { get; } =
        new[] { "全部" }.Concat(NodeTypeCatalog.Categories).ToList();

    // ── 视图事件（由视图代码后置订阅，用于驱动画布控件） ─────────

    public event EventHandler? DefinitionChangedForCanvas;

    public event EventHandler<(string? NodeId, string? EdgeId)>? SelectionSyncRequested;

    public event EventHandler? RunVisualsChanged;

    public event EventHandler? FitRequested;

    public event EventHandler<double>? ZoomRequested;

    public event EventHandler<string>? FocusNodeRequested;

    public Task OnActivatedAsync() => InitializeAsync();

    [RelayCommand]
    private async Task InitializeAsync()
    {
        try
        {
            await RefreshContextAsync().ConfigureAwait(true);
            var summaries = await _store.ListAsync().ConfigureAwait(true);
            Workflows.Clear();
            foreach (var summary in summaries)
            {
                Workflows.Add(summary);
            }

            SelectedWorkflow ??= Workflows.FirstOrDefault();
            await RefreshSnapshotsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "初始化实验室失败");
            Notifications?.Notify("初始化失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshContextAsync()
    {
        try
        {
            var workflows = await _store.ListAsync().ConfigureAwait(true);
            var models = await _runner.GetModelTargetsAsync().ConfigureAwait(true);
            _context = new DesignerContext(workflows, models, _runner.Algorithms.List());
            RebuildLibrary();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新设计器上下文失败");
        }
    }

    private void RebuildLibrary()
    {
        var search = NodeSearchText?.Trim() ?? string.Empty;
        NodeLibrary.Clear();
        foreach (var category in NodeTypeCatalog.Categories)
        {
            if (NodeCategoryFilter is { Length: > 0 } filter && filter != "全部" && filter != category)
            {
                continue;
            }

            var items = NodeTypeCatalog.All
                .Where(d => d.Category == category)
                .Where(d => search.Length == 0
                    || d.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || d.TypeId.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || d.Description.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Select(d => new NodeLibraryItem { Descriptor = d })
                .ToList();
            if (items.Count > 0)
            {
                NodeLibrary.Add(new NodeLibraryGroup
                {
                    Category = category,
                    Items = new ObservableCollection<NodeLibraryItem>(items),
                });
            }
        }
    }

    partial void OnNodeSearchTextChanged(string value) => RebuildLibrary();

    partial void OnNodeCategoryFilterChanged(string value) => RebuildLibrary();

    partial void OnWorkflowNameTextChanged(string value) => ApplyWorkflowProperties();

    partial void OnWorkflowDescriptionTextChanged(string value) => ApplyWorkflowProperties();

    partial void OnWorkflowTagsTextChanged(string value) => ApplyWorkflowProperties();

    partial void OnWorkflowNotesTextChanged(string value) => ApplyWorkflowProperties();

    partial void OnSelectedNodeChanged(WorkflowNode? value)
    {
        RefreshNodeEditors();
        PropertyPanelMode = value is not null ? "node" : SelectedEdge is not null ? "edge" : "workflow";
        SelectionSyncRequested?.Invoke(this, (value?.Id, value is null ? SelectedEdge?.Id : null));
        RaiseNodeDetail(value?.Id);
    }

    partial void OnSelectedEdgeChanged(WorkflowEdge? value)
    {
        PropertyPanelMode = SelectedNode is not null ? "node" : value is not null ? "edge" : "workflow";
        EdgeBranchId = value?.BranchId ?? string.Empty;
        EdgeLabelText = value?.Label ?? string.Empty;
        SelectionSyncRequested?.Invoke(this, (SelectedNode?.Id, value?.Id));
    }

    partial void OnEdgeBranchIdChanged(string value) => ApplyEdgeProperties();

    partial void OnEdgeLabelTextChanged(string value) => ApplyEdgeProperties();

    private void ApplyEdgeProperties()
    {
        if (_suppressEditorChange || SelectedEdge is not { } edge)
        {
            return;
        }

        edge.BranchId = string.IsNullOrWhiteSpace(EdgeBranchId) ? null : EdgeBranchId.Trim();
        edge.Label = string.IsNullOrWhiteSpace(EdgeLabelText) ? null : EdgeLabelText.Trim();
        MarkDirty(refreshValidation: true);
        DefinitionChangedForCanvas?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>画布选择回调（视图代码后置调用）。</summary>
    public void HandleCanvasSelection(string? nodeId, string? edgeId)
    {
        _suppressEditorChange = true;
        try
        {
            SelectedNode = nodeId is null ? null : Definition.FindNode(nodeId);
            SelectedEdge = edgeId is null ? null : Definition.FindEdge(edgeId);
        }
        finally
        {
            _suppressEditorChange = false;
        }
    }

    private void RefreshNodeEditors()
    {
        if (NodeEditor is not null)
        {
            NodeEditor.Changed -= OnNodeEditorChanged;
        }

        if (SelectedNode is { } node)
        {
            var editor = NodeEditorFactory.Create(node, _context);
            editor.Changed += OnNodeEditorChanged;
            NodeEditor = editor;
        }
        else
        {
            NodeEditor = null;
        }
    }

    private void OnNodeEditorChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorChange || NodeEditor is null || SelectedNode is null)
        {
            return;
        }

        NodeEditor.ApplyToNode();
        MarkDirty(refreshValidation: false);
        DefinitionChangedForCanvas?.Invoke(this, EventArgs.Empty);
        RaiseNodeDetail(SelectedNode.Id);
    }

    private void ApplyWorkflowProperties()
    {
        if (_suppressEditorChange)
        {
            return;
        }

        Definition.Name = string.IsNullOrWhiteSpace(WorkflowNameText) ? "未命名工作流" : WorkflowNameText.Trim();
        Definition.Description = WorkflowDescriptionText ?? string.Empty;
        Definition.Tags = (WorkflowTagsText ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        Definition.Notes = WorkflowNotesText;
        MarkDirty(refreshValidation: false);
        DefinitionChangedForCanvas?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void AddInputVariable()
    {
        InputVariables.Add(new WorkflowVariableRow(new WorkflowVariable { Name = "input" + (InputVariables.Count + 1) }));
        SyncVariablesToDefinition();
    }

    [RelayCommand]
    private void RemoveInputVariable(WorkflowVariableRow? row)
    {
        if (row is not null)
        {
            InputVariables.Remove(row);
            SyncVariablesToDefinition();
        }
    }

    [RelayCommand]
    private void AddOutputVariable()
    {
        OutputVariables.Add(new WorkflowVariableRow(new WorkflowVariable { Name = "output" + (OutputVariables.Count + 1) }));
        SyncVariablesToDefinition();
    }

    [RelayCommand]
    private void RemoveOutputVariable(WorkflowVariableRow? row)
    {
        if (row is not null)
        {
            OutputVariables.Remove(row);
            SyncVariablesToDefinition();
        }
    }

    private void SyncVariablesToDefinition()
    {
        Definition.Inputs = InputVariables.Select(v => v.ToModel()).ToList();
        Definition.Outputs = OutputVariables.Select(v => v.ToModel()).ToList();
        MarkDirty(refreshValidation: false);
    }

    private void MarkDirty(bool refreshValidation)
    {
        IsDirty = true;
        if (refreshValidation)
        {
            _ = ValidateAsync();
        }
    }

    // ── 节点/连线编辑命令 ─────────────────────────────────────

    [RelayCommand]
    private void AddNode(string? typeId)
    {
        if (string.IsNullOrWhiteSpace(typeId))
        {
            return;
        }

        PushUndo("添加节点");
        var node = NodeTypeCatalog.CreateNode(typeId, NewNodeId(), 80 + Definition.Nodes.Count * 40, 80 + Definition.Nodes.Count * 30);
        Definition.Nodes.Add(node);
        SelectedNode = node;
        SelectedEdge = null;
        MarkDirty(refreshValidation: true);
        DefinitionChangedForCanvas?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>画布拖放回调。</summary>
    public void HandleNodeDropped(string typeId, double x, double y)
    {
        PushUndo("添加节点");
        var node = NodeTypeCatalog.CreateNode(typeId, NewNodeId(), Math.Max(0, x), Math.Max(0, y));
        Definition.Nodes.Add(node);
        SelectedNode = node;
        SelectedEdge = null;
        MarkDirty(refreshValidation: true);
        DefinitionChangedForCanvas?.Invoke(this, EventArgs.Empty);
    }

    private string NewNodeId()
    {
        var index = 1;
        while (Definition.Nodes.Any(n => n.Id == "n" + index))
        {
            index++;
        }

        return "n" + index;
    }

    [RelayCommand]
    private void DeleteSelection()
    {
        if (SelectedNode is { } node)
        {
            if (node.TypeId == NodeTypeCatalog.Start && Definition.Nodes.Count(n => n.TypeId == NodeTypeCatalog.Start) == 1)
            {
                Notifications?.Notify("工作流必须保留一个开始节点。");
                return;
            }

            PushUndo("删除节点");
            Definition.Nodes.Remove(node);
            Definition.Edges.RemoveAll(e => e.SourceNodeId == node.Id || e.TargetNodeId == node.Id);
            SelectedNode = null;
            MarkDirty(refreshValidation: true);
            DefinitionChangedForCanvas?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (SelectedEdge is { } edge)
        {
            PushUndo("删除连线");
            Definition.Edges.Remove(edge);
            SelectedEdge = null;
            MarkDirty(refreshValidation: true);
            DefinitionChangedForCanvas?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void DuplicateSelection()
    {
        if (SelectedNode is not { } node)
        {
            return;
        }

        PushUndo("复制节点");
        var copy = node.CopyShallow();
        copy.Id = NewNodeId();
        copy.Name = node.Name + " 副本";
        copy.X += 40;
        copy.Y += 40;
        Definition.Nodes.Add(copy);
        SelectedNode = copy;
        MarkDirty(refreshValidation: true);
        DefinitionChangedForCanvas?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleBreakpoint()
    {
        if (SelectedNode is not { } node)
        {
            return;
        }

        node.IsBreakpoint = !node.IsBreakpoint;
        if (NodeEditor is not null)
        {
            _suppressEditorChange = true;
            try
            {
                NodeEditor.Breakpoint = node.IsBreakpoint;
            }
            finally
            {
                _suppressEditorChange = false;
            }
        }

        MarkDirty(refreshValidation: false);
        DefinitionChangedForCanvas?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>画布节点拖动开始（视图调用，用于压入撤销快照）。</summary>
    public void HandleNodeDragStarted(string nodeId) => PushUndo("移动节点");

    /// <summary>画布节点移动（视图调用）。</summary>
    public void HandleNodeMoved(string nodeId)
    {
        MarkDirty(refreshValidation: false);
    }

    /// <summary>画布连线请求（视图调用）。</summary>
    public void HandleConnectionRequested(WorkflowConnectionRequest request)
    {
        var source = Definition.FindNode(request.SourceNodeId);
        var target = Definition.FindNode(request.TargetNodeId);
        if (source is null || target is null)
        {
            return;
        }

        if (Definition.Edges.Any(e =>
                e.SourceNodeId == request.SourceNodeId
                && e.TargetNodeId == request.TargetNodeId
                && e.SourcePort == request.SourcePort))
        {
            Notifications?.Notify("相同的连线已存在。");
            return;
        }

        PushUndo("添加连线");
        var branchNodes = new HashSet<string>(new[] { NodeTypeCatalog.Condition, NodeTypeCatalog.Parallel, NodeTypeCatalog.Confirm }, StringComparer.Ordinal);
        var branchId = branchNodes.Contains(source.TypeId) ? request.SourcePort : null;
        var edge = new WorkflowEdge
        {
            Id = "e" + (Definition.Edges.Count + 1) + "-" + Guid.NewGuid().ToString("N")[..4],
            SourceNodeId = request.SourceNodeId,
            SourcePort = request.SourcePort,
            TargetNodeId = request.TargetNodeId,
            TargetPort = request.TargetPort,
            BranchId = branchId,
        };
        Definition.Edges.Add(edge);
        SelectedEdge = edge;
        SelectedNode = null;
        MarkDirty(refreshValidation: true);
        DefinitionChangedForCanvas?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void AutoLayout()
    {
        PushUndo("自动布局");
        var depths = ComputeDepths();
        var columns = new Dictionary<int, List<WorkflowNode>>();
        foreach (var node in Definition.Nodes)
        {
            var depth = depths.GetValueOrDefault(node.Id, 0);
            if (!columns.TryGetValue(depth, out var list))
            {
                list = new List<WorkflowNode>();
                columns[depth] = list;
            }

            list.Add(node);
        }

        foreach (var (depth, columnNodes) in columns)
        {
            for (var i = 0; i < columnNodes.Count; i++)
            {
                columnNodes[i].X = 60 + depth * 300;
                columnNodes[i].Y = 60 + i * 140;
            }
        }

        MarkDirty(refreshValidation: false);
        DefinitionChangedForCanvas?.Invoke(this, EventArgs.Empty);
        FitRequested?.Invoke(this, EventArgs.Empty);
    }

    private Dictionary<string, int> ComputeDepths()
    {
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        var start = Definition.Nodes.FirstOrDefault(n => n.TypeId == NodeTypeCatalog.Start);
        if (start is null)
        {
            return depths;
        }

        var queue = new Queue<string>();
        queue.Enqueue(start.Id);
        depths[start.Id] = 0;
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var edge in Definition.OutgoingEdges(id))
            {
                if (!depths.ContainsKey(edge.TargetNodeId))
                {
                    depths[edge.TargetNodeId] = depths[id] + 1;
                    queue.Enqueue(edge.TargetNodeId);
                }
            }
        }

        return depths;
    }

    [RelayCommand]
    private void Fit() => FitRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ZoomIn() => ZoomRequested?.Invoke(this, 1.15);

    [RelayCommand]
    private void ZoomOut() => ZoomRequested?.Invoke(this, 1 / 1.15);

    // ── 撤销 / 重做 ─────────────────────────────────────────

    private void PushUndo(string label)
    {
        _undoStack.Add((WorkflowJson.Serialize(Definition, indented: false), label));
        if (_undoStack.Count > 50)
        {
            _undoStack.RemoveAt(0);
        }

        _redoStack.Clear();
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        var (json, label) = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add((WorkflowJson.Serialize(Definition, indented: false), label));
        ApplyDefinitionJson(json);
        Notifications?.Notify($"已撤销：{label}");
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        var (json, label) = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add((WorkflowJson.Serialize(Definition, indented: false), label));
        ApplyDefinitionJson(json);
        Notifications?.Notify($"已重做：{label}");
    }

    private void ApplyDefinitionJson(string json)
    {
        var restored = WorkflowJson.Deserialize<WorkflowDefinition>(json);
        if (restored is null)
        {
            return;
        }

        Definition = restored;
        SelectedNode = null;
        SelectedEdge = null;
        LoadDefinitionIntoEditors();
        MarkDirty(refreshValidation: true);
        DefinitionChangedForCanvas?.Invoke(this, EventArgs.Empty);
    }

    // ── 工作流级命令 ─────────────────────────────────────────

    [RelayCommand]
    private void NewWorkflow()
    {
        PushUndo("新建工作流");
        Definition = CreateEmptyWorkflow();
        SelectedNode = null;
        SelectedEdge = null;
        LoadDefinitionIntoEditors();
        IsDirty = false;
        Issues.Clear();
        IssuesSummaryText = "尚未校验。";
        DefinitionChangedForCanvas?.Invoke(this, EventArgs.Empty);
        FitRequested?.Invoke(this, EventArgs.Empty);
    }

    private static WorkflowDefinition CreateEmptyWorkflow()
    {
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = "新工作流",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var start = NodeTypeCatalog.CreateNode(NodeTypeCatalog.Start, "n1", 80, 120);
        var end = NodeTypeCatalog.CreateNode(NodeTypeCatalog.End, "n2", 620, 120);
        workflow.Nodes.Add(start);
        workflow.Nodes.Add(end);
        workflow.Edges.Add(new WorkflowEdge
        {
            Id = "e1",
            SourceNodeId = start.Id,
            TargetNodeId = end.Id,
        });
        return workflow;
    }

    [RelayCommand]
    private async Task OpenWorkflowAsync()
    {
        if (IsDirty && !await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            return;
        }

        await RefreshContextAsync().ConfigureAwait(true);
        var summary = SelectedWorkflow
            ?? Workflows.FirstOrDefault();
        if (summary is null)
        {
            Notifications?.Notify("还没有保存过的工作流。");
            return;
        }

        var result = await _store.LoadAsync(summary.WorkflowId).ConfigureAwait(true);
        if (result.Definition is null)
        {
            Notifications?.Notify("加载失败：" + string.Join("；", result.Issues.Select(i => i.Message)));
            return;
        }

        _undoStack.Clear();
        _redoStack.Clear();
        Definition = result.Definition;
            RunTestMode = Definition.Defaults.DefaultTestMode;
        SelectedNode = null;
        SelectedEdge = null;
        LoadDefinitionIntoEditors();
        IsDirty = false;
        await ValidateAsync().ConfigureAwait(true);
        await RefreshSnapshotsAsync().ConfigureAwait(true);
        DefinitionChangedForCanvas?.Invoke(this, EventArgs.Empty);
        FitRequested?.Invoke(this, EventArgs.Empty);
        if (result.Issues.Count > 0)
        {
            Notifications?.Notify($"已打开，但存在 {result.Issues.Count} 个问题（见校验面板）。");
            foreach (var issue in result.Issues)
            {
                Issues.Add(new DesignerValidationRow { Issue = issue });
            }
        }
    }

    private async Task<bool> ConfirmDiscardAsync()
        => await (Dialogs?.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "舍弃未保存的修改？",
            Message = "当前工作流有未保存的修改，继续将丢失这些修改。",
            PrimaryButtonText = "舍弃修改",
            IsDestructive = true,
        }) ?? Task.FromResult(true)).ConfigureAwait(true);

    [RelayCommand]
    private async Task SaveWorkflowAsync()
    {
        try
        {
            await _store.SaveAsync(Definition).ConfigureAwait(true);
            IsDirty = false;
            Notifications?.Notify($"已保存工作流“{Definition.Name}”。");
            await ReloadWorkflowListAsync().ConfigureAwait(true);
            await ValidateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await (Dialogs?.ShowErrorAsync("保存失败", ex.Message) ?? Task.CompletedTask);
        }
    }

    [RelayCommand]
    private async Task SaveAsVersionAsync()
    {
        var label = await (Dialogs?.PromptAsync("创建版本快照", "版本标签（如 v1、修复分支）：", $"v{DateTime.Now:MMdd-HHmm}") ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        var note = await (Dialogs?.PromptAsync("版本说明", "说明（可留空）：", string.Empty) ?? Task.FromResult<string?>(null));
        try
        {
            await _store.SaveAsync(Definition, label!, note).ConfigureAwait(true);
            IsDirty = false;
            Notifications?.Notify($"已保存并创建快照“{label}”。");
            await ReloadWorkflowListAsync().ConfigureAwait(true);
            await RefreshSnapshotsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await (Dialogs?.ShowErrorAsync("保存失败", ex.Message) ?? Task.CompletedTask);
        }
    }

    [RelayCommand]
    private async Task DeleteWorkflowAsync()
    {
        var confirmed = await (Dialogs?.ConfirmAsync(new ConfirmDialogOptions
        {
            Title = "删除工作流",
            Message = $"确定删除工作流“{Definition.Name}”（{Definition.Id}）？快照与运行历史不会被删除。",
            PrimaryButtonText = "删除",
            IsDestructive = true,
        }) ?? Task.FromResult(false)).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _store.DeleteAsync(Definition.Id).ConfigureAwait(true);
            Notifications?.Notify("工作流已删除。");
            await ReloadWorkflowListAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await (Dialogs?.ShowErrorAsync("删除失败", ex.Message) ?? Task.CompletedTask);
        }
    }

    [RelayCommand]
    private async Task ImportWorkflowAsync()
    {
        var path = Dialogs?.PickOpenFile("导入工作流", "工作流文件 (*.workflow.json)|*.workflow.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*");
        if (path is null)
        {
            return;
        }

        var result = await _store.ImportAsync(path).ConfigureAwait(true);
        if (result.Definition is null)
        {
            await (Dialogs?.ShowErrorAsync("导入失败", string.Join("\n", result.Issues.Select(i => i.Message))) ?? Task.CompletedTask);
            return;
        }

        PushUndo("导入工作流");
        Definition = result.Definition;
        SelectedNode = null;
        SelectedEdge = null;
        LoadDefinitionIntoEditors();
        MarkDirty(refreshValidation: true);
        DefinitionChangedForCanvas?.Invoke(this, EventArgs.Empty);
        FitRequested?.Invoke(this, EventArgs.Empty);
        if (result.Issues.Count > 0)
        {
            Issues.Clear();
            foreach (var issue in result.Issues)
            {
                Issues.Add(new DesignerValidationRow { Issue = issue });
            }

            IssuesSummaryText = $"导入完成，发现 {result.Issues.Count} 个问题（不会自动运行）。";
        }
        else
        {
            Notifications?.Notify("导入完成（未自动保存与运行）。");
        }
    }

    [RelayCommand]
    private async Task ExportWorkflowAsync()
    {
        var path = Dialogs?.PickSaveFile(
            "导出工作流",
            "工作流文件 (*.workflow.json)|*.workflow.json",
            Definition.Id + ".workflow.json");
        if (path is null)
        {
            return;
        }

        try
        {
            await _store.ExportAsync(Definition, path).ConfigureAwait(true);
            Notifications?.Notify($"已导出：{path}");
        }
        catch (Exception ex)
        {
            await (Dialogs?.ShowErrorAsync("导出失败", ex.Message) ?? Task.CompletedTask);
        }
    }

    [RelayCommand]
    private async Task ValidateAsync()
    {
        try
        {
            var report = await _runner.ValidateAsync(Definition, forRun: false).ConfigureAwait(true);
            Issues.Clear();
            foreach (var issue in report.OrderedIssues)
            {
                Issues.Add(new DesignerValidationRow { Issue = issue });
            }

            IssuesSummaryText = report.Summary;
        }
        catch (Exception ex)
        {
            IssuesSummaryText = "校验失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void SelectIssue(DesignerValidationRow? row)
    {
        if (row?.NodeId is not { Length: > 0 } nodeId)
        {
            return;
        }

        var node = Definition.FindNode(nodeId);
        if (node is not null)
        {
            SelectedNode = node;
            FocusNodeRequested?.Invoke(this, nodeId);
        }
    }

    private async Task ReloadWorkflowListAsync()
    {
        var summaries = await _store.ListAsync().ConfigureAwait(true);
        var selectedId = SelectedWorkflow?.WorkflowId;
        Workflows.Clear();
        foreach (var summary in summaries)
        {
            Workflows.Add(summary);
        }

        SelectedWorkflow = Workflows.FirstOrDefault(w => w.WorkflowId == selectedId) ?? Workflows.FirstOrDefault();
        await RefreshContextAsync().ConfigureAwait(true);
    }

    private async Task RefreshSnapshotsAsync()
    {
        try
        {
            var snapshots = await _store.ListSnapshotsAsync(Definition.Id).ConfigureAwait(true);
            SnapshotInfoText = snapshots.Count == 0
                ? "尚无版本快照。"
                : $"共 {snapshots.Count} 个快照 · 最近：{snapshots[0].Label}（{snapshots[0].CreatedUtc.ToLocalTime():MM-dd HH:mm}）";
        }
        catch
        {
            SnapshotInfoText = "快照信息不可用。";
        }
    }

    private void LoadDefinitionIntoEditors()
    {
        _suppressEditorChange = true;
        try
        {
            WorkflowNameText = Definition.Name;
            WorkflowDescriptionText = Definition.Description;
            WorkflowTagsText = string.Join(", ", Definition.Tags);
            WorkflowNotesText = Definition.Notes ?? string.Empty;
            InputVariables.Clear();
            foreach (var variable in Definition.Inputs)
            {
                InputVariables.Add(new WorkflowVariableRow(variable));
            }

            OutputVariables.Clear();
            foreach (var variable in Definition.Outputs)
            {
                OutputVariables.Add(new WorkflowVariableRow(variable));
            }
        }
        finally
        {
            _suppressEditorChange = false;
        }
    }

    public void Dispose()
    {
        if (NodeEditor is not null)
        {
            NodeEditor.Changed -= OnNodeEditorChanged;
        }
    }
}
