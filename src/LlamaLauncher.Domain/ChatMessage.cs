namespace LlamaLauncher.Domain;

/// <summary>聊天消息（Markdown 原文持久化；渲染结果不作为数据源）。</summary>
public sealed class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }

    public ChatRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    public MessageStatus Status { get; set; } = MessageStatus.Completed;

    /// <summary>会话内顺序号（从 0 递增，排序稳定）。</summary>
    public long Sequence { get; set; }

    public ChatParameters? ParametersSnapshot { get; set; }

    public ChatMetrics? Metrics { get; set; }

    public string? ErrorText { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? CompletedUtc { get; set; }
}

/// <summary>生成统计（仅保存服务器真实返回或本地可测量值，未知保持 null）。</summary>
public sealed class ChatMetrics
{
    public int? PromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    public double? ElapsedMs { get; set; }

    public double? TokensPerSecond { get; set; }

    public string? InstanceNameSnapshot { get; set; }

    public string? ModelNameSnapshot { get; set; }
}
