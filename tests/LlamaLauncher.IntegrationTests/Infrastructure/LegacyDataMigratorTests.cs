using System.Text.Json;
using LlamaLauncher.Infrastructure;
using LlamaLauncher.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Infrastructure;

/// <summary>旧数据位置（%AppData% 或旧便携目录）→ 程序根目录的迁移与路径改写。</summary>
public sealed class LegacyDataMigratorTests
{
    [Fact]
    public async Task Migrate_MovesEverythingAndRewritesPaths()
    {
        var source = CreateTempDirectory("legacy");
        var destination = CreateTempDirectory("program");
        try
        {
            var oldExe = Path.Combine(source, "runtimes", "llama.cpp", "b10618", "llama-server.exe");
            var oldLog = Path.Combine(source, "logs", "inst.log");

            // 1) 源数据库（含旧路径）
            var sourceDataDir = Path.Combine(source, "data");
            Directory.CreateDirectory(sourceDataDir);
            var sourceDbPath = Path.Combine(sourceDataDir, "llamalauncher.db");
            var database = new SqliteDatabase(sourceDbPath, NullLogger<SqliteDatabase>.Instance);
            await database.InitializeAsync();
            InsertRows(sourceDbPath, oldExe, oldLog);

            // 2) 运行时、日志与安装清单
            Directory.CreateDirectory(Path.GetDirectoryName(oldExe)!);
            File.WriteAllText(oldExe, "dummy-exe");
            Directory.CreateDirectory(Path.Combine(source, "logs"));
            File.WriteAllText(oldLog, "log line");
            var manifestDirectory = Path.Combine(source, "manifests");
            Directory.CreateDirectory(manifestDirectory);
            var manifestPath = Path.Combine(manifestDirectory, "installations.json");
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        entries = new[]
                        {
                            new
                            {
                                id = Guid.NewGuid(),
                                versionLabel = "b10618",
                                directory = Path.GetDirectoryName(oldExe),
                                executablePath = oldExe,
                                sha256 = (string?)null,
                            },
                        },
                    },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

            // Act
            SqliteConnection.ClearAllPools();
            var report = LegacyDataMigrator.Migrate(source, destination);

            // Assert：整体移动
            Assert.True(report.Migrated);
            Assert.True(report.Issues.Count == 0, string.Join(Environment.NewLine, report.Issues));
            Assert.False(Directory.Exists(sourceDataDir));
            Assert.False(File.Exists(oldExe));

            var newDatabasePath = Path.Combine(destination, "data", "llamalauncher.db");
            var newExe = Path.Combine(destination, "runtimes", "llama.cpp", "b10618", "llama-server.exe");
            var newLog = Path.Combine(destination, "logs", "inst.log");
            Assert.True(File.Exists(newDatabasePath));
            Assert.True(File.Exists(newExe));
            Assert.True(File.Exists(newLog));

            // Assert：数据库路径前缀已改写
            using (var connection = Open(newDatabasePath))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT ExecutablePath, Directory FROM LlamaCppInstallations;";
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(newExe, reader.GetString(0));
                Assert.Equal(Path.GetDirectoryName(newExe), reader.GetString(1));
            }

            using (var connection = Open(newDatabasePath))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT LogFilePath, CommandLineMasked FROM StartupHistory;";
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(newLog, reader.GetString(0));
                Assert.StartsWith(newExe, reader.GetString(1), StringComparison.OrdinalIgnoreCase);
            }

            // Assert：安装清单已改写（JSON 中反斜杠成对转义）
            var manifestJson = File.ReadAllText(Path.Combine(destination, "manifests", "installations.json"));
            Assert.Contains(EscapeForJson(newExe), manifestJson);
            Assert.DoesNotContain(EscapeForJson(source), manifestJson);
        }
        finally
        {
            TryDelete(source);
            TryDelete(destination);
        }
    }

    [Fact]
    public void MigrateIfNeeded_Skips_WhenDestinationHasData()
    {
        var programRoot = CreateTempDirectory("program");
        var legacy = CreateTempDirectory("legacy");
        var original = Environment.GetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, null);

            // 源有旧数据、目标已有数据库 → 不迁移
            Directory.CreateDirectory(Path.Combine(legacy, "data"));
            File.WriteAllText(Path.Combine(legacy, "data", "llamalauncher.db"), "x");
            Directory.CreateDirectory(Path.Combine(programRoot, "data"));
            File.WriteAllText(Path.Combine(programRoot, "data", "llamalauncher.db"), "y");

            Assert.Null(LegacyDataMigrator.MigrateIfNeeded(programRoot));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable, original);
            TryDelete(programRoot);
            TryDelete(legacy);
        }
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        return connection;
    }

    private static void InsertRows(string databasePath, string executablePath, string logPath)
    {
        using var connection = Open(databasePath);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO LlamaCppInstallations(Id, VersionLabel, SourceKind, Directory, ExecutablePath, Status, InstalledUtc)
                VALUES ($id, 'b10618', 0, $dir, $exe, 0, $utc);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$dir", Path.GetDirectoryName(executablePath)!);
            command.Parameters.AddWithValue("$exe", executablePath);
            command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("o"));
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO StartupHistory(Id, InstanceId, CommandLineMasked, StartedUtc, FinalState, LogFilePath)
                VALUES ($id, $iid, $cmd, $utc, 1, $log);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$iid", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$cmd", executablePath + " --host 127.0.0.1");
            command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("o"));
            command.Parameters.AddWithValue("$log", logPath);
            command.ExecuteNonQuery();
        }
    }

    private static string EscapeForJson(string path) => path.Replace("\\", "\\\\");

    private static string CreateTempDirectory(string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), "LlamaLauncherMigration", tag, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }
}
