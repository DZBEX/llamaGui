using LlamaLauncher.Application.Workflows;
using LlamaLauncher.Domain.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Sqlite;

/// <summary>工作流运行历史的 SQLite 仓储（完整记录含定义快照；摘要查询不加载大字段）。</summary>
public sealed class SqliteWorkflowRunStore : IWorkflowRunStore
{
    private const string SummaryColumns =
        "RunId, WorkflowId, WorkflowNameSnapshot, VersionLabel, StartedUtc, EndedUtc, DurationMs, State, TestMode, ErrorSummary";

    private readonly SqliteDatabase _database;
    private readonly ILogger<SqliteWorkflowRunStore> _logger;

    public SqliteWorkflowRunStore(SqliteDatabase database, ILogger<SqliteWorkflowRunStore> logger)
    {
        _database = database;
        _logger = logger;
    }

    public async Task AppendAsync(WorkflowRunRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WorkflowRuns(RunId, WorkflowId, WorkflowNameSnapshot, VersionLabel, AppVersion,
                                     StartedUtc, EndedUtc, DurationMs, State, TestMode,
                                     InputSnapshotJson, OutputJson, ErrorSummary, ModelContextJson,
                                     DefinitionSnapshotJson, ValidationSummary, FinalVariablesJson, NodesJson, LogsJson)
            VALUES($runId, $workflowId, $name, $versionLabel, $appVersion,
                   $started, $ended, $duration, $state, $testMode,
                   $input, $output, $error, $modelContext,
                   $definition, $validation, $finalVariables, $nodes, $logs)
            ON CONFLICT(RunId) DO UPDATE SET
                EndedUtc = excluded.EndedUtc,
                DurationMs = excluded.DurationMs,
                State = excluded.State,
                InputSnapshotJson = excluded.InputSnapshotJson,
                OutputJson = excluded.OutputJson,
                ErrorSummary = excluded.ErrorSummary,
                ModelContextJson = excluded.ModelContextJson,
                DefinitionSnapshotJson = excluded.DefinitionSnapshotJson,
                ValidationSummary = excluded.ValidationSummary,
                FinalVariablesJson = excluded.FinalVariablesJson,
                NodesJson = excluded.NodesJson,
                LogsJson = excluded.LogsJson;
            """;
        command.Parameters.AddWithValue("$runId", record.RunId);
        command.Parameters.AddWithValue("$workflowId", record.WorkflowId);
        command.Parameters.AddWithValue("$name", record.WorkflowNameSnapshot);
        command.Parameters.AddNullable("$versionLabel", record.VersionLabel);
        command.Parameters.AddWithValue("$appVersion", record.AppVersion);
        command.Parameters.AddWithValue("$started", SqliteMapping.FormatDate(record.StartedUtc));
        command.Parameters.AddNullable("$ended", SqliteMapping.FormatDateOrNull(record.EndedUtc));
        command.Parameters.AddNullable("$duration", record.DurationMs);
        command.Parameters.AddWithValue("$state", (int)record.State);
        command.Parameters.AddWithValue("$testMode", (int)record.TestMode);
        command.Parameters.AddNullable("$input", record.InputSnapshotJson);
        command.Parameters.AddNullable("$output", record.OutputJson);
        command.Parameters.AddNullable("$error", record.ErrorSummary);
        command.Parameters.AddNullable("$modelContext", record.ModelContextJson);
        command.Parameters.AddNullable("$definition", record.DefinitionSnapshotJson);
        command.Parameters.AddNullable("$validation", record.ValidationSummary);
        command.Parameters.AddNullable("$finalVariables", record.FinalVariablesJson);
        command.Parameters.AddWithValue("$nodes", WorkflowJson.Serialize(record.Nodes, indented: false));
        command.Parameters.AddWithValue("$logs", WorkflowJson.Serialize(record.Logs, indented: false));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkflowRunSummary>> ListAsync(
        WorkflowRunQuery query,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.WorkflowId))
        {
            conditions.Add("WorkflowId = $workflowId");
            command.Parameters.AddWithValue("$workflowId", query.WorkflowId);
        }

        if (query.State is { } state)
        {
            conditions.Add("State = $state");
            command.Parameters.AddWithValue("$state", (int)state);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            conditions.Add("(WorkflowNameSnapshot LIKE $search OR RunId LIKE $search OR IFNULL(ErrorSummary,'') LIKE $search)");
            command.Parameters.AddWithValue("$search", "%" + query.SearchText.Trim() + "%");
        }

        var where = conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions);
        command.CommandText =
            $"SELECT {SummaryColumns} FROM WorkflowRuns{where} ORDER BY StartedUtc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 2000));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<WorkflowRunSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new WorkflowRunSummary
            {
                RunId = reader.GetString(0),
                WorkflowId = reader.GetString(1),
                WorkflowNameSnapshot = reader.GetString(2),
                VersionLabel = reader.GetStringOrNull(3),
                StartedUtc = reader.GetDate(4),
                EndedUtc = reader.GetDateOrNull(5),
                DurationMs = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                State = (WorkflowRunState)reader.GetInt32(7),
                TestMode = (WorkflowTestMode)reader.GetInt32(8),
                ErrorSummary = reader.GetStringOrNull(9),
            });
        }

        return results;
    }

    public async Task<WorkflowRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RunId, WorkflowId, WorkflowNameSnapshot, VersionLabel, AppVersion,
                   StartedUtc, EndedUtc, DurationMs, State, TestMode,
                   InputSnapshotJson, OutputJson, ErrorSummary, ModelContextJson,
                   DefinitionSnapshotJson, ValidationSummary, FinalVariablesJson, NodesJson, LogsJson
            FROM WorkflowRuns WHERE RunId = $runId;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var record = new WorkflowRunRecord
        {
            RunId = reader.GetString(0),
            WorkflowId = reader.GetString(1),
            WorkflowNameSnapshot = reader.GetString(2),
            VersionLabel = reader.GetStringOrNull(3),
            AppVersion = reader.GetString(4),
            StartedUtc = reader.GetDate(5),
            EndedUtc = reader.GetDateOrNull(6),
            DurationMs = reader.IsDBNull(7) ? null : reader.GetDouble(7),
            State = (WorkflowRunState)reader.GetInt32(8),
            TestMode = (WorkflowTestMode)reader.GetInt32(9),
            InputSnapshotJson = reader.GetStringOrNull(10),
            OutputJson = reader.GetStringOrNull(11),
            ErrorSummary = reader.GetStringOrNull(12),
            ModelContextJson = reader.GetStringOrNull(13),
            DefinitionSnapshotJson = reader.GetStringOrNull(14),
            ValidationSummary = reader.GetStringOrNull(15),
            FinalVariablesJson = reader.GetStringOrNull(16),
        };

        var nodesJson = reader.GetStringOrNull(17);
        if (!string.IsNullOrWhiteSpace(nodesJson))
        {
            record.Nodes = WorkflowJson.Deserialize<List<NodeRunRecord>>(nodesJson) ?? new List<NodeRunRecord>();
        }

        var logsJson = reader.GetStringOrNull(18);
        if (!string.IsNullOrWhiteSpace(logsJson))
        {
            record.Logs = WorkflowJson.Deserialize<List<WorkflowLogEntry>>(logsJson) ?? new List<WorkflowLogEntry>();
        }

        return record;
    }

    public async Task<bool> DeleteAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM WorkflowRuns WHERE RunId = $runId;";
        command.Parameters.AddWithValue("$runId", runId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<int> PruneAsync(int keepLatest, CancellationToken cancellationToken = default)
    {
        var keep = Math.Max(0, keepLatest);
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM WorkflowRuns
            WHERE RunId NOT IN (
                SELECT RunId FROM WorkflowRuns ORDER BY StartedUtc DESC LIMIT $keep
            );
            """;
        command.Parameters.AddWithValue("$keep", keep);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            _logger.LogInformation("运行历史清理：保留最近 {Keep} 条，删除 {Removed} 条。", keep, removed);
        }

        return removed;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM WorkflowRuns;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }
}
