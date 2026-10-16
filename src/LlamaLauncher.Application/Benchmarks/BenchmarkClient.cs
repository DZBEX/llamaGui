namespace LlamaLauncher.Application.Benchmarks;

/// <summary>一次基准调用的请求（针对 llama-server 的 /completion 端点）。</summary>
public sealed record BenchmarkCallRequest(
    string BaseUrl,
    string? ApiKey,
    string Prompt,
    int GenerateTokens,
    TimeSpan Timeout);

/// <summary>一次基准调用的结果；失败时 <see cref="Error"/> 非空，其余指标为尽力采集值。</summary>
public sealed record BenchmarkCallResult(
    bool Success,
    int? PromptTokens,
    double? PromptMs,
    double? PromptTokensPerSecond,
    int? GeneratedTokens,
    double? GeneratedMs,
    double? GeneratedTokensPerSecond,
    double? TimeToFirstTokenMs,
    double? TotalMs,
    string? Error)
{
    /// <summary>构造失败结果。</summary>
    public static BenchmarkCallResult Fail(string error, double? totalMs = null, double? timeToFirstTokenMs = null)
        => new(false, null, null, null, null, null, null, timeToFirstTokenMs, totalMs, error);
}

/// <summary>
/// 基准调用客户端：对目标服务执行一次“固定提示 + 固定生成长度”的请求，
/// 返回服务端 timings（若提供）与客户端实测的首 token / 总耗时。
/// </summary>
public interface IBenchmarkClient
{
    Task<BenchmarkCallResult> RunOnceAsync(
        BenchmarkCallRequest request,
        CancellationToken cancellationToken = default);
}
