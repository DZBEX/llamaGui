namespace LlamaLauncher.Domain.Workflows;

/// <summary>工作流连线（有向边；分支标识用于条件/规则/人工/并行等节点的路径选择）。</summary>
public sealed class WorkflowEdge
{
    /// <summary>连线 ID（工作流内唯一）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>源节点 ID。</summary>
    public string SourceNodeId { get; set; } = string.Empty;

    /// <summary>源端口名（默认 out）。</summary>
    public string SourcePort { get; set; } = "out";

    /// <summary>目标节点 ID。</summary>
    public string TargetNodeId { get; set; } = string.Empty;

    /// <summary>目标端口名（默认 in）。</summary>
    public string TargetPort { get; set; } = "in";

    /// <summary>分支标识（可空：无分支语义的普通连线）。</summary>
    public string? BranchId { get; set; }

    /// <summary>连线说明（可空）。</summary>
    public string? Label { get; set; }

    /// <summary>浅拷贝。</summary>
    public WorkflowEdge CopyShallow() => new()
    {
        Id = Id,
        SourceNodeId = SourceNodeId,
        SourcePort = SourcePort,
        TargetNodeId = TargetNodeId,
        TargetPort = TargetPort,
        BranchId = BranchId,
        Label = Label,
    };
}
