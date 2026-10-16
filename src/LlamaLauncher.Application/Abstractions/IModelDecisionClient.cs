using System.Text.Json.Nodes;

namespace LlamaLauncher.Application.Abstractions;

/// <summary>决策模型调用错误分类。</summary>
public enum DecisionErrorKind
{
    /// <summary>无错误。</summary>
    None = 0,

    /// <summary>连接失败（服务未启动/端口不可达）。</summary>
    ConnectionFailed = 1,

    /// <summary>请求超时。</summary>
    Timeout = 2,

    /// <summary>HTTP 非成功状态。</summary>
    HttpError = 3,

    /// <summary>响应格式不符（不是有效 JSON / 缺少 answers）。</summary>
    InvalidResponse = 4,

    /// <summary>响应超过大小限制。</summary>
    ResponseTooLarge = 5,

    /// <summary>请求被取消。</summary>
    Canceled = 6,

    /// <summary>服务不支持该能力（例如旧版 llama.cpp 无 /v1/systemone）。</summary>
    Unsupported = 7,
}

/// <summary>决策模型调用错误。</summary>
public sealed record DecisionCallError(
    DecisionErrorKind Kind,
    string Message,
    int? HttpStatusCode = null,
    string? Detail = null);

/// <summary>/v1/systemone 请求。</summary>
public sealed class DecisionCallRequest
{
    /// <summary>服务基地址（如 http://127.0.0.1:8081）。</summary>
    public required string BaseUrl { get; init; }

    /// <summary>API Key（可空）。</summary>
    public string? ApiKey { get; init; }

    /// <summary>state（字符串、对象或数组）。</summary>
    public required JsonNode State { get; init; }

    /// <summary>questions 对象（TypeSafe System One 结构）。</summary>
    public required JsonObject Questions { get; init; }

    /// <summary>请求超时。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>响应大小上限（字节）。</summary>
    public int MaxResponseBytes { get; init; } = 1_048_576;
}

/// <summary>决策模型调用结果。</summary>
public sealed record DecisionCallResult(
    bool Success,
    JsonObject? Response,
    string? RawJson,
    DecisionCallError? Error,
    TimeSpan Elapsed)
{
    public static DecisionCallResult Ok(JsonObject response, string rawJson, TimeSpan elapsed)
        => new(true, response, rawJson, null, elapsed);

    public static DecisionCallResult Fail(DecisionCallError error, TimeSpan elapsed)
        => new(false, null, null, error, elapsed);
}

/// <summary>决策模型 HTTP 客户端（/v1/systemone；由基础设施实现，模块内不散落 HttpClient 代码）。</summary>
public interface IModelDecisionClient
{
    /// <summary>调用 /v1/systemone（非流式）。</summary>
    Task<DecisionCallResult> CallAsync(DecisionCallRequest request, CancellationToken cancellationToken = default);
}
