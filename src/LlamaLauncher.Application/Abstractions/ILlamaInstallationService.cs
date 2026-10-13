using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Abstractions;

/// <summary>llama.cpp 安装管理失败（面向用户消息）。</summary>
public sealed class LlamaInstallationException : Exception
{
    public LlamaInstallationException(string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
    }
}

/// <summary>llama.cpp 安装记录管理：外部程序登记、能力探测刷新、默认版本切换、移除。</summary>
public interface ILlamaInstallationService
{
    Task<IReadOnlyList<LlamaCppInstallation>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<LlamaCppInstallation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LlamaCppInstallation?> GetDefaultAsync(CancellationToken cancellationToken = default);

    /// <summary>登记用户指定的外部 llama-server.exe（校验路径并执行能力探测）。</summary>
    Task<LlamaCppInstallation> RegisterExternalAsync(string executablePath, CancellationToken cancellationToken = default);

    /// <summary>重新探测并更新安装记录（版本标签、能力快照、后端标签、状态）。</summary>
    Task<LlamaCppInstallation> RefreshProbeAsync(Guid installationId, CancellationToken cancellationToken = default);

    Task SetDefaultAsync(Guid installationId, CancellationToken cancellationToken = default);

    /// <summary>移除安装记录（仍被配置档案引用时抛出 <see cref="LlamaInstallationException"/>；不删除磁盘文件）。</summary>
    Task RemoveAsync(Guid installationId, CancellationToken cancellationToken = default);
}
