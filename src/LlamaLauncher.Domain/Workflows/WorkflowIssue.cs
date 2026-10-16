namespace LlamaLauncher.Domain.Workflows;

/// <summary>工作流校验问题（设计时与运行时共用）。</summary>
public sealed class WorkflowIssue
{
    public WorkflowIssue()
    {
    }

    public WorkflowIssue(
        WorkflowIssueSeverity severity,
        string code,
        string message,
        string? nodeId = null,
        string? edgeId = null,
        string? hint = null)
    {
        Severity = severity;
        Code = code;
        Message = message;
        NodeId = nodeId;
        EdgeId = edgeId;
        Hint = hint;
    }

    /// <summary>严重级别。</summary>
    public WorkflowIssueSeverity Severity { get; set; }

    /// <summary>稳定的问题代码（用于测试与文档，如 WF-VAL-001）。</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>问题说明（面向用户，说明原因与影响）。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>关联节点（可空，UI 双击定位）。</summary>
    public string? NodeId { get; set; }

    /// <summary>关联连线（可空）。</summary>
    public string? EdgeId { get; set; }

    /// <summary>建议操作（可空）。</summary>
    public string? Hint { get; set; }
}

/// <summary>工作流校验报告。</summary>
public sealed class WorkflowValidationReport
{
    public WorkflowValidationReport()
    {
    }

    public WorkflowValidationReport(IEnumerable<WorkflowIssue> issues)
        => Issues = issues.ToList();

    /// <summary>全部问题（含错误、警告与信息）。</summary>
    public List<WorkflowIssue> Issues { get; set; } = new();

    /// <summary>是否存在阻止运行/保存的错误。</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == WorkflowIssueSeverity.Error);

    /// <summary>错误数量。</summary>
    public int ErrorCount => Issues.Count(i => i.Severity == WorkflowIssueSeverity.Error);

    /// <summary>警告数量。</summary>
    public int WarningCount => Issues.Count(i => i.Severity == WorkflowIssueSeverity.Warning);

    /// <summary>按严重级别排序的问题列表（错误 → 警告 → 信息）。</summary>
    public IReadOnlyList<WorkflowIssue> OrderedIssues => Issues
        .OrderBy(i => i.Severity)
        .ThenBy(i => i.Code, StringComparer.Ordinal)
        .ToList();

    /// <summary>单行摘要文本。</summary>
    public string Summary => HasErrors
        ? $"校验未通过：{ErrorCount} 个错误、{WarningCount} 个警告。"
        : WarningCount > 0
            ? $"校验通过：{WarningCount} 个警告。"
            : "校验通过。";
}
