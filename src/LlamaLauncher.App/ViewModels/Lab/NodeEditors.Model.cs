using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Workflows;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.App.ViewModels.Lab;

/// <summary>决策模型节点编辑器（也作为循环节点的决策模板编辑器）。</summary>
public sealed partial class DecisionNodeEditor : NodeConfigEditor
{
    private readonly bool _isTemplate;

    public DecisionNodeEditor(WorkflowNode node, DesignerContext context, bool isTemplate = false)
        : base(node, context)
    {
        _isTemplate = isTemplate;
        var config = WorkflowJson.TryGetConfig<DecisionNodeConfig>(node.Config, out _) ?? new DecisionNodeConfig();
        _instanceId = config.InstanceId ?? string.Empty;
        _instanceNameSnapshot = config.InstanceNameSnapshot ?? string.Empty;
        _stateTemplate = config.StateTemplate;
        _stateJsonTemplate = config.StateJsonTemplate ?? string.Empty;
        _timeoutSeconds = config.TimeoutSeconds;
        _maxResponseKb = config.MaxResponseBytes is { } bytes ? bytes / 1024 : 0;
        foreach (var question in config.Questions)
        {
            Questions.Add(new DecisionQuestionRow(question));
        }

        WatchCollection(Questions);
        _selectedQuestion = Questions.FirstOrDefault();
    }

    /// <summary>模板模式（循环内使用）：隐藏实例绑定字段。</summary>
    public bool IsTemplateMode => _isTemplate;

    public IReadOnlyList<WorkflowModelTarget> ModelOptions => Context.Models;

    [ObservableProperty]
    private string _instanceId;

    [ObservableProperty]
    private string _instanceNameSnapshot;

    [ObservableProperty]
    private string _stateTemplate;

    [ObservableProperty]
    private string _stateJsonTemplate;

    [ObservableProperty]
    private int _timeoutSeconds = 120;

    [ObservableProperty]
    private int _maxResponseKb;

    [ObservableProperty]
    private DecisionQuestionRow? _selectedQuestion;

    public ObservableCollection<DecisionQuestionRow> Questions { get; } = new();

    partial void OnInstanceIdChanged(string value)
    {
        var target = Context.Models.FirstOrDefault(m =>
            string.Equals(m.InstanceId, value, StringComparison.OrdinalIgnoreCase));
        InstanceNameSnapshot = target?.InstanceName ?? string.Empty;
    }

    [RelayCommand]
    private void AddQuestion()
    {
        var row = new DecisionQuestionRow(new DecisionQuestion
        {
            Id = "question" + (Questions.Count + 1),
            Type = "choice",
            Instructions = "请描述问题…",
            Options = { new DecisionOption { Key = "yes" }, new DecisionOption { Key = "no" } },
        });
        Questions.Add(row);
        SelectedQuestion = row;
    }

    [RelayCommand]
    private void RemoveQuestion()
    {
        if (SelectedQuestion is { } row)
        {
            Questions.Remove(row);
            SelectedQuestion = Questions.FirstOrDefault();
        }
    }

    public DecisionNodeConfig BuildDecisionConfig() => new()
    {
        InstanceId = _isTemplate || string.IsNullOrWhiteSpace(InstanceId) ? null : InstanceId.Trim(),
        InstanceNameSnapshot = _isTemplate || string.IsNullOrWhiteSpace(InstanceNameSnapshot) ? null : InstanceNameSnapshot,
        StateTemplate = StateTemplate,
        StateJsonTemplate = string.IsNullOrWhiteSpace(StateJsonTemplate) ? null : StateJsonTemplate,
        Questions = Questions.Select(q => q.ToModel()).ToList(),
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, 3600),
        MaxResponseBytes = MaxResponseKb > 0 ? Math.Clamp(MaxResponseKb, 1, 65_536) * 1024 : null,
    };

    public override object BuildConfig() => BuildDecisionConfig();
}

/// <summary>规则校验节点编辑器。</summary>
public sealed partial class RulesNodeEditor : NodeConfigEditor
{
    public RulesNodeEditor(WorkflowNode node, DesignerContext context)
        : base(node, context)
    {
        var config = WorkflowJson.TryGetConfig<RulesNodeConfig>(node.Config, out _) ?? new RulesNodeConfig();
        _onFailure = config.OnFailure;
        foreach (var rule in config.Rules)
        {
            Rules.Add(new RuleRow(rule));
        }

        WatchCollection(Rules);
        _selectedRule = Rules.FirstOrDefault();
    }

    [ObservableProperty]
    private RuleFailureAction _onFailure = RuleFailureAction.Stop;

    [ObservableProperty]
    private RuleRow? _selectedRule;

    [ObservableProperty]
    private string _trialContextJson = "{}";

    [ObservableProperty]
    private string _trialResult = "输入试算上下文后点击“试算”。";

    public ObservableCollection<RuleRow> Rules { get; } = new();

    [RelayCommand]
    private void AddRule()
    {
        var row = new RuleRow(new RuleDefinition
        {
            Id = "rule" + (Rules.Count + 1),
            Name = "规则 " + (Rules.Count + 1),
            Condition = "true",
            Severity = WorkflowIssueSeverity.Error,
        });
        Rules.Add(row);
        SelectedRule = row;
    }

    [RelayCommand]
    private void RemoveRule()
    {
        if (SelectedRule is { } row)
        {
            Rules.Remove(row);
            SelectedRule = Rules.FirstOrDefault();
        }
    }

    [RelayCommand]
    private void TrialEvaluate()
    {
        TrialResult = SelectedRule is not { } rule
            ? "请选择一条规则。"
            : DesignerTrial.Evaluate(rule.Condition, TrialContextJson);
    }

    public override object BuildConfig() => new RulesNodeConfig
    {
        OnFailure = OnFailure,
        Rules = Rules.Select(r => r.ToModel()).ToList(),
    };
}

/// <summary>算法求解节点编辑器。</summary>
public sealed partial class AlgorithmNodeEditor : NodeConfigEditor
{
    public AlgorithmNodeEditor(WorkflowNode node, DesignerContext context)
        : base(node, context)
    {
        var config = WorkflowJson.TryGetConfig<AlgorithmNodeConfig>(node.Config, out _) ?? new AlgorithmNodeConfig();
        _solverKey = config.SolverKey;
        _inputJsonTemplate = config.InputJsonTemplate ?? string.Empty;
        _timeoutSeconds = config.TimeoutSeconds;
    }

    public IReadOnlyList<IWorkflowAlgorithm> Solvers => Context.Algorithms;

    [ObservableProperty]
    private string _solverKey;

    [ObservableProperty]
    private string _inputJsonTemplate;

    [ObservableProperty]
    private int _timeoutSeconds = 60;

    public override object BuildConfig() => new AlgorithmNodeConfig
    {
        SolverKey = SolverKey,
        InputJsonTemplate = string.IsNullOrWhiteSpace(InputJsonTemplate) ? null : InputJsonTemplate,
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, 3600),
    };
}

/// <summary>循环节点编辑器。</summary>
public sealed partial class LoopNodeEditor : NodeConfigEditor
{
    public LoopNodeEditor(WorkflowNode node, DesignerContext context)
        : base(node, context)
    {
        var config = WorkflowJson.TryGetConfig<LoopNodeConfig>(node.Config, out _) ?? new LoopNodeConfig();
        _mode = config.Mode;
        _itemsExpression = config.ItemsExpression;
        _maxIterations = config.MaxIterations;
        _continueOnItemError = config.ContinueOnItemError;
        _subWorkflowId = config.SubWorkflowId ?? string.Empty;
        _subWorkflowNameSnapshot = config.SubWorkflowNameSnapshot ?? string.Empty;
        _itemTimeoutText = config.ItemTimeoutSeconds?.ToString() ?? string.Empty;
        DecisionTemplate = new DecisionNodeEditor(node, context, isTemplate: true);
        if (config.DecisionTemplate is not null)
        {
            DecisionTemplate.LoadFromConfig(config.DecisionTemplate);
        }

        DecisionTemplate.Changed += (_, _) => RaiseChanged();
    }

    public DecisionNodeEditor DecisionTemplate { get; }

    public IReadOnlyList<WorkflowDefinitionSummary> WorkflowOptions => Context.Workflows;

    [ObservableProperty]
    private LoopMode _mode = LoopMode.SubWorkflow;

    [ObservableProperty]
    private string _itemsExpression;

    [ObservableProperty]
    private int _maxIterations = 100;

    [ObservableProperty]
    private bool _continueOnItemError;

    [ObservableProperty]
    private string _subWorkflowId;

    [ObservableProperty]
    private string _subWorkflowNameSnapshot;

    [ObservableProperty]
    private string _itemTimeoutText;

    partial void OnSubWorkflowIdChanged(string value)
    {
        var workflow = Context.Workflows.FirstOrDefault(w =>
            string.Equals(w.WorkflowId, value, StringComparison.OrdinalIgnoreCase));
        SubWorkflowNameSnapshot = workflow?.Name ?? string.Empty;
    }

    public override object BuildConfig() => new LoopNodeConfig
    {
        Mode = Mode,
        ItemsExpression = ItemsExpression,
        MaxIterations = Math.Clamp(MaxIterations, 1, 1000),
        ContinueOnItemError = ContinueOnItemError,
        SubWorkflowId = string.IsNullOrWhiteSpace(SubWorkflowId) ? null : SubWorkflowId.Trim(),
        SubWorkflowNameSnapshot = string.IsNullOrWhiteSpace(SubWorkflowNameSnapshot) ? null : SubWorkflowNameSnapshot,
        ItemTimeoutSeconds = int.TryParse(ItemTimeoutText, out var timeout) ? timeout : null,
        DecisionTemplate = Mode == LoopMode.DecisionModel ? DecisionTemplate.BuildDecisionConfig() : null,
    };
}

/// <summary>并行扇出节点编辑器。</summary>
public sealed partial class ParallelNodeEditor : NodeConfigEditor
{
    public ParallelNodeEditor(WorkflowNode node, DesignerContext context)
        : base(node, context)
    {
        var config = WorkflowJson.TryGetConfig<ParallelNodeConfig>(node.Config, out _) ?? new ParallelNodeConfig();
        foreach (var branch in config.Branches)
        {
            Branches.Add(new BranchRow(branch));
        }

        WatchCollection(Branches);
        _maxConcurrencyText = config.MaxConcurrency?.ToString() ?? string.Empty;
        _sharedExpression = config.SharedExpression ?? string.Empty;
    }

    [ObservableProperty]
    private string _maxConcurrencyText;

    [ObservableProperty]
    private string _sharedExpression;

    [ObservableProperty]
    private BranchRow? _selectedBranch;

    public ObservableCollection<BranchRow> Branches { get; } = new();

    [RelayCommand]
    private void AddBranch()
    {
        Branches.Add(new BranchRow(new ParallelBranch
        {
            Id = "b" + (Branches.Count + 1),
            Name = "分支 " + (Branches.Count + 1),
        }));
    }

    [RelayCommand]
    private void RemoveBranch(BranchRow? row)
    {
        if (row is not null)
        {
            Branches.Remove(row);
        }
    }

    public override object BuildConfig() => new ParallelNodeConfig
    {
        Branches = Branches.Select(b => new ParallelBranch { Id = b.Id.Trim(), Name = b.Name }).ToList(),
        MaxConcurrency = int.TryParse(MaxConcurrencyText, out var max) ? max : null,
        SharedExpression = string.IsNullOrWhiteSpace(SharedExpression) ? null : SharedExpression.Trim(),
    };
}

/// <summary>汇合节点编辑器。</summary>
public sealed partial class JoinNodeEditor : NodeConfigEditor
{
    public JoinNodeEditor(WorkflowNode node, DesignerContext context)
        : base(node, context)
    {
        var config = WorkflowJson.TryGetConfig<JoinNodeConfig>(node.Config, out _) ?? new JoinNodeConfig();
        _mode = config.Mode;
        _expectedBranches = config.ExpectedBranches ?? string.Empty;
    }

    [ObservableProperty]
    private ParallelJoinMode _mode = ParallelJoinMode.AllComplete;

    [ObservableProperty]
    private string _expectedBranches;

    public override object BuildConfig() => new JoinNodeConfig
    {
        Mode = Mode,
        ExpectedBranches = string.IsNullOrWhiteSpace(ExpectedBranches) ? null : ExpectedBranches.Trim(),
    };
}

/// <summary>人工确认节点编辑器。</summary>
public sealed partial class ConfirmNodeEditor : NodeConfigEditor
{
    public ConfirmNodeEditor(WorkflowNode node, DesignerContext context)
        : base(node, context)
    {
        var config = WorkflowJson.TryGetConfig<ManualConfirmNodeConfig>(node.Config, out _) ?? new ManualConfirmNodeConfig();
        _title = config.Title;
        _messageTemplate = config.MessageTemplate;
        _approveLabel = config.ApproveLabel;
        _rejectLabel = config.RejectLabel;
        foreach (var field in config.Fields)
        {
            Fields.Add(new ManualFieldRow(field));
        }

        WatchCollection(Fields);
    }

    [ObservableProperty]
    private string _title = "请确认";

    [ObservableProperty]
    private string _messageTemplate;

    [ObservableProperty]
    private string _approveLabel = "批准";

    [ObservableProperty]
    private string _rejectLabel = "拒绝";

    public ObservableCollection<ManualFieldRow> Fields { get; } = new();

    [RelayCommand]
    private void AddField() => Fields.Add(new ManualFieldRow(new ManualField
    {
        Id = "field" + (Fields.Count + 1),
        Label = "字段 " + (Fields.Count + 1),
    }));

    [RelayCommand]
    private void RemoveField(ManualFieldRow? row)
    {
        if (row is not null)
        {
            Fields.Remove(row);
        }
    }

    public override object BuildConfig() => new ManualConfirmNodeConfig
    {
        Title = Title,
        MessageTemplate = MessageTemplate ?? string.Empty,
        ApproveLabel = string.IsNullOrWhiteSpace(ApproveLabel) ? "批准" : ApproveLabel,
        RejectLabel = string.IsNullOrWhiteSpace(RejectLabel) ? "拒绝" : RejectLabel,
        Fields = Fields.Select(f => f.ToModel()).ToList(),
    };
}

/// <summary>子工作流节点编辑器。</summary>
public sealed partial class SubWorkflowNodeEditor : NodeConfigEditor
{
    public SubWorkflowNodeEditor(WorkflowNode node, DesignerContext context)
        : base(node, context)
    {
        var config = WorkflowJson.TryGetConfig<SubWorkflowNodeConfig>(node.Config, out _) ?? new SubWorkflowNodeConfig();
        _workflowId = config.WorkflowId;
        _workflowNameSnapshot = config.WorkflowNameSnapshot ?? string.Empty;
        _timeoutText = config.TimeoutSeconds?.ToString() ?? string.Empty;
    }

    public IReadOnlyList<WorkflowDefinitionSummary> WorkflowOptions => Context.Workflows;

    [ObservableProperty]
    private string _workflowId;

    [ObservableProperty]
    private string _workflowNameSnapshot;

    [ObservableProperty]
    private string _timeoutText;

    partial void OnWorkflowIdChanged(string value)
    {
        var workflow = Context.Workflows.FirstOrDefault(w =>
            string.Equals(w.WorkflowId, value, StringComparison.OrdinalIgnoreCase));
        WorkflowNameSnapshot = workflow?.Name ?? string.Empty;
    }

    public override object BuildConfig() => new SubWorkflowNodeConfig
    {
        WorkflowId = WorkflowId,
        WorkflowNameSnapshot = string.IsNullOrWhiteSpace(WorkflowNameSnapshot) ? null : WorkflowNameSnapshot,
        TimeoutSeconds = int.TryParse(TimeoutText, out var timeout) ? timeout : null,
    };
}

/// <summary>异常处理节点编辑器。</summary>
public sealed partial class ExceptionNodeEditor : NodeConfigEditor
{
    public ExceptionNodeEditor(WorkflowNode node, DesignerContext context)
        : base(node, context)
    {
        var config = WorkflowJson.TryGetConfig<ExceptionNodeConfig>(node.Config, out _) ?? new ExceptionNodeConfig();
        _action = config.Action;
        _recordLog = config.RecordLog;
    }

    [ObservableProperty]
    private ExceptionAction _action = ExceptionAction.Capture;

    [ObservableProperty]
    private bool _recordLog = true;

    public override object BuildConfig() => new ExceptionNodeConfig
    {
        Action = Action,
        RecordLog = RecordLog,
    };
}

/// <summary>按节点类型创建属性编辑器。</summary>
public static class NodeEditorFactory
{
    public static NodeConfigEditor Create(WorkflowNode node, DesignerContext context)
        => node.TypeId switch
        {
            NodeTypeCatalog.Start => new StartNodeEditor(node, context),
            NodeTypeCatalog.End => new EndNodeEditor(node, context),
            NodeTypeCatalog.Transform => new TransformNodeEditor(node, context),
            NodeTypeCatalog.Condition => new ConditionNodeEditor(node, context),
            NodeTypeCatalog.Decision => new DecisionNodeEditor(node, context),
            NodeTypeCatalog.Rules => new RulesNodeEditor(node, context),
            NodeTypeCatalog.Algorithm => new AlgorithmNodeEditor(node, context),
            NodeTypeCatalog.Loop => new LoopNodeEditor(node, context),
            NodeTypeCatalog.Parallel => new ParallelNodeEditor(node, context),
            NodeTypeCatalog.Join => new JoinNodeEditor(node, context),
            NodeTypeCatalog.Confirm => new ConfirmNodeEditor(node, context),
            NodeTypeCatalog.SubWorkflow => new SubWorkflowNodeEditor(node, context),
            NodeTypeCatalog.Exception => new ExceptionNodeEditor(node, context),
            _ => new StartNodeEditor(node, context),
        };
}

/// <summary>循环编辑器的决策模板补充方法（从既有配置载入）。</summary>
public static class DecisionNodeEditorExtensions
{
    public static void LoadFromConfig(this DecisionNodeEditor editor, DecisionNodeConfig config)
    {
        editor.StateTemplate = config.StateTemplate;
        editor.StateJsonTemplate = config.StateJsonTemplate ?? string.Empty;
        editor.TimeoutSeconds = config.TimeoutSeconds;
        editor.Questions.Clear();
        foreach (var question in config.Questions)
        {
            editor.Questions.Add(new DecisionQuestionRow(question));
        }

        editor.SelectedQuestion = editor.Questions.FirstOrDefault();
    }
}
