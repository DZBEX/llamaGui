namespace LlamaLauncher.Application.Abstractions;

/// <summary>端口预检查结果。</summary>
public sealed record PortCheckResult(bool IsAvailable, string? Detail)
{
    public static PortCheckResult Available() => new(true, null);

    public static PortCheckResult InUse(string? detail) => new(false, detail);
}

/// <summary>端口占用预检查（仅做绑定测试，不抢占他人端口）。</summary>
public interface IPortProbeService
{
    Task<PortCheckResult> CheckAsync(string host, int port, CancellationToken cancellationToken = default);
}
