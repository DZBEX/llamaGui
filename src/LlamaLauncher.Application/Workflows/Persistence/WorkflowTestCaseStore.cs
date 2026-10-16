using LlamaLauncher.Domain.Workflows;

namespace LlamaLauncher.Application.Workflows;

/// <summary>测试用例加载/导入结果。</summary>
public sealed class WorkflowTestCaseLoadResult
{
    public WorkflowTestCaseLoadResult(WorkflowTestCase? testCase, IReadOnlyList<WorkflowIssue>? issues = null)
    {
        TestCase = testCase;
        Issues = issues ?? Array.Empty<WorkflowIssue>();
    }

    /// <summary>用例（可能带问题）。</summary>
    public WorkflowTestCase? TestCase { get; }

    /// <summary>问题列表。</summary>
    public IReadOnlyList<WorkflowIssue> Issues { get; }

    /// <summary>是否存在阻止使用的错误。</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == WorkflowIssueSeverity.Error);
}

/// <summary>测试用例存储（JSON 文件；原子写入；导入导出）。</summary>
public interface IWorkflowTestCaseStore
{
    /// <summary>列出全部用例。</summary>
    Task<IReadOnlyList<WorkflowTestCase>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>按 ID 加载。</summary>
    Task<WorkflowTestCase?> LoadAsync(string testCaseId, CancellationToken cancellationToken = default);

    /// <summary>保存（新建或覆盖；原子写入）。</summary>
    Task SaveAsync(WorkflowTestCase testCase, CancellationToken cancellationToken = default);

    /// <summary>删除。</summary>
    Task<bool> DeleteAsync(string testCaseId, CancellationToken cancellationToken = default);

    /// <summary>从文件导入（只读取为数据）。</summary>
    Task<WorkflowTestCaseLoadResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);

    /// <summary>导出到文件（原子写入）。</summary>
    Task ExportAsync(WorkflowTestCase testCase, string destinationPath, CancellationToken cancellationToken = default);
}
