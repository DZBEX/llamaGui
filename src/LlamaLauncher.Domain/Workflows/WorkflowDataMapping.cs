namespace LlamaLauncher.Domain.Workflows;

/// <summary>节点输入字段的数据映射：来源表达式 → 目标字段。</summary>
public sealed class WorkflowDataMapping
{
    /// <summary>目标字段名（节点输入文档中的键）。</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>
    /// 来源表达式（受限安全表达式，例如 <c>input.message</c>、<c>up.node1.out.intent</c>、<c>vars.x</c>）。
    /// 为空表示仅使用默认值。
    /// </summary>
    public string? Source { get; set; }

    /// <summary>默认值（JSON 文本；来源为空或求值失败且未标记必填时使用）。</summary>
    public string? DefaultValueJson { get; set; }

    /// <summary>是否必填（必填且来源缺失时运行前报错）。</summary>
    public bool Required { get; set; }

    /// <summary>可选转换：number / string / boolean / json（为空表示不转换）。</summary>
    public string? Convert { get; set; }
}
