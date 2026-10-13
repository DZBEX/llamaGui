using System.Text.Json;
using System.Text.Json.Nodes;
using LlamaLauncher.Domain;
using Microsoft.Data.Sqlite;

namespace LlamaLauncher.Infrastructure;

/// <summary>
/// 旧数据位置 → 程序根目录的一次性迁移。
/// 旧位置包括：%AppData%\LlamaLauncher（旧默认）与 exe\data（旧便携模式布局）。
/// 迁移会整体移动目录内容，并改写数据库与安装清单中的绝对路径前缀，
/// 保证已安装的 llama.cpp 版本、日志链接等在迁移后仍可用。
/// </summary>
public static class LegacyDataMigrator
{
    private const string LegacyPortableMarkerFileName = "portable.marker";

    public sealed record Report(
        string Source,
        string Destination,
        bool Migrated,
        IReadOnlyList<string> Issues);

    /// <summary>
    /// 需要时执行迁移并返回报告；返回 null 表示无需迁移
    /// （设置了自定义数据目录，或程序目录下已有数据，或程序目录不可写由 AppPaths 自行回退）。
    /// </summary>
    public static Report? MigrateIfNeeded(string programRoot)
    {
        programRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programRoot));

        // 环境变量 / 引导配置指定了数据目录时不自动迁移。
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable)))
        {
            return null;
        }

        var (configured, _) = AppPaths.ReadBootstrap(programRoot);
        if (configured is not null)
        {
            return null;
        }

        if (!AppPaths.IsDirectoryWritable(programRoot))
        {
            return null;
        }

        var destinationDatabase = Path.Combine(programRoot, "data", "llamalauncher.db");
        if (File.Exists(destinationDatabase))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(programRoot, "data"), // 旧便携布局：数据原本在 exe\data
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LlamaLauncher"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "data", "llamalauncher.db")))
            {
                return Migrate(candidate, programRoot);
            }
        }

        return null;
    }

    /// <summary>把 sourceRoot 下的全部内容移动到 destinationRoot，并改写其中的绝对路径。</summary>
    public static Report Migrate(string sourceRoot, string destinationRoot)
    {
        // 去掉末尾分隔符，避免改写绝对路径前缀时产生 “\\” 之类的重复分隔符。
        sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        var issues = new List<string>();

        if (string.Equals(
                Path.TrimEndingDirectorySeparator(sourceRoot),
                Path.TrimEndingDirectorySeparator(destinationRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return new Report(sourceRoot, destinationRoot, false, new[] { "源目录与目标目录相同，未执行迁移。" });
        }

        Directory.CreateDirectory(destinationRoot);

        // data（数据库）优先移动：如果它失败，其余内容保持原位，应用仍可在旧位置正常工作。
        var entries = Directory.GetFileSystemEntries(sourceRoot)
            .OrderBy(entry => string.Equals(Path.GetFileName(entry), "data", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        foreach (var entry in entries)
        {
            var destination = Path.Combine(destinationRoot, Path.GetFileName(entry));
            try
            {
                if (Directory.Exists(entry))
                {
                    MoveDirectory(entry, destination);
                }
                else
                {
                    MoveFile(entry, destination);
                }
            }
            catch (Exception ex)
            {
                issues.Add($"移动 {Path.GetFileName(entry)} 失败：{ex.Message}");
            }
        }

        var databasePath = Path.Combine(destinationRoot, "data", "llamalauncher.db");
        var migrated = File.Exists(databasePath);

        if (migrated)
        {
            try
            {
                RewriteDatabasePaths(databasePath, sourceRoot, destinationRoot);
            }
            catch (Exception ex)
            {
                issues.Add("改写数据库内路径失败：" + ex.Message);
            }

            foreach (var manifestName in new[] { "installations.json", "installations.json.bak" })
            {
                var manifestPath = Path.Combine(destinationRoot, "manifests", manifestName);
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        RewriteManifestPaths(manifestPath, sourceRoot, destinationRoot);
                    }
                    catch (Exception ex)
                    {
                        issues.Add($"改写安装清单 {manifestName} 失败：{ex.Message}");
                    }
                }
            }

            // 旧便携标记已无用途（数据目录默认就是程序目录），清理避免误导。
            try
            {
                var marker = Path.Combine(destinationRoot, LegacyPortableMarkerFileName);
                if (File.Exists(marker))
                {
                    File.Delete(marker);
                }
            }
            catch (Exception)
            {
            }
        }

        TryDeleteEmptyDirectory(sourceRoot);
        return new Report(sourceRoot, destinationRoot, migrated, issues);
    }

    private static void MoveDirectory(string source, string destination)
    {
        if (!Directory.Exists(destination))
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException)
            {
                // 跨卷移动会抛 IOException，退化为逐项复制+删除。
            }
        }

        // 目标已存在：逐项合并。
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.GetFileSystemEntries(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                MoveDirectory(entry, target);
            }
            else
            {
                MoveFile(entry, target);
            }
        }

        TryDeleteEmptyDirectory(source);
    }

    private static void MoveFile(string source, string destination)
    {
        if (File.Exists(destination))
        {
            return; // 不覆盖目标位置已有文件
        }

        try
        {
            File.Move(source, destination);
        }
        catch (IOException)
        {
            File.Copy(source, destination, overwrite: false);
            File.Delete(source);
        }
    }

    private static void RewriteDatabasePaths(string databasePath, string oldRoot, string newRoot)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var targets = new (string Table, string Column)[]
        {
            ("LlamaCppInstallations", "Directory"),
            ("LlamaCppInstallations", "ExecutablePath"),
            ("ServerInstances", "ExecutablePathSnapshot"),
            ("ServerInstances", "LogFilePath"),
            ("StartupHistory", "LogFilePath"),
            ("StartupHistory", "CommandLineMasked"),
            ("UpdateOperations", "StagingDirectory"),
            ("UpdateOperations", "InstalledDirectory"),
        };

        foreach (var (table, column) in targets)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"UPDATE {table} SET {column} = replace({column}, $old, $new) WHERE instr({column}, $old) = 1;";
            command.Parameters.AddWithValue("$old", oldRoot);
            command.Parameters.AddWithValue("$new", newRoot);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void RewriteManifestPaths(string manifestPath, string oldRoot, string newRoot)
    {
        var node = JsonNode.Parse(File.ReadAllText(manifestPath));
        if (node?["entries"] is not JsonArray entries)
        {
            return;
        }

        foreach (var entry in entries)
        {
            ReplaceJsonStringPrefix(entry?["directory"], oldRoot, newRoot);
            ReplaceJsonStringPrefix(entry?["executablePath"], oldRoot, newRoot);
        }

        // 走 JsonSerializer.Serialize（而不是 JsonNode.ToJsonString(裸 options)）：后者在 .NET 8
        // 需要显式 TypeInfoResolver，否则 JsonValue 写入时会抛 InvalidOperationException。
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(node, JsonDefaults.Export));
    }

    private static void ReplaceJsonStringPrefix(JsonNode? value, string oldRoot, string newRoot)
    {
        if (value is null)
        {
            return;
        }

        var text = value.GetValue<string>();
        if (text.StartsWith(oldRoot, StringComparison.OrdinalIgnoreCase))
        {
            value.ReplaceWith(newRoot + text[oldRoot.Length..]);
        }
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception)
        {
        }
    }
}
