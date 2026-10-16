using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Sqlite;

/// <summary>数据库初始化失败（数据库损坏、不可访问、迁移失败等）。</summary>
public sealed class DatabaseInitializationException : Exception
{
    public DatabaseInitializationException(string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
    }
}

/// <summary>SQLite 连接工厂与版本化迁移执行器。</summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteDatabase> _logger;

    public SqliteDatabase(string databasePath, ILogger<SqliteDatabase> logger)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
        }

        DatabasePath = Path.GetFullPath(databasePath);
        _logger = logger;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
        };
        _connectionString = builder.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>打开连接并设置连接级 PRAGMA。</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>创建数据库目录并执行迁移；可重复调用。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                await wal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var create = connection.CreateCommand())
            {
                create.CommandText =
                    "CREATE TABLE IF NOT EXISTS schema_migrations(" +
                    "Version INTEGER PRIMARY KEY, AppliedUtc TEXT NOT NULL);";
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            int currentVersion;
            await using (var query = connection.CreateCommand())
            {
                query.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM schema_migrations;";
                var scalar = await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                currentVersion = Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
            }

            foreach (var (version, sql) in Migrations)
            {
                if (version <= currentVersion)
                {
                    continue;
                }

                _logger.LogInformation("应用数据库迁移 v{Version}", version);
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                await using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = sql;
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var mark = connection.CreateCommand())
                {
                    mark.Transaction = transaction;
                    mark.CommandText =
                        "INSERT INTO schema_migrations(Version, AppliedUtc) VALUES ($v, $t);";
                    mark.Parameters.AddWithValue("$v", version);
                    mark.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                currentVersion = version;
            }
        }
        catch (SqliteException ex)
        {
            var message = ex.SqliteErrorCode switch
            {
                11 or 26 => "数据库文件损坏或不是有效的 SQLite 数据库。请从 backups 目录选择备份恢复，或重命名该文件后重启应用重新建库。",
                8 or 13 or 14 => "数据库文件无权限访问或路径不可写。请检查数据目录权限。",
                5 or 6 => "数据库被其他进程锁定。请关闭其他占用该数据库的实例后重试。",
                _ => $"数据库初始化失败：{ex.Message}",
            };
            throw new DatabaseInitializationException(message + $"（{DatabasePath}）", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DatabaseInitializationException($"数据库初始化失败：{ex.Message}", ex);
        }
    }

    /// <summary>执行一次在线备份（SQLite backup API，保证事务一致）。</summary>
    public async Task BackupToAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var source = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(destinationPath),
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
        _logger.LogInformation("数据库已备份到 {Path}", destinationPath);
    }

    /// <summary>schema 版本列表（顺序执行）。</summary>
    public static IReadOnlyList<(int Version, string Sql)> Migrations { get; } = new List<(int, string)>
    {
        (1, SchemaV1),
        (2, SchemaV2),
        (3, SchemaV3),
    };

    /// <summary>v3：模型性能基准——运行记录与样本明细。</summary>
    private const string SchemaV3 = """
        CREATE TABLE IF NOT EXISTS BenchmarkRuns(
          RunId TEXT PRIMARY KEY,
          InstanceId TEXT NOT NULL,
          InstanceNameSnapshot TEXT NOT NULL,
          ModelNameSnapshot TEXT NULL,
          ProfileNameSnapshot TEXT NULL,
          VersionLabel TEXT NULL,
          AppVersion TEXT NOT NULL,
          StartedUtc TEXT NOT NULL,
          EndedUtc TEXT NULL,
          DurationMs REAL NULL,
          State INTEGER NOT NULL,
          OptionsJson TEXT NOT NULL,
          SamplesJson TEXT NOT NULL DEFAULT '[]',
          ErrorSummary TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_BenchmarkRuns_Started ON BenchmarkRuns(StartedUtc DESC);
        CREATE INDEX IF NOT EXISTS IX_BenchmarkRuns_Instance ON BenchmarkRuns(InstanceId, StartedUtc DESC);
        """;

    /// <summary>v2：决策模型实验室——工作流运行历史。</summary>
    private const string SchemaV2 = """
        CREATE TABLE IF NOT EXISTS WorkflowRuns(
          RunId TEXT PRIMARY KEY,
          WorkflowId TEXT NOT NULL,
          WorkflowNameSnapshot TEXT NOT NULL,
          VersionLabel TEXT NULL,
          AppVersion TEXT NOT NULL,
          StartedUtc TEXT NOT NULL,
          EndedUtc TEXT NULL,
          DurationMs REAL NULL,
          State INTEGER NOT NULL,
          TestMode INTEGER NOT NULL,
          InputSnapshotJson TEXT NULL,
          OutputJson TEXT NULL,
          ErrorSummary TEXT NULL,
          ModelContextJson TEXT NULL,
          DefinitionSnapshotJson TEXT NULL,
          ValidationSummary TEXT NULL,
          FinalVariablesJson TEXT NULL,
          NodesJson TEXT NOT NULL DEFAULT '[]',
          LogsJson TEXT NOT NULL DEFAULT '[]'
        );
        CREATE INDEX IF NOT EXISTS IX_WorkflowRuns_Workflow ON WorkflowRuns(WorkflowId, StartedUtc DESC);
        CREATE INDEX IF NOT EXISTS IX_WorkflowRuns_Started ON WorkflowRuns(StartedUtc DESC);
        CREATE INDEX IF NOT EXISTS IX_WorkflowRuns_State ON WorkflowRuns(State);
        """;

    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS Models(
          Id TEXT PRIMARY KEY,
          Name TEXT NOT NULL,
          FilePath TEXT NOT NULL,
          CanonicalPath TEXT NOT NULL COLLATE NOCASE UNIQUE,
          FileSize INTEGER NULL,
          FileLastWriteUtc TEXT NULL,
          Status INTEGER NOT NULL,
          MetadataStatus INTEGER NOT NULL,
          MetadataJson TEXT NULL,
          MetadataError TEXT NULL,
          Alias TEXT NULL,
          TagsJson TEXT NULL,
          Notes TEXT NULL,
          DefaultProfileId TEXT NULL,
          CreatedUtc TEXT NOT NULL,
          UpdatedUtc TEXT NOT NULL,
          LastCheckedUtc TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Models_Name ON Models(Name);

        CREATE TABLE IF NOT EXISTS LaunchProfiles(
          Id TEXT PRIMARY KEY,
          Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
          ModelId TEXT NULL,
          ModelSnapshotJson TEXT NULL,
          InstallationId TEXT NULL,
          InstallationSnapshotJson TEXT NULL,
          OptionsJson TEXT NOT NULL,
          ExtraArguments TEXT NULL,
          AllowExtraArgsOverrideManaged INTEGER NOT NULL DEFAULT 0,
          EnvironmentJson TEXT NULL,
          IsDefault INTEGER NOT NULL DEFAULT 0,
          Notes TEXT NULL,
          CreatedUtc TEXT NOT NULL,
          UpdatedUtc TEXT NOT NULL,
          FOREIGN KEY(ModelId) REFERENCES Models(Id) ON DELETE SET NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Profiles_Model ON LaunchProfiles(ModelId);

        CREATE TABLE IF NOT EXISTS LlamaCppInstallations(
          Id TEXT PRIMARY KEY,
          VersionLabel TEXT NOT NULL,
          BuildInfo TEXT NULL,
          AssetName TEXT NULL,
          SourceUrl TEXT NULL,
          SourceKind INTEGER NOT NULL,
          Directory TEXT NOT NULL,
          ExecutablePath TEXT NOT NULL COLLATE NOCASE UNIQUE,
          Sha256 TEXT NULL,
          Sha256FromOfficialDigest INTEGER NOT NULL DEFAULT 0,
          ArchiveSize INTEGER NULL,
          Status INTEGER NOT NULL,
          IsDefault INTEGER NOT NULL DEFAULT 0,
          CapabilitiesJson TEXT NULL,
          LastProbedUtc TEXT NULL,
          InstalledUtc TEXT NOT NULL,
          Notes TEXT NULL,
          BackendTagsJson TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS ServerInstances(
          Id TEXT PRIMARY KEY,
          Name TEXT NOT NULL,
          ProfileId TEXT NULL,
          ProfileSnapshotJson TEXT NULL,
          ModelId TEXT NULL,
          InstallationId TEXT NULL,
          State INTEGER NOT NULL,
          Host TEXT NOT NULL,
          Port INTEGER NOT NULL,
          ProcessId INTEGER NULL,
          ProcessStartTimeUtc TEXT NULL,
          ExecutablePathSnapshot TEXT NULL,
          Generation INTEGER NOT NULL DEFAULT 0,
          StartedUtc TEXT NULL,
          StoppedUtc TEXT NULL,
          ExitCode INTEGER NULL,
          LastError TEXT NULL,
          LogFilePath TEXT NULL,
          CreatedUtc TEXT NOT NULL,
          UpdatedUtc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Instances_State ON ServerInstances(State);

        CREATE TABLE IF NOT EXISTS StartupHistory(
          Id TEXT PRIMARY KEY,
          InstanceId TEXT NOT NULL,
          InstanceNameSnapshot TEXT NULL,
          ProfileId TEXT NULL,
          ModelSnapshotJson TEXT NULL,
          InstallationSnapshotJson TEXT NULL,
          CommandLineMasked TEXT NULL,
          ProcessId INTEGER NULL,
          StartedUtc TEXT NOT NULL,
          EndedUtc TEXT NULL,
          ExitCode INTEGER NULL,
          FinalState INTEGER NOT NULL,
          FailureStage TEXT NULL,
          ErrorSummary TEXT NULL,
          LogFilePath TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_History_StartedAt ON StartupHistory(StartedUtc DESC);
        CREATE INDEX IF NOT EXISTS IX_History_Instance ON StartupHistory(InstanceId, StartedUtc DESC);

        CREATE TABLE IF NOT EXISTS Conversations(
          Id TEXT PRIMARY KEY,
          Title TEXT NOT NULL,
          InstanceId TEXT NULL,
          ModelNameSnapshot TEXT NULL,
          SystemPrompt TEXT NULL,
          ParametersJson TEXT NULL,
          CreatedUtc TEXT NOT NULL,
          UpdatedUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ChatMessages(
          Id TEXT PRIMARY KEY,
          ConversationId TEXT NOT NULL,
          Role INTEGER NOT NULL,
          Content TEXT NOT NULL,
          Status INTEGER NOT NULL,
          Sequence INTEGER NOT NULL,
          ParamsJson TEXT NULL,
          MetricsJson TEXT NULL,
          ErrorText TEXT NULL,
          CreatedUtc TEXT NOT NULL,
          CompletedUtc TEXT NULL,
          FOREIGN KEY(ConversationId) REFERENCES Conversations(Id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS IX_Messages_Conversation ON ChatMessages(ConversationId, Sequence);
        CREATE INDEX IF NOT EXISTS IX_Messages_Status ON ChatMessages(Status);

        CREATE TABLE IF NOT EXISTS UpdateOperations(
          Id TEXT PRIMARY KEY,
          Kind INTEGER NOT NULL,
          FromVersion TEXT NULL,
          ToVersion TEXT NULL,
          AssetName TEXT NULL,
          AssetUrl TEXT NULL,
          AssetSize INTEGER NULL,
          Stage INTEGER NOT NULL,
          Error TEXT NULL,
          Sha256 TEXT NULL,
          Sha256Verified INTEGER NOT NULL DEFAULT 0,
          Sha256FromOfficialDigest INTEGER NOT NULL DEFAULT 0,
          RolledBack INTEGER NOT NULL DEFAULT 0,
          StagingDirectory TEXT NULL,
          InstalledDirectory TEXT NULL,
          StartedUtc TEXT NOT NULL,
          CompletedUtc TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_UpdateOps_StartedAt ON UpdateOperations(StartedUtc DESC);

        CREATE TABLE IF NOT EXISTS AppSettings(
          Key TEXT PRIMARY KEY,
          ValueJson TEXT NOT NULL,
          UpdatedUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Secrets(
          Name TEXT PRIMARY KEY,
          ProtectedBlob BLOB NOT NULL,
          UpdatedUtc TEXT NOT NULL
        );
        """;
}
