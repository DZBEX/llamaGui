using LlamaLauncher.IntegrationTests.TestSupport;
using LlamaLauncher.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Sqlite;

public sealed class MigrationTests
{
    [Fact]
    public async Task Initialize_CreatesSchemaAndMigrationRecord()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);

        await using var connection = await database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT MAX(Version) FROM schema_migrations), " +
            "(SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN " +
            "('Models','LaunchProfiles','LlamaCppInstallations','ServerInstances','StartupHistory'," +
            "'Conversations','ChatMessages','UpdateOperations','AppSettings','Secrets','WorkflowRuns'," +
            "'BenchmarkRuns'));";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        // 迁移版本随功能增量推进（v2：决策模型实验室运行历史；v3：性能基准运行记录）。
        Assert.Equal(SqliteDatabase.Migrations.Count, reader.GetInt32(0));
        Assert.Equal(12, reader.GetInt32(1));
    }

    [Fact]
    public async Task Initialize_IsIdempotent()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("data", "test.db");
        var first = new SqliteDatabase(path, NullLogger<SqliteDatabase>.Instance);
        await first.InitializeAsync();
        var second = new SqliteDatabase(path, NullLogger<SqliteDatabase>.Instance);
        await second.InitializeAsync();

        await using var connection = await second.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
        var count = Convert.ToInt64(await command.ExecuteScalarAsync());
        Assert.Equal(SqliteDatabase.Migrations.Count, count);
    }

    [Fact]
    public async Task Initialize_OnCorruptedFile_ThrowsFriendlyError()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("data", "broken.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "这不是一个 SQLite 数据库，而是一段普通文本。");

        var database = new SqliteDatabase(path, NullLogger<SqliteDatabase>.Instance);
        var exception = await Assert.ThrowsAsync<DatabaseInitializationException>(
            () => database.InitializeAsync());
        Assert.Contains("损坏", exception.Message);
    }

    [Fact]
    public async Task BackupTo_CreatesReadableCopy()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        await using (var connection = await database.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO AppSettings(Key, ValueJson, UpdatedUtc) VALUES('test.key', '\"v\"', '2026-01-01T00:00:00.0000000+00:00');";
            await command.ExecuteNonQueryAsync();
        }

        var backupPath = directory.Combine("backup", "copy.db");
        await database.BackupToAsync(backupPath);
        Assert.True(File.Exists(backupPath));

        var copy = new SqliteDatabase(backupPath, NullLogger<SqliteDatabase>.Instance);
        await using var copyConnection = await copy.OpenAsync();
        await using var read = copyConnection.CreateCommand();
        read.CommandText = "SELECT ValueJson FROM AppSettings WHERE Key='test.key';";
        Assert.Equal("\"v\"", (string?)await read.ExecuteScalarAsync());
    }
}
