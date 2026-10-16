using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.App.ViewModels.Lab;

/// <summary>节点库分组。</summary>
public sealed class NodeLibraryGroup
{
    public required string Category { get; init; }

    public required ObservableCollection<NodeLibraryItem> Items { get; init; }
}

/// <summary>节点库条目。</summary>
public sealed class NodeLibraryItem
{
    public required NodeTypeDescriptor Descriptor { get; init; }

    public string DisplayName => Descriptor.DisplayName;

    public string Glyph => Descriptor.Glyph;

    public string Description => Descriptor.Description;
}

/// <summary>运行日志行（UI 展示）。</summary>
public sealed class WorkflowLogRow
{
    public required WorkflowLogEntry Entry { get; init; }

    public string TimeText => Entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");

    public string LevelText => Entry.Level switch
    {
        WorkflowLogLevel.Trace => "跟踪",
        WorkflowLogLevel.Debug => "调试",
        WorkflowLogLevel.Info => "信息",
        WorkflowLogLevel.Warning => "警告",
        _ => "错误",
    };

    public string NodeText => Entry.NodeId ?? "—";

    public string Message => Entry.Message;
}

/// <summary>设计器校验问题行（与启动配置页的同名行类型区分）。</summary>
public sealed class DesignerValidationRow
{
    public required WorkflowIssue Issue { get; init; }

    public string SeverityText => Issue.Severity switch
    {
        WorkflowIssueSeverity.Error => "错误",
        WorkflowIssueSeverity.Warning => "警告",
        _ => "信息",
    };

    public string Code => Issue.Code;

    public string Message => Issue.Message;

    public string? Hint => Issue.Hint;

    public string NodeId => Issue.NodeId ?? string.Empty;

    public bool IsError => Issue.Severity == WorkflowIssueSeverity.Error;
}

/// <summary>节点运行状态行（节点详情表）。</summary>
public sealed partial class NodeStateRow : ObservableObject
{
    public required string NodeId { get; init; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _stateText = "未运行";

    [ObservableProperty]
    private string _durationText = "—";

    [ObservableProperty]
    private int _retries;

    [ObservableProperty]
    private string? _branch;

    [ObservableProperty]
    private string? _error;

    public NodeRunState? State { get; set; }

    /// <summary>是否已执行过（用于运行轨迹计数）。</summary>
    public bool Started { get; set; }
}

/// <summary>数据映射编辑行。</summary>
public sealed partial class MappingRow : ObservableObject
{
    public MappingRow(WorkflowDataMapping? mapping = null)
    {
        if (mapping is not null)
        {
            _target = mapping.Target;
            _source = mapping.Source ?? string.Empty;
            _convert = mapping.Convert ?? string.Empty;
            _required = mapping.Required;
            _defaultValueJson = mapping.DefaultValueJson ?? string.Empty;
        }
    }

    [ObservableProperty]
    private string _target = string.Empty;

    [ObservableProperty]
    private string _source = string.Empty;

    [ObservableProperty]
    private string _convert = string.Empty;

    [ObservableProperty]
    private bool _required;

    [ObservableProperty]
    private string _defaultValueJson = string.Empty;

    public WorkflowDataMapping ToModel() => new()
    {
        Target = Target.Trim(),
        Source = string.IsNullOrWhiteSpace(Source) ? null : Source.Trim(),
        Convert = string.IsNullOrWhiteSpace(Convert) ? null : Convert.Trim(),
        Required = Required,
        DefaultValueJson = string.IsNullOrWhiteSpace(DefaultValueJson) ? null : DefaultValueJson.Trim(),
    };
}

/// <summary>工作流输入/输出变量编辑行。</summary>
public sealed partial class WorkflowVariableRow : ObservableObject
{
    public WorkflowVariableRow(WorkflowVariable? variable = null)
    {
        if (variable is not null)
        {
            _name = variable.Name;
            _type = variable.Type;
            _required = variable.Required;
            _defaultValueJson = variable.DefaultValueJson ?? string.Empty;
            _description = variable.Description ?? string.Empty;
            _exampleJson = variable.ExampleJson ?? string.Empty;
        }
    }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private WorkflowValueType _type = WorkflowValueType.Any;

    [ObservableProperty]
    private bool _required;

    [ObservableProperty]
    private string _defaultValueJson = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _exampleJson = string.Empty;

    public WorkflowVariable ToModel() => new()
    {
        Name = Name.Trim(),
        Type = Type,
        Required = Required,
        DefaultValueJson = string.IsNullOrWhiteSpace(DefaultValueJson) ? null : DefaultValueJson.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        ExampleJson = string.IsNullOrWhiteSpace(ExampleJson) ? null : ExampleJson.Trim(),
    };
}

/// <summary>分支编辑行（条件 / 并行 / 人工确认共用）。</summary>
public sealed partial class BranchRow : ObservableObject
{
    public BranchRow(ConditionBranch? branch = null)
    {
        if (branch is not null)
        {
            _id = branch.Id;
            _name = branch.Name;
            _condition = branch.Condition ?? string.Empty;
            _isDefault = branch.IsDefault;
        }
    }

    public BranchRow(ParallelBranch? branch = null)
    {
        if (branch is not null)
        {
            _id = branch.Id;
            _name = branch.Name;
        }
    }

    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _condition = string.Empty;

    [ObservableProperty]
    private bool _isDefault;
}

/// <summary>决策问题编辑行（选项/等级用文本简化编辑）。</summary>
public sealed partial class DecisionQuestionRow : ObservableObject
{
    public DecisionQuestionRow(DecisionQuestion? question = null)
    {
        if (question is null)
        {
            return;
        }

        _id = question.Id;
        _type = question.Type;
        _instructions = question.Instructions;
        _optionsText = string.Join(
            "\n",
            question.Options.Select(o => string.IsNullOrWhiteSpace(o.Description) ? o.Key : $"{o.Key}={o.Description}"));
        _levelsText = string.Join("\n", question.Levels);
        _trueDescription = question.TrueDescription ?? string.Empty;
        _falseDescription = question.FalseDescription ?? string.Empty;
    }

    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _type = "choice";

    [ObservableProperty]
    private string _instructions = string.Empty;

    [ObservableProperty]
    private string _optionsText = string.Empty;

    [ObservableProperty]
    private string _levelsText = string.Empty;

    [ObservableProperty]
    private string _trueDescription = string.Empty;

    [ObservableProperty]
    private string _falseDescription = string.Empty;

    public DecisionQuestion ToModel() => new()
    {
        Id = Id.Trim(),
        Type = string.IsNullOrWhiteSpace(Type) ? "choice" : Type.Trim().ToLowerInvariant(),
        Instructions = Instructions,
        Options = OptionsText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var separator = line.IndexOf('=');
                return separator < 0
                    ? new DecisionOption { Key = line }
                    : new DecisionOption { Key = line[..separator].Trim(), Description = line[(separator + 1)..].Trim() };
            })
            .Where(o => o.Key.Length > 0)
            .ToList(),
        Levels = LevelsText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList(),
        TrueDescription = string.IsNullOrWhiteSpace(TrueDescription) ? null : TrueDescription,
        FalseDescription = string.IsNullOrWhiteSpace(FalseDescription) ? null : FalseDescription,
    };
}

/// <summary>规则编辑行。</summary>
public sealed partial class RuleRow : ObservableObject
{
    public RuleRow(RuleDefinition? rule = null)
    {
        if (rule is not null)
        {
            _id = rule.Id;
            _name = rule.Name;
            _condition = rule.Condition ?? string.Empty;
            _severity = rule.Severity;
            _messageTemplate = rule.MessageTemplate ?? string.Empty;
        }
    }

    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _condition = string.Empty;

    [ObservableProperty]
    private WorkflowIssueSeverity _severity = WorkflowIssueSeverity.Error;

    [ObservableProperty]
    private string _messageTemplate = string.Empty;

    public RuleDefinition ToModel() => new()
    {
        Id = Id.Trim(),
        Name = Name,
        Condition = string.IsNullOrWhiteSpace(Condition) ? null : Condition.Trim(),
        Severity = Severity,
        MessageTemplate = string.IsNullOrWhiteSpace(MessageTemplate) ? null : MessageTemplate,
    };
}

/// <summary>人工确认字段编辑行。</summary>
public sealed partial class ManualFieldRow : ObservableObject
{
    public ManualFieldRow(ManualField? field = null)
    {
        if (field is not null)
        {
            _id = field.Id;
            _label = field.Label;
            _defaultValue = field.DefaultValue ?? string.Empty;
            _required = field.Required;
        }
    }

    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _defaultValue = string.Empty;

    [ObservableProperty]
    private bool _required;

    public ManualField ToModel() => new()
    {
        Id = Id.Trim(),
        Label = Label,
        DefaultValue = string.IsNullOrWhiteSpace(DefaultValue) ? null : DefaultValue,
        Required = Required,
    };
}
