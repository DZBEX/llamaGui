using System.Text.Json;
using LlamaLauncher.Infrastructure;
using LlamaLauncher.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Infrastructure;

/// <summary>整包搬到另一台电脑/另一个目录后的数据库、清单与 JSON 路径适配。</summary>
public sealed class PortableDataRelocationServiceTests
{
    [Fact]
    public async Task Rebase_InfersOldRootWithoutMarker_AndRewritesAllPathStorage()
    {
        var root = CreateTempDirectory("new-program");
        try
        {
            var oldRoot = @"C:\OldComputer\Apps\LlamaLauncher";
            var oldRuntime = Path.Combine(oldRoot, "runtimes", "llama.cpp", "b11703-e5afe10e", "llama-server.exe");
            var newRuntime = Path.Combine(root, "runtimes", "llama.cpp", "b11703-e5afe10e", "llama-server.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(newRuntime)!);
            await File.WriteAllTextAsync(newRuntime, "dummy");
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            await File.WriteAllTextAsync(Path.Combine(root, "logs", "instance.log"), "log");

            var databasePath = await CreateDatabaseAsync(root);
            var installationId = Guid.NewGuid();
            var modelId = Guid.NewGuid();
            var profileId = Guid.NewGuid();
            var instanceId = Guid.NewGuid();

            await InsertRowsAsync(databasePath, oldRoot, oldRuntime, installationId, modelId, profileId, instanceId);

            var manifestPath = Path.Combine(root, "manifests", "installations.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    entries = new[]
                    {
                        new
                        {
                            id = installationId,
                            versionLabel = "b11703",
                            directory = Path.GetDirectoryName(oldRuntime),
                            executablePath = oldRuntime,
                        },
                    },
                }));

            var paths = new AppPaths(root);
            var service = new PortableDataRelocationService(
                paths,
                NullLogger<PortableDataRelocationService>.Instance);

            var report = service.RebaseIfNeeded();

            Assert.NotNull(report);
            Assert.True(report!.Changed);
            Assert.Contains(oldRoot, report.OldRoots, StringComparer.OrdinalIgnoreCase);

            await using var connection = Open(databasePath);
            Assert.Equal(newRuntime, Scalar(connection, "SELECT ExecutablePath FROM LlamaCppInstallations;"));
            Assert.Equal(
                Path.Combine(root, "models", "model.gguf"),
                Scalar(connection, "SELECT CanonicalPath FROM Models;"));
            Assert.Equal(
                newRuntime,
                Scalar(connection, "SELECT ExecutablePathSnapshot FROM ServerInstances;"));
            Assert.Equal(
                Path.Combine(root, "logs", "instance.log"),
                Scalar(connection, "SELECT LogFilePath FROM ServerInstances;"));

            var optionsJson = Scalar(connection, "SELECT OptionsJson FROM LaunchProfiles;");
            var snapshotJson = Scalar(connection, "SELECT InstallationSnapshotJson FROM LaunchProfiles;");
            Assert.DoesNotContain(oldRoot, optionsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(oldRoot, snapshotJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                Path.Combine(root, "models", "model.gguf").Replace("\\", "\\\\"),
                optionsJson,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                newRuntime.Replace("\\", "\\\\"),
                snapshotJson,
                StringComparison.OrdinalIgnoreCase);

            var manifest = await File.ReadAllTextAsync(manifestPath);
            Assert.DoesNotContain(oldRoot, manifest, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(newRuntime.Replace("\\", "\\\\"), manifest, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(root),
                (await File.ReadAllTextAsync(Path.Combine(root, "data", PortableDataRelocationService.MarkerFileName))).Trim());

            // 第二次运行不应重复改写，也不应产生额外变化。
            var second = service.RebaseIfNeeded();
            Assert.Same(report, second);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Rebase_MergesDuplicateInstallationRows_AndRepointsProfiles()
    {
        var root = CreateTempDirectory("new-program-merge");
        try
        {
            var oldRoot = @"D:\Old\LlamaLauncher";
            var relativeRuntime = Path.Combine("runtimes", "llama.cpp", "b11703-e5afe10e", "llama-server.exe");
            var oldRuntime = Path.Combine(oldRoot, relativeRuntime);
            var newRuntime = Path.Combine(root, relativeRuntime);
            Directory.CreateDirectory(Path.GetDirectoryName(newRuntime)!);
            await File.WriteAllTextAsync(newRuntime, "dummy");

            var databasePath = await CreateDatabaseAsync(root);
            var oldId = Guid.NewGuid();
            var newId = Guid.NewGuid();
            var profileId = Guid.NewGuid();

            await using (var connection = Open(databasePath))
            {
                await InsertInstallationAsync(connection, oldId, "b11703", oldRuntime, isDefault: true);
                await InsertInstallationAsync(connection, newId, "b11703", newRuntime, isDefault: false);

                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO LaunchProfiles(
                        Id, Name, InstallationId, InstallationSnapshotJson, OptionsJson,
                        AllowExtraArgsOverrideManaged, IsDefault, CreatedUtc, UpdatedUtc)
                    VALUES($id, '测试配置', $installationId, $snapshot, '{}', 0, 1, $utc, $utc);
                    """;
                command.Parameters.AddWithValue("$id", profileId.ToString("D"));
                command.Parameters.AddWithValue("$installationId", oldId.ToString("D"));
                command.Parameters.AddWithValue(
                    "$snapshot",
                    JsonSerializer.Serialize(new
                    {
                        installationId = oldId,
                        versionLabel = "b11703",
                        executablePath = oldRuntime,
                    }));
                command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            var service = new PortableDataRelocationService(
                new AppPaths(root),
                NullLogger<PortableDataRelocationService>.Instance);
            var report = service.RebaseIfNeeded();

            Assert.NotNull(report);
            await using var verify = Open(databasePath);
            Assert.Equal(1L, ScalarLong(verify, "SELECT COUNT(*) FROM LlamaCppInstallations;"));
            Assert.Equal(1L, ScalarLong(verify, "SELECT COUNT(*) FROM LlamaCppInstallations WHERE IsDefault = 1;"));
            Assert.Equal(
                newId.ToString("D"),
                Scalar(verify, "SELECT InstallationId FROM LaunchProfiles;"));
            Assert.Equal(newRuntime, Scalar(verify, "SELECT ExecutablePath FROM LlamaCppInstallations;"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<string> CreateDatabaseAsync(string root)
    {
        var dataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "llamalauncher.db");
        var database = new SqliteDatabase(databasePath, NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync();
        return databasePath;
    }

    private static async Task InsertRowsAsync(
        string databasePath,
        string oldRoot,
        string oldRuntime,
        Guid installationId,
        Guid modelId,
        Guid profileId,
        Guid instanceId)
    {
        var oldModel = Path.Combine(oldRoot, "models", "model.gguf");
        var oldLog = Path.Combine(oldRoot, "logs", "instance.log");
        var now = DateTimeOffset.UtcNow.ToString("O");

        await using var connection = Open(databasePath);
        await InsertInstallationAsync(connection, installationId, "b11703", oldRuntime, isDefault: true);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO Models(
                    Id, Name, FilePath, CanonicalPath, Status, MetadataStatus, CreatedUtc, UpdatedUtc)
                VALUES($id, 'model', $path, $path, 0, 0, $utc, $utc);
                """;
            command.Parameters.AddWithValue("$id", modelId.ToString("D"));
            command.Parameters.AddWithValue("$path", oldModel);
            command.Parameters.AddWithValue("$utc", now);
            await command.ExecuteNonQueryAsync();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO LaunchProfiles(
                    Id, Name, ModelId, ModelSnapshotJson, InstallationId, InstallationSnapshotJson,
                    OptionsJson, AllowExtraArgsOverrideManaged, IsDefault, CreatedUtc, UpdatedUtc)
                VALUES($id, '测试配置', $modelId, $modelSnapshot, $installationId, $installationSnapshot,
                       $options, 0, 1, $utc, $utc);
                """;
            command.Parameters.AddWithValue("$id", profileId.ToString("D"));
            command.Parameters.AddWithValue("$modelId", modelId.ToString("D"));
            command.Parameters.AddWithValue(
                "$modelSnapshot",
                JsonSerializer.Serialize(new { modelId, name = "model", filePath = oldModel }));
            command.Parameters.AddWithValue("$installationId", installationId.ToString("D"));
            command.Parameters.AddWithValue(
                "$installationSnapshot",
                JsonSerializer.Serialize(new
                {
                    installationId,
                    versionLabel = "b11703",
                    executablePath = oldRuntime,
                }));
            command.Parameters.AddWithValue(
                "$options",
                JsonSerializer.Serialize(new { modelPath = oldModel, host = "127.0.0.1", port = 8080 }));
            command.Parameters.AddWithValue("$utc", now);
            await command.ExecuteNonQueryAsync();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO ServerInstances(
                    Id, Name, ProfileId, ProfileSnapshotJson, ModelId, InstallationId, State, Host, Port,
                    ExecutablePathSnapshot, Generation, LogFilePath, CreatedUtc, UpdatedUtc)
                VALUES($id, '实例', $profileId, $profileSnapshot, $modelId, $installationId, 1, '127.0.0.1', 8080,
                       $executable, 0, $log, $utc, $utc);
                """;
            command.Parameters.AddWithValue("$id", instanceId.ToString("D"));
            command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            command.Parameters.AddWithValue(
                "$profileSnapshot",
                JsonSerializer.Serialize(new { installationId, executablePath = oldRuntime }));
            command.Parameters.AddWithValue("$modelId", modelId.ToString("D"));
            command.Parameters.AddWithValue("$installationId", installationId.ToString("D"));
            command.Parameters.AddWithValue("$executable", oldRuntime);
            command.Parameters.AddWithValue("$log", oldLog);
            command.Parameters.AddWithValue("$utc", now);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertInstallationAsync(
        SqliteConnection connection,
        Guid id,
        string versionLabel,
        string executablePath,
        bool isDefault)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO LlamaCppInstallations(
                Id, VersionLabel, SourceKind, Directory, ExecutablePath, Status, IsDefault, InstalledUtc)
            VALUES($id, $version, 0, $directory, $executable, 1, $isDefault, $utc);
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$version", versionLabel);
        command.Parameters.AddWithValue("$directory", Path.GetDirectoryName(executablePath)!);
        command.Parameters.AddWithValue("$executable", executablePath);
        command.Parameters.AddWithValue("$isDefault", isDefault ? 1 : 0);
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string CreateTempDirectory(string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), "LlamaLauncherRelocation", tag, Guid.NewGuid().ToString("N"));
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
