using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using Microsoft.Data.Sqlite;

namespace LlamaLauncher.Infrastructure.Sqlite;

/// <summary>StartupHistory 表仓储。</summary>
public sealed class SqliteStartupHistoryRepository : IStartupHistoryRepository
{
    private const string Columns =
        "Id, InstanceId, InstanceNameSnapshot, ProfileId, ModelSnapshotJson, InstallationSnapshotJson, " +
        "CommandLineMasked, ProcessId, StartedUtc, EndedUtc, ExitCode, FinalState, FailureStage, ErrorSummary, LogFilePath";

    private readonly SqliteDatabase _database;

    public SqliteStartupHistoryRepository(SqliteDatabase database) => _database = database;

    public async Task<IReadOnlyList<StartupHistoryEntry>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var where = query.InstanceId is null ? string.Empty : " WHERE InstanceId = $instanceId";
        command.CommandText =
            $"SELECT {Columns} FROM StartupHistory{where} ORDER BY StartedUtc DESC LIMIT $limit;";
        if (query.InstanceId is { } instanceId)
        {
            command.Parameters.AddWithValue("$instanceId", instanceId.ToString("D"));
        }

        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 5000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<StartupHistoryEntry>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<StartupHistoryEntry?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM StartupHistory WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task AddAsync(StartupHistoryEntry entry, CancellationToken cancellationToken = default)
        => await UpsertAsync(entry, cancellationToken).ConfigureAwait(false);

    public async Task UpdateAsync(StartupHistoryEntry entry, CancellationToken cancellationToken = default)
        => await UpsertAsync(entry, cancellationToken).ConfigureAwait(false);

    private async Task UpsertAsync(StartupHistoryEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO StartupHistory(Id, InstanceId, InstanceNameSnapshot, ProfileId, ModelSnapshotJson,
                                       InstallationSnapshotJson, CommandLineMasked, ProcessId, StartedUtc, EndedUtc,
                                       ExitCode, FinalState, FailureStage, ErrorSummary, LogFilePath)
            VALUES($id, $instanceId, $instanceName, $profileId, $modelSnapshot, $installationSnapshot, $commandLine,
                   $pid, $started, $ended, $exitCode, $finalState, $failureStage, $errorSummary, $logFile)
            ON CONFLICT(Id) DO UPDATE SET
                InstanceNameSnapshot = excluded.InstanceNameSnapshot,
                ProfileId = excluded.ProfileId,
                ModelSnapshotJson = excluded.ModelSnapshotJson,
                InstallationSnapshotJson = excluded.InstallationSnapshotJson,
                CommandLineMasked = excluded.CommandLineMasked,
                ProcessId = excluded.ProcessId,
                StartedUtc = excluded.StartedUtc,
                EndedUtc = excluded.EndedUtc,
                ExitCode = excluded.ExitCode,
                FinalState = excluded.FinalState,
                FailureStage = excluded.FailureStage,
                ErrorSummary = excluded.ErrorSummary,
                LogFilePath = excluded.LogFilePath;
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue("$instanceId", entry.InstanceId.ToString("D"));
        command.Parameters.AddNullable("$instanceName", entry.InstanceNameSnapshot);
        command.Parameters.AddNullable("$profileId", entry.ProfileId?.ToString("D"));
        command.Parameters.AddNullable("$modelSnapshot", entry.ModelSnapshotJson);
        command.Parameters.AddNullable("$installationSnapshot", entry.InstallationSnapshotJson);
        command.Parameters.AddNullable("$commandLine", entry.CommandLineMasked);
        command.Parameters.AddNullable("$pid", entry.ProcessId);
        command.Parameters.AddWithValue("$started", SqliteMapping.FormatDate(entry.StartedUtc));
        command.Parameters.AddNullable("$ended", SqliteMapping.FormatDateOrNull(entry.EndedUtc));
        command.Parameters.AddNullable("$exitCode", entry.ExitCode);
        command.Parameters.AddWithValue("$finalState", (int)entry.FinalState);
        command.Parameters.AddNullable("$failureStage", entry.FailureStage);
        command.Parameters.AddNullable("$errorSummary", entry.ErrorSummary);
        command.Parameters.AddNullable("$logFile", entry.LogFilePath);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static StartupHistoryEntry Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        InstanceId = Guid.Parse(reader.GetString(1)),
        InstanceNameSnapshot = reader.GetStringOrNull(2),
        ProfileId = reader.GetStringOrNull(3) is { } profileId ? Guid.Parse(profileId) : null,
        ModelSnapshotJson = reader.GetStringOrNull(4),
        InstallationSnapshotJson = reader.GetStringOrNull(5),
        CommandLineMasked = reader.GetStringOrNull(6),
        ProcessId = reader.GetInt32OrNull(7),
        StartedUtc = reader.GetDate(8),
        EndedUtc = reader.GetDateOrNull(9),
        ExitCode = reader.GetInt32OrNull(10),
        FinalState = (InstanceState)reader.GetInt32(11),
        FailureStage = reader.GetStringOrNull(12),
        ErrorSummary = reader.GetStringOrNull(13),
        LogFilePath = reader.GetStringOrNull(14),
    };
}
