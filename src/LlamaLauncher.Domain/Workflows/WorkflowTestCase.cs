using System.Text.Json.Nodes;

namespace LlamaLauncher.Domain.Workflows;

/// <summary>断言运算符。</summary>
public enum AssertionOperator
{
    Equals = 0,
    NotEquals = 1,
    Contains = 2,
    NotContains = 3,
    GreaterThan = 4,
    LessThan = 5,
    GreaterThanOrEqual = 6,
    LessThanOrEqual = 7,
    Exists = 8,
    NotExists = 9,
    IsTrue = 10,
    IsFalse = 11,
    RegexMatch = 12,
    ApproxEquals = 13,
    JsonEquals = 14,
}

/// <summary>单条断言：对目标表达式结果进行比较（含容差与忽略规则）。</summary>
public sealed class WorkflowAssertion
{
    /// <summary>断言 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>名称/说明。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>目标表达式（相对运行结果作用域，如 output.result.route 或 vars.decision）。</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>运算符。</summary>
    public AssertionOperator Operator { get; set; } = AssertionOperator.Equals;

    /// <summary>期望值（JSON 文本；Exists/IsTrue 等可空）。</summary>
    public string? ExpectedJson { get; set; }

    /// <summary>数值容差（ApproxEquals）。</summary>
    public double? Tolerance { get; set; }

    /// <summary>失败级别：Error 使用例失败；Warning 只提示。</summary>
    public WorkflowIssueSeverity Severity { get; set; } = WorkflowIssueSeverity.Error;
}

/// <summary>模拟模式下单个节点的固定响应。</summary>
public sealed class WorkflowMockResponse
{
    /// <summary>输出值（写入节点 out 端口）。</summary>
    public JsonNode? Output { get; set; }

    /// <summary>选中的分支（可空）。</summary>
    public string? BranchId { get; set; }

    /// <summary>模拟失败信息（设置后节点按失败处理）。</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>测试用例（单场景输入 + 预期结果/断言 + 模拟响应）。</summary>
public sealed class WorkflowTestCase
{
    /// <summary>用例 schema 版本。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>用例 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>说明。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>标签。</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>绑定的工作流 ID。</summary>
    public string WorkflowId { get; set; } = string.Empty;

    /// <summary>绑定的工作流版本快照标签（可空表示使用当前定义）。</summary>
    public string? WorkflowVersionLabel { get; set; }

    /// <summary>测试模式（模拟 / 本地模型）。</summary>
    public WorkflowTestMode Mode { get; set; } = WorkflowTestMode.Simulated;

    /// <summary>输入变量（键 → JSON 值）。</summary>
    public JsonObject? Inputs { get; set; }

    /// <summary>模拟响应（节点 ID → 固定响应）。</summary>
    public Dictionary<string, WorkflowMockResponse> MockResponses { get; set; } = new();

    /// <summary>断言列表。</summary>
    public List<WorkflowAssertion> Assertions { get; set; } = new();

    /// <summary>差异比较时忽略/规范化的字段路径。</summary>
    public List<string> IgnoreFields { get; set; } = new();

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>更新时间。</summary>
    public DateTimeOffset UpdatedUtc { get; set; }
}
