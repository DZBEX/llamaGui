using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Abstractions;

/// <summary>llama-server.exe 能力探测服务（--version / --help / --list-devices，带缓存）。</summary>
public interface ILlamaBinaryProbeService
{
    /// <summary>执行完整探测。同一可执行文件（路径 + 文件指纹）结果会被缓存。</summary>
    Task<ServerCapabilities> ProbeAsync(string executablePath, bool forceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>若缓存中存在该可执行文件的能力快照则返回（不触发探测）。</summary>
    ServerCapabilities? TryGetCached(string executablePath);
}
