namespace LlamaLauncher.Application.Abstractions;

/// <summary>备份条目。</summary>
public sealed record BackupFileInfo(string FilePath, DateTimeOffset CreatedUtc, long SizeBytes);

/// <summary>恢复结果。</summary>
public sealed record RestoreResult(bool Success, string Message, string? PreRestoreBackupPath);

/// <summary>SQLite 在线备份与恢复（恢复前自动备份当前数据库；实现位于 Infrastructure）。</summary>
public interface IBackupRestoreService
{
    Task<string> CreateBackupAsync(string? destinationPath = null, CancellationToken cancellationToken = default);

    IReadOnlyList<BackupFileInfo> ListBackups();

    /// <summary>从备份恢复；成功后需要重启应用生效。</summary>
    Task<RestoreResult> RestoreAsync(string backupPath, CancellationToken cancellationToken = default);
}
