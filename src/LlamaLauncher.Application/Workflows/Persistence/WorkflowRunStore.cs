using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.Application.Workflows;

/// <summary>运行历史查询条件。</summary>
public sealed record WorkflowRunQuery(
    string? WorkflowId = null,
    WorkflowRunState? State = null,
    string? SearchText = null,
    int Limit = 200);

/// <summary>运行历史存储（SQLite；含不可变定义快照；支持保留策略清理）。</summary>
public interface IWorkflowRunStore
{
    /// <summary>追加一次运行记录。</summary>
    Task AppendAsync(WorkflowRunRecord record, CancellationToken cancellationToken = default);

    /// <summary>查询运行摘要列表（按开始时间倒序）。</summary>
    Task<IReadOnlyList<WorkflowRunSummary>> ListAsync(WorkflowRunQuery query, CancellationToken cancellationToken = default);

    /// <summary>加载完整运行记录。</summary>
    Task<WorkflowRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>删除一条运行记录。</summary>
    Task<bool> DeleteAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>按保留策略清理（保留最近 N 条），返回删除数量。</summary>
    Task<int> PruneAsync(int keepLatest, CancellationToken cancellationToken = default);

    /// <summary>运行记录总数。</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
