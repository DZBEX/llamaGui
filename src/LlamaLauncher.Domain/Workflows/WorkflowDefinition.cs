using System.Text.Json.Nodes;

namespace LlamaLauncher.Domain.Workflows;

/// <summary>工作流默认执行设置。</summary>
public sealed class WorkflowExecutionDefaults
{
    /// <summary>最大节点并发数（1–8；受本地模型资源限制）。</summary>
    public int MaxNodeParallelism { get; set; } = 2;

    /// <summary>默认节点超时（秒）。</summary>
    public int DefaultNodeTimeoutSeconds { get; set; } = 120;

    /// <summary>单次运行最大时长（秒）。</summary>
    public int MaxRunSeconds { get; set; } = 1800;

    /// <summary>默认测试模式。</summary>
    public WorkflowTestMode DefaultTestMode { get; set; } = WorkflowTestMode.LocalModel;

    /// <summary>循环节点默认最大迭代次数。</summary>
    public int MaxLoopIterations { get; set; } = 100;

    /// <summary>模型响应最大字节数（超出视为错误）。</summary>
    public int MaxResponseBytes { get; set; } = 1_048_576;

    /// <summary>单节点输入/输出快照上限（字符数；超出截断并标记）。</summary>
    public int MaxNodeSnapshotChars { get; set; } = 262_144;
}

/// <summary>工作流定义（版本化、可序列化的根对象）。</summary>
public sealed class WorkflowDefinition
{
    /// <summary>当前支持的 schema 版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>定义 schema 版本。</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>工作流唯一 ID（GUID 字符串）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>说明。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>标签。</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>更新时间（UTC）。</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>备注。</summary>
    public string? Notes { get; set; }

    /// <summary>工作流输入定义（开始节点运行时校验）。</summary>
    public List<WorkflowVariable> Inputs { get; set; } = new();

    /// <summary>工作流输出定义（结束节点输出映射参考）。</summary>
    public List<WorkflowVariable> Outputs { get; set; } = new();

    /// <summary>节点集合。</summary>
    public List<WorkflowNode> Nodes { get; set; } = new();

    /// <summary>连线集合。</summary>
    public List<WorkflowEdge> Edges { get; set; } = new();

    /// <summary>默认执行设置。</summary>
    public WorkflowExecutionDefaults Defaults { get; set; } = new();

    /// <summary>按 ID 查找节点。</summary>
    public WorkflowNode? FindNode(string? nodeId)
        => string.IsNullOrEmpty(nodeId) ? null : Nodes.FirstOrDefault(n => n.Id == nodeId);

    /// <summary>按 ID 查找连线。</summary>
    public WorkflowEdge? FindEdge(string? edgeId)
        => string.IsNullOrEmpty(edgeId) ? null : Edges.FirstOrDefault(e => e.Id == edgeId);

    /// <summary>节点的出边。</summary>
    public IEnumerable<WorkflowEdge> OutgoingEdges(string nodeId)
        => Edges.Where(e => e.SourceNodeId == nodeId);

    /// <summary>节点的入边。</summary>
    public IEnumerable<WorkflowEdge> IncomingEdges(string nodeId)
        => Edges.Where(e => e.TargetNodeId == nodeId);

    /// <summary>深拷贝（JSON 往返，保证与序列化语义一致）。</summary>
    public WorkflowDefinition Clone()
    {
        var json = WorkflowJson.Serialize(this, indented: false);
        return WorkflowJson.Deserialize<WorkflowDefinition>(json)
            ?? throw new InvalidOperationException("工作流克隆失败。");
    }

    /// <summary>空的执行输入文档。</summary>
    public JsonObject BuildEmptyInputDocument() => new();
}
