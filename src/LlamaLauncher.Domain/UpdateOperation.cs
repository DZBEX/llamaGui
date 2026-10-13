namespace LlamaLauncher.Domain;

/// <summary>一次更新相关操作（检查/下载/安装/切换/回滚/清理）的审计记录。</summary>
public sealed class UpdateOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public UpdateOperationKind Kind { get; set; }

    public string? FromVersion { get; set; }

    public string? ToVersion { get; set; }

    public string? AssetName { get; set; }

    public string? AssetUrl { get; set; }

    public long? AssetSizeBytes { get; set; }

    public UpdateStage Stage { get; set; } = UpdateStage.Checking;

    public string? Error { get; set; }

    public string? Sha256 { get; set; }

    public bool Sha256Verified { get; set; }

    /// <summary>校验值是否来自官方 release 元数据（false 表示仅为本地指纹）。</summary>
    public bool Sha256FromOfficialDigest { get; set; }

    public bool RolledBack { get; set; }

    public string? StagingDirectory { get; set; }

    public string? InstalledDirectory { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset? CompletedUtc { get; set; }
}
