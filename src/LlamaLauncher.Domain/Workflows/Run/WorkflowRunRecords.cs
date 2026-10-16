namespace LlamaLauncher.Domain.Workflows;

/// <summary>单节点运行记录（输入/输出快照受大小限制；不可采集的耗时以 null 表示）。</summary>
public sealed class NodeRunRecord
{
    /// <summary>节点 ID。</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>节点类型 ID 快照。</summary>
    public string NodeTypeId { get; set; } = string.Empty;

    /// <summary>节点名称快照。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>最终状态。</summary>
    public NodeRunState State { get; set; }

    /// <summary>开始时间。</summary>
    public DateTimeOffset? StartedUtc { get; set; }

    /// <summary>结束时间。</summary>
    public DateTimeOffset? EndedUtc { get; set; }

    /// <summary>耗时（毫秒；无法采集时为 null）。</summary>
    public double? DurationMs { get; set; }

    /// <summary>重试次数（不含首次）。</summary>
    public int Retries { get; set; }

    /// <summary>实际走的分支 ID（可空）。</summary>
    public string? BranchTaken { get; set; }

    /// <summary>输入快照（JSON 文本）。</summary>
    public string? InputJson { get; set; }

    /// <summary>输出快照（JSON 文本）。</summary>
    public string? OutputJson { get; set; }

    /// <summary>输入是否被截断。</summary>
    public bool InputTruncated { get; set; }

    /// <summary>输出是否被截断。</summary>
    public bool OutputTruncated { get; set; }

    /// <summary>错误类型（异常类名或分类）。</summary>
    public string? ErrorType { get; set; }

    /// <summary>错误信息（脱敏）。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>错误上下文（异常处理节点使用：type、message、nodeId、attempts）。</summary>
    public string? ErrorContextJson { get; set; }

    /// <summary>模型节点诊断信息（脱敏的请求摘要、token 用量、耗时等）。</summary>
    public string? ModelDiagnosticsJson { get; set; }

    /// <summary>本次执行写入的工作流变量（变量名 → 值）。</summary>
    public string? VariablesJson { get; set; }

    /// <summary>运行轨迹序号（执行顺序）。</summary>
    public int Sequence { get; set; }
}

/// <summary>运行日志条目。</summary>
public sealed class WorkflowLogEntry
{
    /// <summary>序号（稳定排序）。</summary>
    public int Sequence { get; set; }

    /// <summary>时间（UTC）。</summary>
    public DateTimeOffset TimestampUtc { get; set; }

    /// <summary>级别。</summary>
    public WorkflowLogLevel Level { get; set; }

    /// <summary>关联节点（可空）。</summary>
    public string? NodeId { get; set; }

    /// <summary>消息（脱敏）。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>异常信息（脱敏，可空）。</summary>
    public string? ExceptionText { get; set; }
}

/// <summary>历史列表用的运行摘要（不加载大字段）。</summary>
public sealed class WorkflowRunSummary
{
    /// <summary>运行 ID。</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>工作流 ID。</summary>
    public string WorkflowId { get; set; } = string.Empty;

    /// <summary>工作流名称快照。</summary>
    public string WorkflowNameSnapshot { get; set; } = string.Empty;

    /// <summary>版本快照标签（可空）。</summary>
    public string? VersionLabel { get; set; }

    /// <summary>开始时间。</summary>
    public DateTimeOffset StartedUtc { get; set; }

    /// <summary>结束时间。</summary>
    public DateTimeOffset? EndedUtc { get; set; }

    /// <summary>总耗时（毫秒；无法采集时为 null）。</summary>
    public double? DurationMs { get; set; }

    /// <summary>最终状态。</summary>
    public WorkflowRunState State { get; set; }

    /// <summary>测试模式。</summary>
    public WorkflowTestMode TestMode { get; set; }

    /// <summary>错误摘要（可空）。</summary>
    public string? ErrorSummary { get; set; }
}

/// <summary>一次完整运行的记录（含不可变定义快照，供复现与导出）。</summary>
public sealed class WorkflowRunRecord
{
    /// <summary>运行 ID（GUID 字符串）。</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>工作流 ID。</summary>
    public string WorkflowId { get; set; } = string.Empty;

    /// <summary>工作流名称快照。</summary>
    public string WorkflowNameSnapshot { get; set; } = string.Empty;

    /// <summary>版本快照标签（可空表示未使用快照，直接使用当前定义）。</summary>
    public string? VersionLabel { get; set; }

    /// <summary>应用版本。</summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>开始时间。</summary>
    public DateTimeOffset StartedUtc { get; set; }

    /// <summary>结束时间。</summary>
    public DateTimeOffset? EndedUtc { get; set; }

    /// <summary>总耗时（毫秒；无法采集时为 null）。</summary>
    public double? DurationMs { get; set; }

    /// <summary>最终状态。</summary>
    public WorkflowRunState State { get; set; }

    /// <summary>测试模式。</summary>
    public WorkflowTestMode TestMode { get; set; }

    /// <summary>输入快照（JSON 文本）。</summary>
    public string? InputSnapshotJson { get; set; }

    /// <summary>输出 JSON 文本。</summary>
    public string? OutputJson { get; set; }

    /// <summary>错误摘要。</summary>
    public string? ErrorSummary { get; set; }

    /// <summary>非敏感的模型上下文快照（实例名、端口、模型文件标识等）。</summary>
    public string? ModelContextJson { get; set; }

    /// <summary>运行时的不可变定义快照（JSON 文本；历史不随工作流编辑变化）。</summary>
    public string? DefinitionSnapshotJson { get; set; }

    /// <summary>最终校验/断言结果摘要。</summary>
    public string? ValidationSummary { get; set; }

    /// <summary>运行结束时的变量快照（JSON 文本）。</summary>
    public string? FinalVariablesJson { get; set; }

    /// <summary>节点运行记录（按执行顺序）。</summary>
    public List<NodeRunRecord> Nodes { get; set; } = new();

    /// <summary>运行日志。</summary>
    public List<WorkflowLogEntry> Logs { get; set; } = new();
}

/// <summary>工作流版本快照（不可变；运行历史通过标签关联）。</summary>
public sealed class WorkflowVersionSnapshot
{
    /// <summary>快照 ID。</summary>
    public string SnapshotId { get; set; } = string.Empty;

    /// <summary>工作流 ID。</summary>
    public string WorkflowId { get; set; } = string.Empty;

    /// <summary>标签（如 v1、修复分支）。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>说明（可空）。</summary>
    public string? Note { get; set; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>快照内容（完整定义）。</summary>
    public WorkflowDefinition? Definition { get; set; }
}
