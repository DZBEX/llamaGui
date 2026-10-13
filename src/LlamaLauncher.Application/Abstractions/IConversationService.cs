using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Abstractions;

/// <summary>会话与消息管理。</summary>
public interface IConversationService
{
    Task<IReadOnlyList<Conversation>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Conversation?> GetAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<Conversation> CreateAsync(
        Guid? instanceId,
        string? modelName,
        string? systemPrompt,
        CancellationToken cancellationToken = default);

    Task RenameAsync(Guid conversationId, string title, CancellationToken cancellationToken = default);

    /// <summary>更新系统提示词与采样参数（不丢失已有消息）。</summary>
    Task UpdateSettingsAsync(
        Guid conversationId,
        string? systemPrompt,
        ChatParameters parameters,
        CancellationToken cancellationToken = default);

    /// <summary>清空当前会话的全部消息（保留会话本身）。</summary>
    Task ClearMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>删除单条消息（生成中的消息会被拒绝，需先停止生成）。</summary>
    Task DeleteMessageAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>删除会话及其全部消息。</summary>
    Task DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>应用启动恢复：把 Streaming/Pending 消息标记为 Interrupted。返回受影响数量。</summary>
    Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default);

    /// <summary>构造发送给服务的消息序列（系统提示词在前，跳过未完成/失败的空消息）。</summary>
    IReadOnlyList<ChatTurn> BuildTurns(Conversation conversation, IReadOnlyList<ChatMessage> messages);
}
