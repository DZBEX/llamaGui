using LlamaLauncher.Application.Models;
using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Abstractions;

/// <summary>GGUF 元数据读取（实现位于 Infrastructure，仅读取文件头与元数据区）。</summary>
public interface IGgufMetadataReader
{
    Task<GgufReadResult> ReadAsync(string filePath, CancellationToken cancellationToken = default);
}

/// <summary>模型库服务：登记、扫描、校验与元数据维护。</summary>
public interface IModelCatalogService
{
    Task<IReadOnlyList<ModelRecord>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ModelRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>登记单个模型文件；已登记则抛出 <see cref="ModelCatalogException"/>。</summary>
    Task<ModelRecord> AddAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>登记（如已登记则直接返回现有记录）。</summary>
    Task<ModelRecord> EnsureRegisteredAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>扫描目录（顶层；可选子目录），支持取消与进度。</summary>
    Task<IReadOnlyList<ModelRecord>> ScanDirectoryAsync(
        string directory,
        bool includeSubdirectories,
        IProgress<ModelScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>重新检查文件可用性并刷新元数据。</summary>
    Task<ModelRecord> RevalidateAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelRecord>> RevalidateAllAsync(CancellationToken cancellationToken = default);

    /// <summary>移除登记（不删除文件）。</summary>
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpdateDetailsAsync(
        Guid id,
        string? alias,
        IReadOnlyList<string>? tags,
        string? notes,
        CancellationToken cancellationToken = default);
}
