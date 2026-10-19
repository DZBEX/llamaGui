using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure;

/// <summary>程序/数据目录整体搬家后的路径适配结果。</summary>
public sealed record PortableRelocationReport(
    string ProgramRoot,
    IReadOnlyList<string> OldRoots,
    int DatabaseChanges,
    int ManifestFilesUpdated,
    IReadOnlyList<string> Issues)
{
    public bool Changed => DatabaseChanges > 0 || ManifestFilesUpdated > 0;
}

/// <summary>
/// 便携目录重定位：整个程序目录（含 data/runtimes）被复制到另一台电脑或另一个路径后，
/// 把数据库与安装清单中的旧绝对路径前缀改写为新程序目录，使实例配置无需重建。
/// 同时写入程序位置标记；即使标记缺失，也会根据“数据库中的旧路径 + 新目录下存在的相同相对路径”推断旧根目录。
/// </summary>
public sealed class PortableDataRelocationService
{
    public const string MarkerFileName = "program-root.txt";

    private static readonly (string Table, string Column)[] PathColumns =
    {
        ("Models", "FilePath"),
        ("Models", "CanonicalPath"),
        ("LaunchProfiles", "ModelSnapshotJson"),
        ("LaunchProfiles", "InstallationSnapshotJson"),
        ("LaunchProfiles", "OptionsJson"),
        ("LlamaCppInstallations", "Directory"),
        ("LlamaCppInstallations", "ExecutablePath"),
        ("ServerInstances", "ProfileSnapshotJson"),
        ("ServerInstances", "ExecutablePathSnapshot"),
        ("ServerInstances", "LogFilePath"),
        ("StartupHistory", "ModelSnapshotJson"),
        ("StartupHistory", "InstallationSnapshotJson"),
        ("StartupHistory", "CommandLineMasked"),
        ("StartupHistory", "LogFilePath"),
        ("UpdateOperations", "StagingDirectory"),
        ("UpdateOperations", "InstalledDirectory"),
        ("AppSettings", "ValueJson"),
    };

    private readonly AppPaths _paths;
    private readonly ILogger<PortableDataRelocationService> _logger;
    private readonly object _gate = new();
    private bool _completed;
    private PortableRelocationReport? _lastReport;

    public PortableDataRelocationService(
        AppPaths paths,
        ILogger<PortableDataRelocationService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public PortableRelocationReport? LastReport
    {
        get
        {
            lock (_gate)
            {
                return _lastReport;
            }
        }
    }

    /// <summary>启动时执行一次；重复调用返回同一结果。</summary>
    public PortableRelocationReport? RebaseIfNeeded()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return _lastReport;
            }

            _completed = true;
            try
            {
                _lastReport = RebaseCore();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "便携目录路径适配失败");
                _lastReport = new PortableRelocationReport(
                    _paths.BaseDirectory,
                    Array.Empty<string>(),
                    0,
                    0,
                    new[] { "路径适配失败：" + ex.Message });
            }

            return _lastReport;
        }
    }

    private PortableRelocationReport RebaseCore()
    {
        var currentRoot = TrimDirectory(_paths.BaseDirectory);
        var dataRoot = TrimDirectory(_paths.DataRoot);
        var markerDirectory = Path.Combine(dataRoot, "data");
        var markerPath = Path.Combine(markerDirectory, MarkerFileName);
        var issues = new List<string>();
        var oldRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(markerDirectory);
        var previousRoot = ReadMarker(markerPath, issues);
        if (!string.IsNullOrWhiteSpace(previousRoot) && !PathEquals(previousRoot, currentRoot))
        {
            oldRoots.Add(TrimDirectory(previousRoot));
        }

        var databasePath = _paths.DatabasePath;
        if (File.Exists(databasePath))
        {
            CollectDatabaseRoots(databasePath, currentRoot, oldRoots, issues);
        }

        CollectManifestRoots(_paths.InstallationsManifestPath, currentRoot, oldRoots, issues);
        CollectManifestRoots(_paths.InstallationsManifestPath + ".bak", currentRoot, oldRoots, issues);

        oldRoots.RemoveWhere(root =>
            string.IsNullOrWhiteSpace(root)
            || PathEquals(root, currentRoot)
            || !Path.IsPathRooted(root)
            || string.Equals(TrimDirectory(Path.GetPathRoot(root) ?? string.Empty), root, StringComparison.OrdinalIgnoreCase));

        var databaseChanges = 0;
        var manifestFiles = 0;
        var portableData = IsUnder(dataRoot, currentRoot);
        if (oldRoots.Count > 0 && portableData)
        {
            if (File.Exists(databasePath))
            {
                try
                {
                    databaseChanges = RewriteDatabase(databasePath, currentRoot, oldRoots);
                }
                catch (Exception ex)
                {
                    issues.Add("数据库路径适配失败：" + ex.Message);
                }
            }

            foreach (var manifestPath in new[]
                     {
                         _paths.InstallationsManifestPath,
                         _paths.InstallationsManifestPath + ".bak",
                     })
            {
                try
                {
                    if (RewriteTextFile(manifestPath, currentRoot, oldRoots))
                    {
                        manifestFiles++;
                    }
                }
                catch (Exception ex)
                {
                    issues.Add($"安装清单适配失败（{Path.GetFileName(manifestPath)}）：{ex.Message}");
                }
            }
        }

        try
        {
            File.WriteAllText(markerPath, currentRoot + Environment.NewLine);
        }
        catch (Exception ex)
        {
            issues.Add("写入程序位置标记失败：" + ex.Message);
        }

        return new PortableRelocationReport(
            currentRoot,
            oldRoots.ToList(),
            databaseChanges,
            manifestFiles,
            issues);
    }

    private static void CollectDatabaseRoots(
        string databasePath,
        string currentRoot,
        ISet<string> roots,
        List<string> issues)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();

            foreach (var (table, column) in PathColumns)
            {
                if (!TableExists(connection, table))
                {
                    continue;
                }

                try
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT {column} FROM {table} WHERE {column} IS NOT NULL;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        if (!reader.IsDBNull(0))
                        {
                            TryInferRoot(reader.GetString(0), currentRoot, roots);
                        }
                    }
                }
                catch (SqliteException ex)
                {
                    issues.Add($"读取 {table}.{column} 失败：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            issues.Add("读取数据库路径失败：" + ex.Message);
        }
    }

    private static void CollectManifestRoots(
        string manifestPath,
        string currentRoot,
        ISet<string> roots,
        List<string> issues)
    {
        if (!File.Exists(manifestPath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                foreach (var name in new[] { "directory", "executablePath" })
                {
                    if (entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        TryInferRoot(value.GetString(), currentRoot, roots);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            issues.Add($"读取安装清单失败（{Path.GetFileName(manifestPath)}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 从失效的旧绝对路径推断旧程序根目录：寻找该路径末尾在当前程序目录下仍然存在的最大相对后缀。
    /// 例：旧 D:\A\bin\net8.0\runtimes\b11703\llama-server.exe、新 D:\B\bin\net8.0 下存在
    /// runtimes\b11703\llama-server.exe，则旧根为 D:\A\bin\net8.0。
    /// </summary>
    private static void TryInferRoot(string? value, string currentRoot, ISet<string> roots)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(value.Trim());
        }
        catch (Exception)
        {
            return;
        }

        // 仍然存在的绝对路径不需要推断（例如新电脑上模型仍放在原来的 D:\LLM\models）。
        // 若原始程序目录仍存在，位置标记也足以在整包复制时触发明确的路径迁移。
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            return;
        }

        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(pathRoot))
        {
            return;
        }

        var relative = fullPath[pathRoot.Length..].TrimStart('\\', '/');
        var parts = relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (var offset = 1; offset < parts.Length; offset++)
        {
            var suffix = string.Join(Path.DirectorySeparatorChar, parts.Skip(offset));
            var candidate = Path.Combine(currentRoot, suffix);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                continue;
            }

            var oldRoot = Path.Combine(new[] { pathRoot }.Concat(parts.Take(offset)).ToArray());
            if (!PathEquals(oldRoot, currentRoot))
            {
                roots.Add(TrimDirectory(oldRoot));
            }

            return;
        }
    }

    private static int RewriteDatabase(
        string databasePath,
        string currentRoot,
        IReadOnlyCollection<string> oldRoots)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var changes = 0;
        changes += MergeInstallationDuplicates(connection, transaction, currentRoot, oldRoots);
        changes += MergeModelDuplicates(connection, transaction, currentRoot, oldRoots);

        foreach (var oldRoot in oldRoots)
        {
            foreach (var (oldValue, newValue) in BuildVariants(oldRoot, currentRoot))
            {
                foreach (var (table, column) in PathColumns)
                {
                    if (!TableExists(connection, table))
                    {
                        continue;
                    }

                    changes += ReplaceExact(connection, transaction, table, column, oldValue, newValue);
                    changes += ReplacePrefix(
                        connection,
                        transaction,
                        table,
                        column,
                        oldValue + Path.DirectorySeparatorChar,
                        newValue + Path.DirectorySeparatorChar);
                }
            }
        }

        transaction.Commit();
        return changes;
    }

    private static IEnumerable<(string OldValue, string NewValue)> BuildVariants(
        string oldRoot,
        string newRoot)
    {
        yield return (oldRoot, newRoot);

        var oldJson = oldRoot.Replace("\\", "\\\\", StringComparison.Ordinal);
        var newJson = newRoot.Replace("\\", "\\\\", StringComparison.Ordinal);
        if (!string.Equals(oldJson, oldRoot, StringComparison.Ordinal))
        {
            yield return (oldJson, newJson);
        }

        var oldSlash = oldRoot.Replace('\\', '/');
        var newSlash = newRoot.Replace('\\', '/');
        if (!string.Equals(oldSlash, oldRoot, StringComparison.Ordinal))
        {
            yield return (oldSlash, newSlash);
        }
    }

    private static int MergeInstallationDuplicates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string currentRoot,
        IReadOnlyCollection<string> oldRoots)
    {
        if (!TableExists(connection, "LlamaCppInstallations"))
        {
            return 0;
        }

        var rows = new List<(string Id, string Path, bool IsDefault)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT Id, ExecutablePath, IsDefault FROM LlamaCppInstallations;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2) != 0));
            }
        }

        var targetByNewPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(row => !IsUnderAnyRoot(row.Path, oldRoots)))
        {
            targetByNewPath[NormalizePathKey(row.Path)] = row.Id;
        }

        var changes = 0;
        foreach (var row in rows.Where(row => IsUnderAnyRoot(row.Path, oldRoots)))
        {
            var newPath = RebasePath(row.Path, currentRoot, oldRoots);
            if (newPath is null || PathEquals(newPath, row.Path))
            {
                continue;
            }

            var key = NormalizePathKey(newPath);
            if (targetByNewPath.TryGetValue(key, out var targetId)
                && !string.Equals(targetId, row.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (row.IsDefault)
                {
                    using var setDefault = connection.CreateCommand();
                    setDefault.Transaction = transaction;
                    setDefault.CommandText = "UPDATE LlamaCppInstallations SET IsDefault = 1 WHERE Id = $target;";
                    setDefault.Parameters.AddWithValue("$target", targetId);
                    changes += setDefault.ExecuteNonQuery();
                }

                changes += UpdateReference(
                    connection,
                    transaction,
                    "LaunchProfiles",
                    "InstallationId",
                    row.Id,
                    targetId);
                changes += UpdateReference(
                    connection,
                    transaction,
                    "ServerInstances",
                    "InstallationId",
                    row.Id,
                    targetId);
                changes += ReplacePrefix(
                    connection,
                    transaction,
                    "LaunchProfiles",
                    "InstallationSnapshotJson",
                    row.Id,
                    targetId);
                changes += ReplacePrefix(
                    connection,
                    transaction,
                    "ServerInstances",
                    "ProfileSnapshotJson",
                    row.Id,
                    targetId);

                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM LlamaCppInstallations WHERE Id = $id;";
                delete.Parameters.AddWithValue("$id", row.Id);
                changes += delete.ExecuteNonQuery();
            }
            else
            {
                targetByNewPath[key] = row.Id;
            }
        }

        return changes;
    }

    private static int MergeModelDuplicates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string currentRoot,
        IReadOnlyCollection<string> oldRoots)
    {
        if (!TableExists(connection, "Models"))
        {
            return 0;
        }

        var rows = new List<(string Id, string Path)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT Id, CanonicalPath FROM Models;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var targetByNewPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(row => !IsUnderAnyRoot(row.Path, oldRoots)))
        {
            targetByNewPath[NormalizePathKey(row.Path)] = row.Id;
        }

        var changes = 0;
        foreach (var row in rows.Where(row => IsUnderAnyRoot(row.Path, oldRoots)))
        {
            var newPath = RebasePath(row.Path, currentRoot, oldRoots);
            if (newPath is null || PathEquals(newPath, row.Path))
            {
                continue;
            }

            var key = NormalizePathKey(newPath);
            if (targetByNewPath.TryGetValue(key, out var targetId)
                && !string.Equals(targetId, row.Id, StringComparison.OrdinalIgnoreCase))
            {
                changes += UpdateReference(connection, transaction, "LaunchProfiles", "ModelId", row.Id, targetId);
                changes += UpdateReference(connection, transaction, "ServerInstances", "ModelId", row.Id, targetId);
                changes += ReplacePrefix(connection, transaction, "LaunchProfiles", "ModelSnapshotJson", row.Id, targetId);
                changes += ReplacePrefix(connection, transaction, "ServerInstances", "ProfileSnapshotJson", row.Id, targetId);

                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM Models WHERE Id = $id;";
                delete.Parameters.AddWithValue("$id", row.Id);
                changes += delete.ExecuteNonQuery();
            }
            else
            {
                targetByNewPath[key] = row.Id;
            }
        }

        return changes;
    }

    private static int UpdateReference(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string oldValue,
        string newValue)
    {
        if (!TableExists(connection, table))
        {
            return 0;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE {table} SET {column} = $new WHERE {column} = $old;";
        command.Parameters.AddWithValue("$new", newValue);
        command.Parameters.AddWithValue("$old", oldValue);
        return command.ExecuteNonQuery();
    }

    private static int ReplaceExact(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string oldValue,
        string newValue)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE {table} SET {column} = $new WHERE {column} = $old;";
        command.Parameters.AddWithValue("$new", newValue);
        command.Parameters.AddWithValue("$old", oldValue);
        return command.ExecuteNonQuery();
    }

    private static int ReplacePrefix(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string oldValue,
        string newValue)
    {
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal) || oldValue.Length == 0)
        {
            return 0;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"UPDATE {table} SET {column} = replace({column}, $old, $new) " +
            $"WHERE instr({column}, $old) > 0;";
        command.Parameters.AddWithValue("$new", newValue);
        command.Parameters.AddWithValue("$old", oldValue);
        return command.ExecuteNonQuery();
    }

    private static bool RewriteTextFile(
        string path,
        string currentRoot,
        IReadOnlyCollection<string> oldRoots)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var text = File.ReadAllText(path);
        var updated = text;
        foreach (var oldRoot in oldRoots)
        {
            foreach (var (oldValue, newValue) in BuildVariants(oldRoot, currentRoot))
            {
                updated = updated.Replace(oldValue, newValue, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (string.Equals(text, updated, StringComparison.Ordinal))
        {
            return false;
        }

        File.WriteAllText(path, updated);
        return true;
    }

    private static string? RebasePath(
        string path,
        string currentRoot,
        IReadOnlyCollection<string> oldRoots)
    {
        foreach (var oldRoot in oldRoots.OrderByDescending(root => root.Length))
        {
            if (PathEquals(path, oldRoot))
            {
                return currentRoot;
            }

            if (path.StartsWith(oldRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return currentRoot + path[oldRoot.Length..];
            }
        }

        return null;
    }

    private static bool IsUnderAnyRoot(string path, IReadOnlyCollection<string> roots)
        => roots.Any(root => IsUnder(path, root));

    private static bool IsUnder(string path, string root)
    {
        var normalizedPath = TrimDirectory(path);
        var normalizedRoot = TrimDirectory(root);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                TrimDirectory(Path.GetFullPath(left)),
                TrimDirectory(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string NormalizePathKey(string path)
    {
        try
        {
            return TrimDirectory(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return TrimDirectory(path);
        }
    }

    private static string TrimDirectory(string path)
        => Path.TrimEndingDirectorySeparator(path);

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static string? ReadMarker(string markerPath, List<string> issues)
    {
        if (!File.Exists(markerPath))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(markerPath).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex)
        {
            issues.Add("读取程序位置标记失败：" + ex.Message);
            return null;
        }
    }
}

/// <summary>在数据库初始化之前执行便携路径适配。</summary>
public sealed class PortableDataRelocationHostedService : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly PortableDataRelocationService _relocation;
    private readonly ILogger<PortableDataRelocationHostedService> _logger;

    public PortableDataRelocationHostedService(
        PortableDataRelocationService relocation,
        ILogger<PortableDataRelocationHostedService> logger)
    {
        _relocation = relocation;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var report = _relocation.RebaseIfNeeded();
        if (report is { Changed: true })
        {
            _logger.LogInformation(
                "检测到程序目录变化，已适配路径：{OldRoots} → {NewRoot}（数据库 {Changes} 处，清单 {Manifests} 个）",
                string.Join("、", report.OldRoots),
                report.ProgramRoot,
                report.DatabaseChanges,
                report.ManifestFilesUpdated);
        }

        if (report is not null)
        {
            foreach (var issue in report.Issues)
            {
                _logger.LogWarning("便携路径适配：{Issue}", issue);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
