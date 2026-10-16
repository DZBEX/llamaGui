using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.Application.Workflows;

/// <summary>工作流列表摘要（不加载完整定义）。</summary>
public sealed record WorkflowDefinitionSummary(
    string WorkflowId,
    string Name,
    string? Description,
    int NodeCount,
    int EdgeCount,
    DateTimeOffset UpdatedUtc,
    int SnapshotCount);

/// <summary>工作流加载/导入结果（失败时给出可诊断的问题列表，不静默丢弃内容）。</summary>
public sealed class WorkflowLoadResult
{
    public WorkflowLoadResult(WorkflowDefinition? definition, IReadOnlyList<WorkflowIssue>? issues = null)
    {
        Definition = definition;
        Issues = issues ?? Array.Empty<WorkflowIssue>();
    }

    /// <summary>加载出的定义（可能带问题，例如包含未知节点类型）。</summary>
    public WorkflowDefinition? Definition { get; }

    /// <summary>问题列表。</summary>
    public IReadOnlyList<WorkflowIssue> Issues { get; }

    /// <summary>是否存在阻止使用的错误。</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == WorkflowIssueSeverity.Error);
}

/// <summary>工作流定义存储（版本化 JSON 文件；原子写入；导入导出带校验与防护）。</summary>
public interface IWorkflowDefinitionStore
{
    /// <summary>列出全部工作流摘要。</summary>
    Task<IReadOnlyList<WorkflowDefinitionSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>加载工作流定义。</summary>
    Task<WorkflowLoadResult> LoadAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存工作流（原子写入）。提供 <paramref name="snapshotLabel"/> 时同时创建不可变版本快照。
    /// </summary>
    Task SaveAsync(
        WorkflowDefinition definition,
        string? snapshotLabel = null,
        string? snapshotNote = null,
        CancellationToken cancellationToken = default);

    /// <summary>删除工作流定义（不删除快照与运行历史）。</summary>
    Task<bool> DeleteAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>列出某工作流的全部版本快照（按时间倒序）。</summary>
    Task<IReadOnlyList<WorkflowVersionSnapshot>> ListSnapshotsAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>加载指定快照。</summary>
    Task<WorkflowVersionSnapshot?> LoadSnapshotAsync(string workflowId, string snapshotId, CancellationToken cancellationToken = default);

    /// <summary>从文件导入（只读取为数据，不执行、不落库；由调用方决定是否保存）。</summary>
    Task<WorkflowLoadResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);

    /// <summary>导出定义到目标文件（原子写入）。</summary>
    Task ExportAsync(WorkflowDefinition definition, string destinationPath, CancellationToken cancellationToken = default);
}
