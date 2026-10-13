using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Abstractions;

/// <summary>一次聊天上下文中的消息（角色 + 内容）。</summary>
public sealed record ChatTurn(ChatRole Role, string Content);

/// <summary>聊天错误分类信息。</summary>
public sealed record ChatError(
    ChatErrorKind Kind,
    string Message,
    int? HttpStatusCode = null,
    string? Detail = null);

/// <summary>聊天超时配置（连接超时由 HttpClient 处理器控制，此处为首 token 与整次响应时限）。</summary>
public sealed record ChatTimeouts(
    TimeSpan FirstToken,
    TimeSpan? Total)
{
    public static readonly ChatTimeouts Default = new(TimeSpan.FromMinutes(5), null);
}

/// <summary>流式事件。</summary>
public enum ChatStreamEventKind
{
    Delta = 0,
    Completed = 1,
    Error = 2,
}

/// <summary>流式事件载荷。</summary>
public sealed record ChatStreamEvent(
    ChatStreamEventKind Kind,
    string? Text = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    ChatError? Error = null);

/// <summary>聊天请求。</summary>
public sealed class ChatRequest
{
    public required string BaseUrl { get; init; }

    public string? ApiKey { get; init; }

    public string ModelId { get; init; } = "default";

    public required IReadOnlyList<ChatTurn> Messages { get; init; }

    public ChatParameters Parameters { get; init; } = new();

    public ChatTimeouts Timeouts { get; init; } = ChatTimeouts.Default;
}

/// <summary>流式/非流式聊天客户端（真实 HTTP + SSE）。</summary>
public interface IChatCompletionClient
{
    /// <summary>流式生成（真实 SSE 增量）；用户取消时抛出 <see cref="OperationCanceledException"/>。</summary>
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>非流式生成（用于轻量连通性测试与备用路径）。</summary>
    Task<(bool Success, string? Content, ChatError? Error)> CompleteAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default);
}
