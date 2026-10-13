using LlamaLauncher.Application.Models;
using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Abstractions;

/// <summary>更新流程失败（面向用户的消息）。</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
    }
}

/// <summary>回滚结果。</summary>
public sealed record RollbackResult(bool Success, string Message, Guid? RestoredInstallationId);

/// <summary>CUDA 运行时辅助包（cudart）安装结果。</summary>
public sealed record RuntimePackInstallResult(string AssetName, int DllCount, string TargetDirectory);

/// <summary>
/// llama.cpp 程序版本管理：检查更新、下载校验、安全解压、staging 探测、注册安装、
/// 默认版本切换与回滚、清理。所有操作写入 UpdateOperations 审计记录。
/// </summary>
public interface ILlamaUpdateService
{
    /// <summary>检查更新（使用默认安装作为当前版本参照）。</summary>
    Task<UpdateCheckResult> CheckForUpdatesAsync(
        UpdateChannel channel,
        CancellationToken cancellationToken = default);

    /// <summary>下载并安装指定资产（独立版本目录 + 探测 + 注册）。</summary>
    Task<LlamaCppInstallation> DownloadAndInstallAsync(
        ReleaseInfo release,
        ReleaseAsset asset,
        IProgress<DownloadProgressInfo>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 为已安装的 CUDA 版本补装官方 cudart 运行时辅助包：
    /// 下载 → 校验 → 安全解压 → 仅把 DLL 复制到该版本的可执行文件目录（同一次审计记录 Kind=RuntimePack）。
    /// </summary>
    Task<RuntimePackInstallResult> InstallRuntimePackAsync(
        ReleaseInfo release,
        ReleaseAsset asset,
        LlamaCppInstallation installation,
        IProgress<DownloadProgressInfo>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>导入用户手动下载的官方/第三方压缩包（相同安全检查与探测流程）。</summary>
    Task<LlamaCppInstallation> ImportLocalAssetAsync(
        string zipPath,
        bool isOfficialSource,
        string? sourceNote,
        IProgress<DownloadProgressInfo>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>切换默认版本（记录旧默认以便回滚）。</summary>
    Task SetDefaultAsync(Guid installationId, CancellationToken cancellationToken = default);

    /// <summary>回滚到上一次默认版本。</summary>
    Task<RollbackResult> RollbackAsync(CancellationToken cancellationToken = default);

    /// <summary>删除旧版本（校验：非默认、未被配置引用、未被运行中实例使用）。</summary>
    Task DeleteInstallationAsync(Guid installationId, bool deleteFiles, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UpdateOperation>> GetHistoryAsync(int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>可直接打开官方发布页的 URL。</summary>
    string OfficialReleasesPageUrl { get; }
}
