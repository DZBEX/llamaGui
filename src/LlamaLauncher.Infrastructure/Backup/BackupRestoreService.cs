using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Backup;

/// <summary>SQLite 备份恢复实现（校验、恢复前备份、失败自动回滚）。</summary>
public sealed class BackupRestoreService : IBackupRestoreService
{
    private readonly SqliteDatabase _database;
    private readonly IAppPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BackupRestoreService> _logger;

    public BackupRestoreService(
        SqliteDatabase database,
        IAppPaths paths,
        TimeProvider timeProvider,
        ILogger<BackupRestoreService> logger)
    {
        _database = database;
        _paths = paths;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<string> CreateBackupAsync(
        string? destinationPath = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.BackupsDirectory);
        var target = destinationPath
            ?? Path.Combine(
                _paths.BackupsDirectory,
                $"llamalauncher-{_timeProvider.GetUtcNow():yyyyMMdd-HHmmss}.db");
        await _database.BackupToAsync(target, cancellationToken).ConfigureAwait(false);
        return target;
    }

    public IReadOnlyList<BackupFileInfo> ListBackups()
    {
        try
        {
            if (!Directory.Exists(_paths.BackupsDirectory))
            {
                return Array.Empty<BackupFileInfo>();
            }

            return Directory
                .EnumerateFiles(_paths.BackupsDirectory, "*.db")
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return new BackupFileInfo(
                        path,
                        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                        info.Length);
                })
                .OrderByDescending(b => b.CreatedUtc)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举备份失败");
            return Array.Empty<BackupFileInfo>();
        }
    }

    public async Task<RestoreResult> RestoreAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupPath))
        {
            return new RestoreResult(false, $"备份文件不存在：{backupPath}", null);
        }

        var validation = await ValidateSqliteAsync(backupPath, cancellationToken).ConfigureAwait(false);
        if (validation is not null)
        {
            return new RestoreResult(false, "备份文件校验失败：" + validation, null);
        }

        string preRestoreBackup;
        try
        {
            preRestoreBackup = await CreateBackupAsync(
                Path.Combine(_paths.BackupsDirectory, $"pre-restore-{_timeProvider.GetUtcNow():yyyyMMdd-HHmmss}.db"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new RestoreResult(false, "无法创建恢复前的安全备份，已取消恢复：" + ex.Message, null);
        }

        try
        {
            SqliteConnection.ClearAllPools();
            var databasePath = _database.DatabasePath;
            foreach (var side in new[] { databasePath + "-wal", databasePath + "-shm" })
            {
                if (File.Exists(side))
                {
                    File.Delete(side);
                }
            }

            File.Copy(backupPath, databasePath, overwrite: true);
            SqliteConnection.ClearAllPools();

            var postValidation = await ValidateSqliteAsync(databasePath, cancellationToken).ConfigureAwait(false);
            if (postValidation is not null)
            {
                File.Copy(preRestoreBackup, databasePath, overwrite: true);
                SqliteConnection.ClearAllPools();
                return new RestoreResult(
                    false,
                    "恢复后的数据库校验失败，已自动回滚到恢复前状态：" + postValidation,
                    preRestoreBackup);
            }

            _logger.LogInformation("数据库已从 {Backup} 恢复", backupPath);
            return new RestoreResult(
                true,
                "恢复完成。请重启应用以加载恢复后的数据（当前进程仍使用旧数据）。",
                preRestoreBackup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复数据库失败");
            try
            {
                File.Copy(preRestoreBackup, _database.DatabasePath, overwrite: true);
                SqliteConnection.ClearAllPools();
            }
            catch (Exception rollbackError)
            {
                _logger.LogError(rollbackError, "恢复回滚失败");
            }

            return new RestoreResult(false, "恢复失败：" + ex.Message, preRestoreBackup);
        }
    }

    private static async Task<string?> ValidateSqliteAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var result = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) ? null : result;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
