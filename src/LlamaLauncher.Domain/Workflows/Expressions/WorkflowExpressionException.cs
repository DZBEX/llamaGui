namespace LlamaLauncher.Domain.Workflows.Expressions;

/// <summary>表达式语法或求值错误（带位置信息，便于属性面板定位）。</summary>
public sealed class WorkflowExpressionException : Exception
{
    public WorkflowExpressionException(string message, int position = -1, bool isRuntime = false)
        : base(message)
    {
        Position = position;
        IsRuntime = isRuntime;
    }

    /// <summary>错误在表达式文本中的位置（-1 表示未知）。</summary>
    public int Position { get; }

    /// <summary>是否为运行期错误（语法错误为 false）。</summary>
    public bool IsRuntime { get; }

    /// <summary>面向用户的描述（含位置）。</summary>
    public string ToUserMessage()
        => Position >= 0 ? $"{Message}（位置 {Position + 1}）" : Message;
}
