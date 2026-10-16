using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaLauncher.Application.Workflows;
using LlamaLauncher.Domain.Workflows;
using LlamaLauncher.Domain.Workflows.Expressions;

namespace LlamaLauncher.App.ViewModels.Lab;

/// <summary>设计器运行上下文（工作流列表、模型实例、算法）。</summary>
public sealed record DesignerContext(
    IReadOnlyList<WorkflowDefinitionSummary> Workflows,
    IReadOnlyList<WorkflowModelTarget> Models,
    IReadOnlyList<IWorkflowAlgorithm> Algorithms)
{
    public static readonly DesignerContext Empty = new(
        Array.Empty<WorkflowDefinitionSummary>(),
        Array.Empty<WorkflowModelTarget>(),
        Array.Empty<IWorkflowAlgorithm>());
}

/// <summary>表达式试算辅助（属性面板）。</summary>
public static class DesignerTrial
{
    public static string Evaluate(string? expression, string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return "表达式为空。";
        }

        if (WorkflowExpression.Validate(expression) is { } validationError)
        {
            return "表达式无效：" + validationError;
        }

        var scope = WorkflowJson.TryParse(contextJson, out _) as System.Text.Json.Nodes.JsonObject
            ?? new System.Text.Json.Nodes.JsonObject();
        foreach (var root in new[] { "input", "vars", "up", "meta" })
        {
            if (scope[root] is not System.Text.Json.Nodes.JsonObject)
            {
                scope[root] = new System.Text.Json.Nodes.JsonObject();
            }
        }

        if (!WorkflowExpression.TryParse(expression, out var parsed, out _))
        {
            return "表达式无效。";
        }

        return parsed!.TryEvaluateToJson(scope, out var value, out var error)
            ? "= " + (value is null ? "null" : WorkflowJson.Compact(value))
            : "求值失败：" + error;
    }
}

/// <summary>数据处理操作编辑行。</summary>
public sealed partial class TransformOpRow : ObservableObject
{
    private readonly string _id;

    public TransformOpRow(TransformOperation? operation = null)
    {
        _id = operation?.Id is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N")[..8];
        if (operation is null)
        {
            return;
        }

        _kind = operation.Kind;
        _label = operation.Label ?? string.Empty;
        _target = operation.Target ?? string.Empty;
        _source = operation.Source ?? string.Empty;
        _keyExpression = operation.KeyExpression ?? string.Empty;
        _predicateExpression = operation.PredicateExpression ?? string.Empty;
        _fieldsText = string.Join(", ", operation.Fields);
        _defaultValueJson = operation.DefaultValueJson ?? string.Empty;
        _separator = operation.Separator ?? string.Empty;
        _descending = operation.Descending;
        _limitText = operation.Limit?.ToString() ?? string.Empty;
        _templateText = operation.TemplateText ?? string.Empty;
        foreach (var mapping in operation.Mappings)
        {
            Mappings.Add(new MappingRow(mapping));
        }
    }

    [ObservableProperty]
    private TransformOperationKind _kind = TransformOperationKind.Assign;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _target = string.Empty;

    [ObservableProperty]
    private string _source = string.Empty;

    [ObservableProperty]
    private string _keyExpression = string.Empty;

    [ObservableProperty]
    private string _predicateExpression = string.Empty;

    [ObservableProperty]
    private string _fieldsText = string.Empty;

    [ObservableProperty]
    private string _defaultValueJson = string.Empty;

    [ObservableProperty]
    private string _separator = string.Empty;

    [ObservableProperty]
    private bool _descending;

    [ObservableProperty]
    private string _limitText = string.Empty;

    [ObservableProperty]
    private string _templateText = string.Empty;

    public ObservableCollection<MappingRow> Mappings { get; } = new();

    public string Display => string.IsNullOrWhiteSpace(Label) ? Kind.ToString() : Label;

    public TransformOperation ToModel() => new()
    {
        Id = _id,
        Kind = Kind,
        Label = string.IsNullOrWhiteSpace(Label) ? null : Label,
        Target = string.IsNullOrWhiteSpace(Target) ? null : Target.Trim(),
        Source = string.IsNullOrWhiteSpace(Source) ? null : Source.Trim(),
        KeyExpression = string.IsNullOrWhiteSpace(KeyExpression) ? null : KeyExpression.Trim(),
        PredicateExpression = string.IsNullOrWhiteSpace(PredicateExpression) ? null : PredicateExpression.Trim(),
        Fields = FieldsText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList(),
        DefaultValueJson = string.IsNullOrWhiteSpace(DefaultValueJson) ? null : DefaultValueJson.Trim(),
        Separator = string.IsNullOrEmpty(Separator) ? null : Separator,
        Descending = Descending,
        Limit = int.TryParse(LimitText, out var limit) ? limit : null,
        TemplateText = string.IsNullOrWhiteSpace(TemplateText) ? null : TemplateText,
        Mappings = Mappings.Select(m => m.ToModel()).ToList(),
    };

    partial void OnKindChanged(TransformOperationKind value) => OnPropertyChanged(nameof(Display));

    partial void OnLabelChanged(string value) => OnPropertyChanged(nameof(Display));
}

/// <summary>
/// 节点属性编辑器基类：公共字段 + 输入映射；派生类实现类型化配置的读写。
/// 所有变更通过 <see cref="Changed"/> 通知设计器（自动应用回节点并刷新校验）。
/// </summary>
public abstract partial class NodeConfigEditor : ObservableObject
{
    protected NodeConfigEditor(WorkflowNode node, DesignerContext context)
    {
        Node = node;
        Context = context;
        _name = node.Name;
        _notes = node.Notes ?? string.Empty;
        _failurePolicy = node.FailurePolicy;
        _timeoutText = node.TimeoutSeconds?.ToString() ?? string.Empty;
        _breakpoint = node.IsBreakpoint;
        _retryMaxAttempts = node.Retry.MaxAttempts;
        _retryDelayMs = node.Retry.DelayMilliseconds;
        _retryCondition = node.Retry.Condition;
        _outputVariable = node.OutputVariable ?? string.Empty;
        foreach (var mapping in node.InputMappings)
        {
            InputMappings.Add(new MappingRow(mapping));
        }

        WatchCollection(InputMappings);
        PropertyChanged += (_, _) => RaiseChanged();
    }

    public WorkflowNode Node { get; }

    public DesignerContext Context { get; }

    public string NodeTypeTitle => NodeTypeCatalog.Find(Node.TypeId)?.DisplayName ?? Node.TypeId;

    public string NodeTypeDescription => NodeTypeCatalog.Find(Node.TypeId)?.Description ?? string.Empty;

    public string NodeIdText => Node.Id;

    public ObservableCollection<MappingRow> InputMappings { get; } = new();

    public IReadOnlyList<NodeFailurePolicy> FailurePolicyOptions { get; } = new[]
    {
        NodeFailurePolicy.Terminate,
        NodeFailurePolicy.Continue,
        NodeFailurePolicy.ErrorBranch,
    };

    public IReadOnlyList<RetryCondition> RetryConditionOptions { get; } = new[]
    {
        RetryCondition.Never,
        RetryCondition.OnTimeout,
        RetryCondition.OnTransient,
        RetryCondition.Always,
    };

    public IReadOnlyList<WorkflowValueType> ValueTypeOptions { get; } = new[]
    {
        WorkflowValueType.Any,
        WorkflowValueType.String,
        WorkflowValueType.Number,
        WorkflowValueType.Boolean,
        WorkflowValueType.Object,
        WorkflowValueType.Array,
    };

    public IReadOnlyList<TransformOperationKind> TransformKindOptions { get; } =
        Enum.GetValues<TransformOperationKind>();

    public IReadOnlyList<WorkflowIssueSeverity> SeverityOptions { get; } = new[]
    {
        WorkflowIssueSeverity.Error,
        WorkflowIssueSeverity.Warning,
    };

    public IReadOnlyList<RuleFailureAction> RuleFailureOptions { get; } = Enum.GetValues<RuleFailureAction>();

    public IReadOnlyList<ParallelJoinMode> JoinModeOptions { get; } = Enum.GetValues<ParallelJoinMode>();

    public IReadOnlyList<LoopMode> LoopModeOptions { get; } = Enum.GetValues<LoopMode>();

    public IReadOnlyList<ExceptionAction> ExceptionActionOptions { get; } = Enum.GetValues<ExceptionAction>();

    public IReadOnlyList<WorkflowEndStatus> EndStatusOptions { get; } = Enum.GetValues<WorkflowEndStatus>();

    public IReadOnlyList<string> QuestionTypes { get; } = new[] { "choice", "score", "noul" };

    public IReadOnlyList<string> ConvertOptions { get; } = new[] { string.Empty, "number", "string", "boolean", "json" };

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _notes;

    [ObservableProperty]
    private NodeFailurePolicy _failurePolicy;

    [ObservableProperty]
    private string _timeoutText;

    [ObservableProperty]
    private bool _breakpoint;

    [ObservableProperty]
    private int _retryMaxAttempts = 1;

    [ObservableProperty]
    private int _retryDelayMs = 500;

    [ObservableProperty]
    private RetryCondition _retryCondition = RetryCondition.Never;

    [ObservableProperty]
    private string _outputVariable;

    [ObservableProperty]
    private MappingRow? _selectedMapping;

    public event EventHandler? Changed;

    [RelayCommand]
    private void AddMapping()
    {
        var row = new MappingRow();
        InputMappings.Add(row);
        SelectedMapping = row;
    }

    [RelayCommand]
    private void RemoveMapping()
    {
        if (SelectedMapping is { } row)
        {
            InputMappings.Remove(row);
            SelectedMapping = InputMappings.FirstOrDefault();
        }
    }

    /// <summary>把编辑状态写回节点（含类型化配置）。</summary>
    public void ApplyToNode()
    {
        Node.Name = string.IsNullOrWhiteSpace(Name) ? NodeTypeTitle : Name.Trim();
        Node.Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes;
        Node.FailurePolicy = FailurePolicy;
        Node.TimeoutSeconds = int.TryParse(TimeoutText, out var timeout) ? timeout : null;
        Node.IsBreakpoint = Breakpoint;
        Node.Retry = new RetryPolicy
        {
            MaxAttempts = Math.Clamp(RetryMaxAttempts, 1, 5),
            DelayMilliseconds = Math.Clamp(RetryDelayMs, 0, 60_000),
            Condition = RetryCondition,
        };
        Node.OutputVariable = string.IsNullOrWhiteSpace(OutputVariable) ? null : OutputVariable.Trim();
        Node.InputMappings = InputMappings.Select(m => m.ToModel()).ToList();
        var config = BuildConfig();
        if (config is not null)
        {
            Node.Config = WorkflowJson.ToConfigObject(config);
        }
    }

    /// <summary>构建类型化配置（null 保持现有配置）。</summary>
    public abstract object? BuildConfig();

    protected void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected void WatchRow(INotifyPropertyChanged row)
        => row.PropertyChanged += (_, _) => RaiseChanged();

    protected void WatchCollection(INotifyCollectionChanged collection)
    {
        collection.CollectionChanged += (_, args) =>
        {
            foreach (var item in args.NewItems ?? Array.Empty<object>())
            {
                if (item is INotifyPropertyChanged row)
                {
                    WatchRow(row);
                }
            }

            RaiseChanged();
        };
        foreach (var item in collection as IEnumerable<object> ?? Array.Empty<object>())
        {
            if (item is INotifyPropertyChanged row)
            {
                WatchRow(row);
            }
        }
    }
}

/// <summary>开始节点编辑器。</summary>
public sealed class StartNodeEditor : NodeConfigEditor
{
    public StartNodeEditor(WorkflowNode node, DesignerContext context)
        : base(node, context)
    {
    }

    public override object BuildConfig() => new StartNodeConfig();
}

/// <summary>结束节点编辑器。</summary>
public sealed partial class EndNodeEditor : NodeConfigEditor
{
    public EndNodeEditor(WorkflowNode node, DesignerContext context)
        : base(node, context)
    {
        var config = WorkflowJson.TryGetConfig<EndNodeConfig>(node.Config, out _) ?? new EndNodeConfig();
        _status = config.Status;
        _failureMessageTemplate = config.FailureMessageTemplate ?? string.Empty;
        foreach (var mapping in config.OutputMappings)
        {
            OutputMappings.Add(new MappingRow(mapping));
        }

        WatchCollection(OutputMappings);
    }

    [ObservableProperty]
    private WorkflowEndStatus _status = WorkflowEndStatus.Succeeded;

    [ObservableProperty]
    private string _failureMessageTemplate;

    public ObservableCollection<MappingRow> OutputMappings { get; } = new();

    public override object BuildConfig() => new EndNodeConfig
    {
        Status = Status,
        FailureMessageTemplate = string.IsNullOrWhiteSpace(FailureMessageTemplate) ? null : FailureMessageTemplate,
        OutputMappings = OutputMappings.Select(m => m.ToModel()).ToList(),
    };
}

/// <summary>数据处理节点编辑器。</summary>
public sealed partial class TransformNodeEditor : NodeConfigEditor
{
    public TransformNodeEditor(WorkflowNode node, DesignerContext context)
        : base(node, context)
    {
        var config = WorkflowJson.TryGetConfig<TransformNodeConfig>(node.Config, out _) ?? new TransformNodeConfig();
        _baseExpression = config.BaseExpression ?? string.Empty;
        foreach (var operation in config.Operations)
        {
            Operations.Add(new TransformOpRow(operation));
        }

        WatchCollection(Operations);
        SelectedOperation = Operations.FirstOrDefault();
    }

    [ObservableProperty]
    private string _baseExpression;

    [ObservableProperty]
    private TransformOpRow? _selectedOperation;

    public ObservableCollection<TransformOpRow> Operations { get; } = new();

    [RelayCommand]
    private void AddOperation()
    {
        var row = new TransformOpRow();
        Operations.Add(row);
        SelectedOperation = row;
    }

    [RelayCommand]
    private void RemoveOperation()
    {
        if (SelectedOperation is { } row)
        {
            Operations.Remove(row);
            SelectedOperation = Operations.FirstOrDefault();
        }
    }

    [RelayCommand]
    private void MoveOperationUp()
    {
        if (SelectedOperation is { } row)
        {
            var index = Operations.IndexOf(row);
            if (index > 0)
            {
                Operations.Move(index, index - 1);
            }
        }
    }

    [RelayCommand]
    private void MoveOperationDown()
    {
        if (SelectedOperation is { } row)
        {
            var index = Operations.IndexOf(row);
            if (index >= 0 && index < Operations.Count - 1)
            {
                Operations.Move(index, index + 1);
            }
        }
    }

    public override object BuildConfig() => new TransformNodeConfig
    {
        BaseExpression = string.IsNullOrWhiteSpace(BaseExpression) ? null : BaseExpression.Trim(),
        Operations = Operations.Select(o => o.ToModel()).ToList(),
    };
}

/// <summary>条件分支节点编辑器。</summary>
public sealed partial class ConditionNodeEditor : NodeConfigEditor
{
    public ConditionNodeEditor(WorkflowNode node, DesignerContext context)
        : base(node, context)
    {
        var config = WorkflowJson.TryGetConfig<ConditionNodeConfig>(node.Config, out _) ?? new ConditionNodeConfig();
        foreach (var branch in config.Branches)
        {
            Branches.Add(new BranchRow(branch));
        }

        WatchCollection(Branches);
        SelectedBranch = Branches.FirstOrDefault();
    }

    [ObservableProperty]
    private BranchRow? _selectedBranch;

    [ObservableProperty]
    private string _trialContextJson = "{}";

    [ObservableProperty]
    private string _trialResult = "输入试算上下文后点击“试算”。";

    public ObservableCollection<BranchRow> Branches { get; } = new();

    [RelayCommand]
    private void AddBranch()
    {
        var row = new BranchRow(new ConditionBranch
        {
            Id = "branch" + (Branches.Count + 1),
            Name = "分支 " + (Branches.Count + 1),
        });
        Branches.Add(row);
        SelectedBranch = row;
    }

    [RelayCommand]
    private void RemoveBranch()
    {
        if (SelectedBranch is { } row)
        {
            Branches.Remove(row);
            SelectedBranch = Branches.FirstOrDefault();
        }
    }

    [RelayCommand]
    private void TrialEvaluate()
    {
        TrialResult = SelectedBranch is not { IsDefault: false } branch
            ? "默认分支不需要条件表达式。"
            : DesignerTrial.Evaluate(branch.Condition, TrialContextJson);
    }

    public override object BuildConfig() => new ConditionNodeConfig
    {
        Branches = Branches.Select(b => new ConditionBranch
        {
            Id = b.Id.Trim(),
            Name = b.Name,
            Condition = string.IsNullOrWhiteSpace(b.Condition) ? null : b.Condition.Trim(),
            IsDefault = b.IsDefault,
        }).ToList(),
    };
}
