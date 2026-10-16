using System.Text.Json.Nodes;

namespace LlamaLauncher.Domain.Workflows;

/// <summary>节点重试策略。</summary>
public sealed class RetryPolicy
{
    /// <summary>最大尝试次数（含首次；1 表示不重试）。</summary>
    public int MaxAttempts { get; set; } = 1;

    /// <summary>重试间隔（毫秒）。</summary>
    public int DelayMilliseconds { get; set; } = 500;

    /// <summary>触发重试的错误条件。</summary>
    public RetryCondition Condition { get; set; } = RetryCondition.Never;

    /// <summary>是否为有效的重试配置（次数与间隔在允许范围内）。</summary>
    public bool IsEffective => MaxAttempts > 1 && Condition != RetryCondition.Never;
}

/// <summary>工作流节点定义（与 WPF 控件无关的可序列化模型）。</summary>
public sealed class WorkflowNode
{
    /// <summary>节点 ID（工作流内唯一、稳定，用于连线与运行记录）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>节点类型标识（见 NodeTypeCatalog，如 core.start）。</summary>
    public string TypeId { get; set; } = string.Empty;

    /// <summary>节点类型版本（用于兼容性校验）。</summary>
    public int TypeVersion { get; set; } = 1;

    /// <summary>显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>说明/注释。</summary>
    public string? Notes { get; set; }

    /// <summary>画布 X 坐标。</summary>
    public double X { get; set; }

    /// <summary>画布 Y 坐标。</summary>
    public double Y { get; set; }

    /// <summary>颜色标识（分组/可读性，可空）。</summary>
    public string? ColorTag { get; set; }

    /// <summary>调试断点（随工作流保存，便于复用调试）。</summary>
    public bool IsBreakpoint { get; set; }

    /// <summary>失败策略。</summary>
    public NodeFailurePolicy FailurePolicy { get; set; } = NodeFailurePolicy.Terminate;

    /// <summary>节点超时（秒；null 使用工作流默认值）。</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>重试策略。</summary>
    public RetryPolicy Retry { get; set; } = new();

    /// <summary>输入映射（来源表达式 → 目标字段）。</summary>
    public List<WorkflowDataMapping> InputMappings { get; set; } = new();

    /// <summary>把主输出写入的工作流变量名（可空）。</summary>
    public string? OutputVariable { get; set; }

    /// <summary>节点专属配置（类型化配置类经 WorkflowJson.ToConfigObject 写入）。</summary>
    public JsonObject? Config { get; set; }

    /// <summary>浅拷贝（Config 与集合作深拷贝，便于撤销/重做快照）。</summary>
    public WorkflowNode CopyShallow() => new()
    {
        Id = Id,
        TypeId = TypeId,
        TypeVersion = TypeVersion,
        Name = Name,
        Notes = Notes,
        X = X,
        Y = Y,
        ColorTag = ColorTag,
        IsBreakpoint = IsBreakpoint,
        FailurePolicy = FailurePolicy,
        TimeoutSeconds = TimeoutSeconds,
        Retry = new RetryPolicy
        {
            MaxAttempts = Retry.MaxAttempts,
            DelayMilliseconds = Retry.DelayMilliseconds,
            Condition = Retry.Condition,
        },
        InputMappings = InputMappings
            .Select(m => new WorkflowDataMapping
            {
                Target = m.Target,
                Source = m.Source,
                DefaultValueJson = m.DefaultValueJson,
                Required = m.Required,
                Convert = m.Convert,
            })
            .ToList(),
        OutputVariable = OutputVariable,
        Config = Config?.DeepClone() as JsonObject,
    };
}
