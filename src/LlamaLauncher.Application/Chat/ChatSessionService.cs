using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Application.Chat;

/// <summary>一次发送的结果。</summary>
public sealed record ChatSendOutcome(bool Success, MessageStatus Status, string? Message, ChatError? Error);

/// <summary>聊天会话执行服务：编排流式请求与消息持久化、取消。</summary>
public interface IChatSessionService
{
    /// <summary>会话内容发生变化（增量持久化、完成、取消、失败）。</summary>
    event EventHandler<Guid>? ConversationUpdated;

    bool IsGenerating(Guid conversationId);

    Task<ChatSendOutcome> SendAsync(
        Guid conversationId,
        Guid instanceId,
        string userText,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 重新生成最后一条助手回复：删除最后一条用户消息之后的全部尾部消息，
    /// 在保留用户消息的前提下重新流式生成（失败/中断的回复也可用此重试）。
    /// </summary>
    Task<ChatSendOutcome> RegenerateAsync(
        Guid conversationId,
        Guid instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>取消当前生成（真实取消 HTTP 流）。</summary>
    void Cancel(Guid conversationId);

    /// <summary>轻量聊天连通性测试（独立于 /health）。</summary>
    Task<(bool Success, string Message)> TestConnectionAsync(Guid instanceId, CancellationToken cancellationToken = default);
}

/// <summary>聊天会话执行服务实现。</summary>
public sealed class ChatSessionService : IChatSessionService
{
    private static readonly TimeSpan PersistThrottle = TimeSpan.FromMilliseconds(350);

    private readonly IConversationService _conversations;
    private readonly IMessageRepository _messages;
    private readonly IChatCompletionClient _client;
    private readonly IInstanceManager _instances;
    private readonly IModelRepository _models;
    private readonly IServerHealthService _health;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChatSessionService> _logger;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeGenerations = new();

    public ChatSessionService(
        IConversationService conversations,
        IMessageRepository messages,
        IChatCompletionClient client,
        IInstanceManager instances,
        IModelRepository models,
        IServerHealthService health,
        TimeProvider timeProvider,
        ILogger<ChatSessionService> logger)
    {
        _conversations = conversations;
        _messages = messages;
        _client = client;
        _instances = instances;
        _models = models;
        _health = health;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public event EventHandler<Guid>? ConversationUpdated;

    public bool IsGenerating(Guid conversationId) => _activeGenerations.ContainsKey(conversationId);

    public async Task<ChatSendOutcome> SendAsync(
        Guid conversationId,
        Guid instanceId,
        string userText,
        CancellationToken cancellationToken = default)
    {
        var text = userText?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return new ChatSendOutcome(false, MessageStatus.Failed, "消息内容不能为空。", null);
        }

        var conversation = await _conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("会话不存在。");
        var instance = _instances.Get(instanceId)
            ?? throw new InvalidOperationException("实例不存在。");
        if (instance.State != InstanceState.Ready)
        {
            return new ChatSendOutcome(
                false,
                MessageStatus.Failed,
                $"实例“{instance.Name}”当前状态为 {instance.State}，服务未就绪，无法发送。",
                null);
        }

        if (IsGenerating(conversationId))
        {
            return new ChatSendOutcome(false, MessageStatus.Failed, "该会话正在生成中，请先停止或等待完成。", null);
        }

        var generationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_activeGenerations.TryAdd(conversationId, generationCts))
        {
            generationCts.Dispose();
            return new ChatSendOutcome(false, MessageStatus.Failed, "该会话正在生成中。", null);
        }

        try
        {
            var now = _timeProvider.GetUtcNow();

            // 1) 立即持久化用户消息
            var userMessage = new ChatMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                Role = ChatRole.User,
                Content = text,
                Status = MessageStatus.Completed,
                Sequence = await _messages.GetNextSequenceAsync(conversationId, CancellationToken.None).ConfigureAwait(false),
                CreatedUtc = now,
                CompletedUtc = now,
                ParametersSnapshot = conversation.Parameters,
            };
            await _messages.AddAsync(userMessage, CancellationToken.None).ConfigureAwait(false);

            // 2) 会话设置（标题首条消息自动命名）
            if (conversation.Title.StartsWith("新会话", StringComparison.Ordinal))
            {
                var autoTitle = text.Length <= 30 ? text : text[..30] + "…";
                await _conversations.RenameAsync(conversationId, autoTitle, CancellationToken.None)
                    .ConfigureAwait(false);
                conversation.Title = autoTitle;
            }

            conversation.InstanceId = instanceId;
            conversation.UpdatedUtc = _timeProvider.GetUtcNow();
            await _conversations.UpdateSettingsAsync(
                conversationId,
                conversation.SystemPrompt,
                conversation.Parameters,
                CancellationToken.None).ConfigureAwait(false);

            // 3) 构建消息序列并发送
            return await StreamAssistantReplyAsync(conversation, instance, conversationId, generationCts.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _activeGenerations.TryRemove(conversationId, out _);
            generationCts.Dispose();
        }
    }

    public async Task<ChatSendOutcome> RegenerateAsync(
        Guid conversationId,
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("会话不存在。");
        var instance = _instances.Get(instanceId)
            ?? throw new InvalidOperationException("实例不存在。");
        if (instance.State != InstanceState.Ready)
        {
            return new ChatSendOutcome(
                false,
                MessageStatus.Failed,
                $"实例“{instance.Name}”当前状态为 {instance.State}，服务未就绪，无法重新生成。",
                null);
        }

        if (IsGenerating(conversationId))
        {
            return new ChatSendOutcome(false, MessageStatus.Failed, "该会话正在生成中，请先停止或等待完成。", null);
        }

        var history = await _messages.GetByConversationAsync(conversationId, CancellationToken.None)
            .ConfigureAwait(false);
        var lastUser = history.LastOrDefault(m => m.Role == ChatRole.User);
        if (lastUser is null)
        {
            return new ChatSendOutcome(false, MessageStatus.Failed, "当前会话还没有用户消息，无法重新生成。", null);
        }

        var generationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_activeGenerations.TryAdd(conversationId, generationCts))
        {
            generationCts.Dispose();
            return new ChatSendOutcome(false, MessageStatus.Failed, "该会话正在生成中。", null);
        }

        try
        {
            // 删除最后一条用户消息之后的全部尾部消息（助手回复/失败占位），保留用户消息作为上下文
            foreach (var tail in history.Where(m => m.Sequence > lastUser.Sequence))
            {
                await _messages.DeleteAsync(tail.Id, CancellationToken.None).ConfigureAwait(false);
            }

            conversation.InstanceId = instanceId;
            conversation.UpdatedUtc = _timeProvider.GetUtcNow();
            await _conversations.UpdateSettingsAsync(
                conversationId,
                conversation.SystemPrompt,
                conversation.Parameters,
                CancellationToken.None).ConfigureAwait(false);
            RaiseUpdated(conversationId);

            return await StreamAssistantReplyAsync(conversation, instance, conversationId, generationCts.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _activeGenerations.TryRemove(conversationId, out _);
            generationCts.Dispose();
        }
    }

    /// <summary>创建助手占位消息并流式生成（发送与重新生成共用的核心流程）。</summary>
    private async Task<ChatSendOutcome> StreamAssistantReplyAsync(
        Conversation conversation,
        ManagedInstance instance,
        Guid conversationId,
        CancellationToken generationToken)
    {
        var assistant = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Role = ChatRole.Assistant,
            Content = string.Empty,
            Status = MessageStatus.Streaming,
            Sequence = await _messages.GetNextSequenceAsync(conversationId, CancellationToken.None).ConfigureAwait(false),
            CreatedUtc = _timeProvider.GetUtcNow(),
            ParametersSnapshot = conversation.Parameters,
            Metrics = new ChatMetrics
            {
                InstanceNameSnapshot = instance.Name,
                ModelNameSnapshot = conversation.ModelNameSnapshot,
            },
        };
        await _messages.AddAsync(assistant, CancellationToken.None).ConfigureAwait(false);
        RaiseUpdated(conversationId);

        var history = await _messages.GetByConversationAsync(conversationId, CancellationToken.None)
            .ConfigureAwait(false);
        var turns = _conversations.BuildTurns(conversation, history);
        var modelId = await ResolveModelIdAsync(instance, conversation).ConfigureAwait(false);
        var apiKey = await _instances.GetInstanceApiKeyAsync(instance.Id, CancellationToken.None).ConfigureAwait(false);

        var request = new ChatRequest
        {
            BaseUrl = instance.LocalBaseUrl,
            ApiKey = apiKey,
            ModelId = modelId,
            Messages = turns,
            Parameters = conversation.Parameters,
        };

        var stopwatch = Stopwatch.StartNew();
        var content = new StringBuilder();
        var lastPersistUtc = _timeProvider.GetUtcNow();
        int? promptTokens = null;
        int? completionTokens = null;
        var finalStatus = MessageStatus.Completed;
        string? errorText = null;
        ChatError? lastError = null;

        try
        {
            await foreach (var streamEvent in _client
                .StreamAsync(request, generationToken)
                .ConfigureAwait(false))
            {
                if (streamEvent.Kind == ChatStreamEventKind.Delta && streamEvent.Text is { } deltaText)
                {
                    content.Append(deltaText);
                    var nowUtc = _timeProvider.GetUtcNow();
                    if (nowUtc - lastPersistUtc >= PersistThrottle)
                    {
                        assistant.Content = content.ToString();
                        await _messages.UpdateAsync(assistant, CancellationToken.None).ConfigureAwait(false);
                        lastPersistUtc = nowUtc;
                        RaiseUpdated(conversationId);
                    }
                }
                else if (streamEvent.Kind == ChatStreamEventKind.Completed)
                {
                    promptTokens = streamEvent.PromptTokens ?? promptTokens;
                    completionTokens = streamEvent.CompletionTokens ?? completionTokens;
                }
                else if (streamEvent.Kind == ChatStreamEventKind.Error)
                {
                    lastError = streamEvent.Error;
                    finalStatus = content.Length > 0 && streamEvent.Error?.Kind is ChatErrorKind.Connection
                        or ChatErrorKind.Timeout
                        or ChatErrorKind.Protocol
                        ? MessageStatus.Interrupted
                        : MessageStatus.Failed;
                    errorText = streamEvent.Error?.Message;
                }
            }
        }
        catch (OperationCanceledException) when (generationToken.IsCancellationRequested)
        {
            finalStatus = MessageStatus.Interrupted;
            errorText = "已停止生成（用户取消）。";
        }
        catch (OperationCanceledException)
        {
            finalStatus = MessageStatus.Interrupted;
            errorText = "请求被取消。";
        }
        catch (Exception ex)
        {
            finalStatus = content.Length > 0 ? MessageStatus.Interrupted : MessageStatus.Failed;
            errorText = "生成过程中出现异常：" + ex.Message;
            _logger.LogError(ex, "会话 {ConversationId} 生成失败", conversationId);
        }

        stopwatch.Stop();
        assistant.Content = content.ToString();
        assistant.Status = finalStatus;
        assistant.ErrorText = finalStatus == MessageStatus.Completed ? null : errorText;
        assistant.CompletedUtc = _timeProvider.GetUtcNow();
        assistant.Metrics = new ChatMetrics
        {
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            ElapsedMs = stopwatch.Elapsed.TotalMilliseconds,
            TokensPerSecond = completionTokens is { } tokens && stopwatch.Elapsed.TotalSeconds > 0.01
                ? Math.Round(tokens / stopwatch.Elapsed.TotalSeconds, 1)
                : null,
            InstanceNameSnapshot = instance.Name,
            ModelNameSnapshot = conversation.ModelNameSnapshot,
        };
        await _messages.UpdateAsync(assistant, CancellationToken.None).ConfigureAwait(false);
        RaiseUpdated(conversationId);

        return finalStatus switch
        {
            MessageStatus.Completed => new ChatSendOutcome(true, finalStatus, null, null),
            MessageStatus.Interrupted => new ChatSendOutcome(false, finalStatus, errorText, lastError),
            _ => new ChatSendOutcome(false, finalStatus, errorText ?? lastError?.Message, lastError),
        };
    }

    public void Cancel(Guid conversationId)
    {
        if (_activeGenerations.TryGetValue(conversationId, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = _instances.Get(instanceId)
            ?? throw new InvalidOperationException("实例不存在。");
        if (instance.State != InstanceState.Ready)
        {
            return (false, $"实例当前状态为 {instance.State}，服务未就绪。");
        }

        var apiKey = await _instances.GetInstanceApiKeyAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var modelId = await ResolveModelIdAsync(instance, null).ConfigureAwait(false);
        return await _health.TestChatAsync(instance.LocalBaseUrl, apiKey, modelId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> ResolveModelIdAsync(ManagedInstance instance, Conversation? conversation)
    {
        var alias = instance.Profile?.Options.Alias;
        if (!string.IsNullOrWhiteSpace(alias))
        {
            return alias!;
        }

        if (instance.Record.ModelId is { } modelId)
        {
            var model = await _models.GetByIdAsync(modelId, CancellationToken.None).ConfigureAwait(false);
            if (model is not null)
            {
                return model.DisplayName;
            }
        }

        if (!string.IsNullOrWhiteSpace(conversation?.ModelNameSnapshot))
        {
            return conversation!.ModelNameSnapshot!;
        }

        return "local-model";
    }

    private void RaiseUpdated(Guid conversationId)
        => ConversationUpdated?.Invoke(this, conversationId);
}
