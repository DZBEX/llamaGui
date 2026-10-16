using System.Text.Json.Nodes;

namespace LlamaLauncher.Domain.Workflows;

/// <summary>开始节点配置（输入定义在 <see cref="WorkflowDefinition.Inputs"/>，此处无额外配置）。</summary>
public sealed class StartNodeConfig
{
}

/// <summary>结束节点配置。</summary>
public sealed class EndNodeConfig
{
    /// <summary>结束状态。</summary>
    public WorkflowEndStatus Status { get; set; } = WorkflowEndStatus.Succeeded;

    /// <summary>输出映射：目标名 → 来源表达式（映射到工作流输出）。</summary>
    public List<WorkflowDataMapping> OutputMappings { get; set; } = new();

    /// <summary>业务失败时的说明模板（可含 {{}} 占位符）。</summary>
    public string? FailureMessageTemplate { get; set; }
}

/// <summary>数据处理操作类型。</summary>
public enum TransformOperationKind
{
    /// <summary>表达式求值写入目标字段。</summary>
    Assign = 0,

    /// <summary>从对象挑出字段子集（Source 对象、Targets 字段名列表）。</summary>
    Pick = 1,

    /// <summary>字段重命名（Source 字段路径 → Target 字段名）。</summary>
    Rename = 2,

    /// <summary>删除目标字段。</summary>
    Remove = 3,

    /// <summary>目标为空（null/缺失）时写入默认值。</summary>
    Default = 4,

    /// <summary>目标字段转为字符串。</summary>
    ToString = 5,

    /// <summary>目标字段转为数值。</summary>
    ToNumber = 6,

    /// <summary>目标字段转为布尔。</summary>
    ToBoolean = 7,

    /// <summary>字符串按分隔符拆分为数组。</summary>
    Split = 8,

    /// <summary>数组按分隔符连接为字符串。</summary>
    Join = 9,

    /// <summary>数组过滤（Filter 表达式，逐项绑定 item/index）。</summary>
    Filter = 10,

    /// <summary>数组排序（Source 表达式键，Descending 控制方向）。</summary>
    Sort = 11,

    /// <summary>数组截断到最多 Limit 项。</summary>
    Limit = 12,

    /// <summary>对象合并（Source 表达式对象合并到目标对象，后者优先）。</summary>
    Merge = 13,

    /// <summary>字符串解析为 JSON。</summary>
    ParseJson = 14,

    /// <summary>值序列化为 JSON 字符串。</summary>
    Stringify = 15,

    /// <summary>由映射列表构造对象。</summary>
    BuildObject = 16,

    /// <summary>模板渲染写入目标字段。</summary>
    Template = 17,
}

/// <summary>单条数据处理操作（按 Kind 使用相应字段，校验器逐项检查必填）。</summary>
public sealed class TransformOperation
{
    /// <summary>操作 ID（稳定标识，便于 UI 编辑与撤销）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>操作类型。</summary>
    public TransformOperationKind Kind { get; set; }

    /// <summary>通用说明（可空）。</summary>
    public string? Label { get; set; }

    /// <summary>目标字段路径（支持点路径与 [n] 索引）。</summary>
    public string? Target { get; set; }

    /// <summary>来源表达式（赋值的值、过滤/排序/合并的输入、字符串转换对象等）。</summary>
    public string? Source { get; set; }

    /// <summary>键表达式（Sort 排序键；逐项绑定 item/index）。</summary>
    public string? KeyExpression { get; set; }

    /// <summary>谓词表达式（Filter 过滤条件；逐项绑定 item/index，结果为布尔）。</summary>
    public string? PredicateExpression { get; set; }

    /// <summary>目标字段列表（Pick 用）。</summary>
    public List<string> Fields { get; set; } = new();

    /// <summary>字面量默认值（Default 用）。</summary>
    public string? DefaultValueJson { get; set; }

    /// <summary>分隔符（Split/Join 用）。</summary>
    public string? Separator { get; set; }

    /// <summary>排序方向（Sort 用，true=降序）。</summary>
    public bool Descending { get; set; }

    /// <summary>数量限制（Limit 用）。</summary>
    public int? Limit { get; set; }

    /// <summary>模板文本（Template 用）。</summary>
    public string? TemplateText { get; set; }

    /// <summary>构造对象字段映射（BuildObject 用）。</summary>
    public List<WorkflowDataMapping> Mappings { get; set; } = new();
}

/// <summary>数据处理节点配置。</summary>
public sealed class TransformNodeConfig
{
    /// <summary>可选基础输入表达式（默认使用上游输出文档；设置后操作基于该值开始）。</summary>
    public string? BaseExpression { get; set; }

    /// <summary>操作列表（按顺序执行）。</summary>
    public List<TransformOperation> Operations { get; set; } = new();
}

/// <summary>条件分支定义。</summary>
public sealed class ConditionBranch
{
    /// <summary>分支 ID（连线 BranchId 对应值）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>条件表达式（默认分支为空）。</summary>
    public string? Condition { get; set; }

    /// <summary>是否为默认分支（恰好一个）。</summary>
    public bool IsDefault { get; set; }
}

/// <summary>条件分支节点配置。</summary>
public sealed class ConditionNodeConfig
{
    /// <summary>分支列表（按顺序求值，命中即选中；默认分支兜底）。</summary>
    public List<ConditionBranch> Branches { get; set; } = new();
}

/// <summary>决策问题的选项（choice 类型）。</summary>
public sealed class DecisionOption
{
    /// <summary>选项键。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>选项说明（可空）。</summary>
    public string? Description { get; set; }
}

/// <summary>决策问题定义（choice/score/noul，字符串字段支持 {{}} 模板）。</summary>
public sealed class DecisionQuestion
{
    /// <summary>问题 ID（响应 answers 的键）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>问题类型：choice / score / noul。</summary>
    public string Type { get; set; } = "choice";

    /// <summary>问题说明（模板）。</summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>choice 选项。</summary>
    public List<DecisionOption> Options { get; set; } = new();

    /// <summary>score 等级（从低到高）。</summary>
    public List<string> Levels { get; set; } = new();

    /// <summary>noul=true 的说明（可空）。</summary>
    public string? TrueDescription { get; set; }

    /// <summary>noul=false 的说明（可空）。</summary>
    public string? FalseDescription { get; set; }
}

/// <summary>决策模型节点配置（/v1/systemone）。</summary>
public sealed class DecisionNodeConfig
{
    /// <summary>绑定的服务实例 ID（GUID 字符串；null 表示使用运行上下文指定的默认实例）。</summary>
    public string? InstanceId { get; set; }

    /// <summary>实例名称快照（便于诊断；不参与执行）。</summary>
    public string? InstanceNameSnapshot { get; set; }

    /// <summary>state 文本模板（用于字符串状态）。</summary>
    public string StateTemplate { get; set; } = string.Empty;

    /// <summary>state JSON 模板（可选；设置后优先于 StateTemplate，渲染后解析为 JSON 对象/数组）。</summary>
    public string? StateJsonTemplate { get; set; }

    /// <summary>问题列表。</summary>
    public List<DecisionQuestion> Questions { get; set; } = new();

    /// <summary>请求超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>响应大小上限（字节；null 使用工作流默认值）。</summary>
    public int? MaxResponseBytes { get; set; }
}

/// <summary>规则定义。</summary>
public sealed class RuleDefinition
{
    /// <summary>规则 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>规则名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>条件表达式（必须为布尔；可空表示“始终命中”，用于无条件消息）。</summary>
    public string? Condition { get; set; }

    /// <summary>级别：Error（硬错误）或 Warning（警告）。</summary>
    public WorkflowIssueSeverity Severity { get; set; } = WorkflowIssueSeverity.Error;

    /// <summary>命中时的消息模板。</summary>
    public string? MessageTemplate { get; set; }
}

/// <summary>规则校验节点配置。</summary>
public sealed class RulesNodeConfig
{
    /// <summary>规则列表。</summary>
    public List<RuleDefinition> Rules { get; set; } = new();

    /// <summary>失败处理：停止 / fail 分支 / 继续。</summary>
    public RuleFailureAction OnFailure { get; set; } = RuleFailureAction.Stop;
}

/// <summary>算法求解节点配置。</summary>
public sealed class AlgorithmNodeConfig
{
    /// <summary>求解器键（见运行环境中注册的算法实现）。</summary>
    public string SolverKey { get; set; } = string.Empty;

    /// <summary>输入 JSON 模板（字符串值支持 {{}} 模板；渲染后解析为对象）。</summary>
    public string? InputJsonTemplate { get; set; }

    /// <summary>超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>循环节点配置。</summary>
public sealed class LoopNodeConfig
{
    /// <summary>循环模式。</summary>
    public LoopMode Mode { get; set; } = LoopMode.SubWorkflow;

    /// <summary>数组来源表达式（必须求值为数组）。</summary>
    public string ItemsExpression { get; set; } = string.Empty;

    /// <summary>最大迭代次数（1–1000，防止无限循环）。</summary>
    public int MaxIterations { get; set; } = 100;

    /// <summary>单项失败时是否继续后续项（true 时输出错误明细）。</summary>
    public bool ContinueOnItemError { get; set; }

    /// <summary>子工作流模式下引用的工作流 ID。</summary>
    public string? SubWorkflowId { get; set; }

    /// <summary>子工作流名称快照（诊断用）。</summary>
    public string? SubWorkflowNameSnapshot { get; set; }

    /// <summary>单项超时（秒；null 使用默认值）。</summary>
    public int? ItemTimeoutSeconds { get; set; }

    /// <summary>决策模型模式下的模板配置（state/问题中可使用 {{ item.* }} 与 {{ index }}）。</summary>
    public DecisionNodeConfig? DecisionTemplate { get; set; }
}

/// <summary>并行分支定义。</summary>
public sealed class ParallelBranch
{
    /// <summary>分支 ID（连线 BranchId 对应值）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名称。</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>并行节点配置（扇出）。</summary>
public sealed class ParallelNodeConfig
{
    /// <summary>分支列表（每分支对应一条带 BranchId 的出边）。</summary>
    public List<ParallelBranch> Branches { get; set; } = new();

    /// <summary>本节点分支最大并发（null = 使用运行设置；1–8）。</summary>
    public int? MaxConcurrency { get; set; }

    /// <summary>传递给各分支的共享值表达式（可空；写入分支节点输入文档的 shared 字段）。</summary>
    public string? SharedExpression { get; set; }
}

/// <summary>汇合节点配置（扇入）。</summary>
public sealed class JoinNodeConfig
{
    /// <summary>汇合策略。</summary>
    public ParallelJoinMode Mode { get; set; } = ParallelJoinMode.AllComplete;

    /// <summary>可选：期望到达的上游分支（BranchId 逗号分隔，用于校验提示）。</summary>
    public string? ExpectedBranches { get; set; }
}

/// <summary>人工确认字段。</summary>
public sealed class ManualField
{
    /// <summary>字段键。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示标签。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>默认值（文本）。</summary>
    public string? DefaultValue { get; set; }

    /// <summary>是否必填。</summary>
    public bool Required { get; set; }
}

/// <summary>人工确认节点配置。</summary>
public sealed class ManualConfirmNodeConfig
{
    /// <summary>标题。</summary>
    public string Title { get; set; } = "请确认";

    /// <summary>说明模板。</summary>
    public string MessageTemplate { get; set; } = string.Empty;

    /// <summary>可填写字段。</summary>
    public List<ManualField> Fields { get; set; } = new();

    /// <summary>批准按钮文本。</summary>
    public string ApproveLabel { get; set; } = "批准";

    /// <summary>拒绝按钮文本。</summary>
    public string RejectLabel { get; set; } = "拒绝";
}

/// <summary>子工作流节点配置。</summary>
public sealed class SubWorkflowNodeConfig
{
    /// <summary>子工作流 ID。</summary>
    public string WorkflowId { get; set; } = string.Empty;

    /// <summary>子工作流名称快照（诊断用）。</summary>
    public string? WorkflowNameSnapshot { get; set; }

    /// <summary>超时（秒；null 使用默认值）。</summary>
    public int? TimeoutSeconds { get; set; }
}

/// <summary>异常处理节点配置。</summary>
public sealed class ExceptionNodeConfig
{
    /// <summary>动作。</summary>
    public ExceptionAction Action { get; set; } = ExceptionAction.Capture;

    /// <summary>是否写入运行日志。</summary>
    public bool RecordLog { get; set; } = true;
}
