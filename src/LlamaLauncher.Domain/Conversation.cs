namespace LlamaLauncher.Domain;

/// <summary>聊天会话。</summary>
public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = "新会话";

    /// <summary>关联实例（可为空：历史会话在实例消失后仍保留）。</summary>
    public Guid? InstanceId { get; set; }

    public string? ModelNameSnapshot { get; set; }

    public string? SystemPrompt { get; set; }

    public ChatParameters Parameters { get; set; } = new();

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>聊天采样参数。</summary>
public sealed class ChatParameters
{
    public double? Temperature { get; set; }

    public double? TopP { get; set; }

    public int? TopK { get; set; }

    public double? MinP { get; set; }

    public double? RepeatPenalty { get; set; }

    public long? Seed { get; set; }

    /// <summary>-1 表示不限制（按服务器语义）。null 表示不显式发送。</summary>
    public int? MaxTokens { get; set; }

    public List<string> StopSequences { get; set; } = new();

    /// <summary>恢复“配置默认值”时使用的默认参数。</summary>
    public static ChatParameters CreateDefaults() => new()
    {
        Temperature = 0.8,
        TopP = 0.95,
    };
}
