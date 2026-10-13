using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Application.Services;

/// <summary>会话与消息服务实现。</summary>
public sealed class ConversationService : IConversationService
{
    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(
        IConversationRepository conversations,
        IMessageRepository messages,
        TimeProvider timeProvider,
        ILogger<ConversationService> logger)
    {
        _conversations = conversations;
        _messages = messages;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task<IReadOnlyList<Conversation>> GetAllAsync(CancellationToken cancellationToken = default)
        => _conversations.GetAllAsync(cancellationToken);

    public Task<Conversation?> GetAsync(Guid conversationId, CancellationToken cancellationToken = default)
        => _conversations.GetByIdAsync(conversationId, cancellationToken);

    public async Task<Conversation> CreateAsync(
        Guid? instanceId,
        string? modelName,
        string? systemPrompt,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            Title = "新会话 " + now.ToLocalTime().ToString("MM-dd HH:mm"),
            InstanceId = instanceId,
            ModelNameSnapshot = modelName,
            SystemPrompt = systemPrompt,
            Parameters = ChatParameters.CreateDefaults(),
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        await _conversations.UpsertAsync(conversation, cancellationToken).ConfigureAwait(false);
        return conversation;
    }

    public async Task RenameAsync(Guid conversationId, string title, CancellationToken cancellationToken = default)
    {
        var conversation = await _conversations.GetByIdAsync(conversationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("会话不存在。");
        var trimmed = title.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("会话标题不能为空。");
        }

        conversation.Title = trimmed.Length > 120 ? trimmed[..120] : trimmed;
        conversation.UpdatedUtc = _timeProvider.GetUtcNow();
        await _conversations.UpsertAsync(conversation, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateSettingsAsync(
        Guid conversationId,
        string? systemPrompt,
        ChatParameters parameters,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _conversations.GetByIdAsync(conversationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("会话不存在。");
        conversation.SystemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt;
        conversation.Parameters = parameters ?? new ChatParameters();
        conversation.UpdatedUtc = _timeProvider.GetUtcNow();
        await _conversations.UpsertAsync(conversation, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await _messages.DeleteByConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var conversation = await _conversations.GetByIdAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is not null)
        {
            conversation.UpdatedUtc = _timeProvider.GetUtcNow();
            await _conversations.UpsertAsync(conversation, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteMessageAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
    {
        var message = await _messages.GetByIdAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (message is null || message.ConversationId != conversationId)
        {
            return;
        }

        if (message.Status == MessageStatus.Streaming)
        {
            throw new InvalidOperationException("正在生成的消息不能删除，请先停止生成。");
        }

        await _messages.DeleteAsync(messageId, cancellationToken).ConfigureAwait(false);
        var conversation = await _conversations.GetByIdAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is not null)
        {
            conversation.UpdatedUtc = _timeProvider.GetUtcNow();
            await _conversations.UpsertAsync(conversation, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default)
        => _conversations.DeleteAsync(conversationId, cancellationToken);

    public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
        => _messages.GetByConversationAsync(conversationId, cancellationToken);

    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        var affected = await _messages
            .MarkStreamingAsInterruptedAsync(_timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        if (affected > 0)
        {
            _logger.LogInformation("已将 {Count} 条未完成的流式消息标记为中断。", affected);
        }

        return affected;
    }

    public IReadOnlyList<ChatTurn> BuildTurns(Conversation conversation, IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(messages);

        var turns = new List<ChatTurn>();
        if (!string.IsNullOrWhiteSpace(conversation.SystemPrompt))
        {
            turns.Add(new ChatTurn(ChatRole.System, conversation.SystemPrompt!));
        }

        foreach (var message in messages.OrderBy(m => m.Sequence))
        {
            // 跳过未完成/失败且无内容的占位消息；保留 Interrupted 的已生成内容
            if (message.Status is MessageStatus.Pending)
            {
                continue;
            }

            if (message.Status is MessageStatus.Failed && string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            if (string.IsNullOrEmpty(message.Content))
            {
                continue;
            }

            if (message.Role is ChatRole.System)
            {
                continue; // 系统提示词以会话设置为准
            }

            turns.Add(new ChatTurn(message.Role, message.Content));
        }

        return turns;
    }
}
