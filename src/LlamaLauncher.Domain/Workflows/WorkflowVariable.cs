namespace LlamaLauncher.Domain.Workflows;

/// <summary>工作流输入/输出变量定义。</summary>
public sealed class WorkflowVariable
{
    /// <summary>变量名（工作流内唯一，映射与表达式引用用）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>数据类型。</summary>
    public WorkflowValueType Type { get; set; } = WorkflowValueType.Any;

    /// <summary>是否必填。</summary>
    public bool Required { get; set; }

    /// <summary>默认值（JSON 文本；null 表示没有默认值）。</summary>
    public string? DefaultValueJson { get; set; }

    /// <summary>说明。</summary>
    public string? Description { get; set; }

    /// <summary>示例值（JSON 文本）。</summary>
    public string? ExampleJson { get; set; }
}
