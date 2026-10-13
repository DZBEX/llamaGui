using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Abstractions;

/// <summary>健康检查结果。</summary>
public sealed record HealthCheckResult(HealthStatusKind Status, string? Detail, int? HttpStatusCode);

/// <summary>服务模型信息（GET /v1/models 等）。</summary>
public sealed record ServerModelInfo(string? Id, string? Name, string? RawJson);

/// <summary>llama-server HTTP 健康检查与轻量 API 探测。</summary>
public interface IServerHealthService
{
    /// <summary>基本健康检查（GET /health），区分加载中/就绪/错误/不可达。</summary>
    Task<HealthCheckResult> CheckHealthAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken = default);

    /// <summary>获取服务模型列表（GET /v1/models），失败返回 null 与诊断。</summary>
    Task<(IReadOnlyList<ServerModelInfo>? Models, string? Error)> GetModelsAsync(
        string baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default);

    /// <summary>获取 /props 信息原始 JSON（部分版本无该端点）。</summary>
    Task<(string? RawJson, string? Error)> GetPropsAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken = default);

    /// <summary>轻量聊天连通性测试（可选的显式操作）。</summary>
    Task<(bool Success, string Message)> TestChatAsync(
        string baseUrl,
        string? apiKey,
        string modelId,
        CancellationToken cancellationToken = default);
}
