using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using Microsoft.Data.Sqlite;

namespace LlamaLauncher.Infrastructure.Sqlite;

/// <summary>ServerInstances 表仓储。</summary>
public sealed class SqliteInstanceRepository : IInstanceRepository
{
    private const string Columns =
        "Id, Name, ProfileId, ProfileSnapshotJson, ModelId, InstallationId, State, Host, Port, ProcessId, " +
        "ProcessStartTimeUtc, ExecutablePathSnapshot, Generation, StartedUtc, StoppedUtc, ExitCode, LastError, " +
        "LogFilePath, CreatedUtc, UpdatedUtc";

    private readonly SqliteDatabase _database;

    public SqliteInstanceRepository(SqliteDatabase database) => _database = database;

    public async Task<IReadOnlyList<ServerInstance>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM ServerInstances ORDER BY CreatedUtc;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ServerInstance>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<ServerInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM ServerInstances WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task UpsertAsync(ServerInstance instance, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ServerInstances(Id, Name, ProfileId, ProfileSnapshotJson, ModelId, InstallationId, State, Host,
                                        Port, ProcessId, ProcessStartTimeUtc, ExecutablePathSnapshot, Generation,
                                        StartedUtc, StoppedUtc, ExitCode, LastError, LogFilePath, CreatedUtc, UpdatedUtc)
            VALUES($id, $name, $profileId, $profileSnapshot, $modelId, $installationId, $state, $host,
                   $port, $pid, $processStart, $executable, $generation,
                   $started, $stopped, $exitCode, $lastError, $logFile, $created, $updated)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                ProfileId = excluded.ProfileId,
                ProfileSnapshotJson = excluded.ProfileSnapshotJson,
                ModelId = excluded.ModelId,
                InstallationId = excluded.InstallationId,
                State = excluded.State,
                Host = excluded.Host,
                Port = excluded.Port,
                ProcessId = excluded.ProcessId,
                ProcessStartTimeUtc = excluded.ProcessStartTimeUtc,
                ExecutablePathSnapshot = excluded.ExecutablePathSnapshot,
                Generation = excluded.Generation,
                StartedUtc = excluded.StartedUtc,
                StoppedUtc = excluded.StoppedUtc,
                ExitCode = excluded.ExitCode,
                LastError = excluded.LastError,
                LogFilePath = excluded.LogFilePath,
                CreatedUtc = excluded.CreatedUtc,
                UpdatedUtc = excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$id", instance.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", instance.Name);
        command.Parameters.AddNullable("$profileId", instance.ProfileId?.ToString("D"));
        command.Parameters.AddNullable("$profileSnapshot", instance.ProfileSnapshotJson);
        command.Parameters.AddNullable("$modelId", instance.ModelId?.ToString("D"));
        command.Parameters.AddNullable("$installationId", instance.InstallationId?.ToString("D"));
        command.Parameters.AddWithValue("$state", (int)instance.State);
        command.Parameters.AddWithValue("$host", instance.Host);
        command.Parameters.AddWithValue("$port", instance.Port);
        command.Parameters.AddNullable("$pid", instance.ProcessId);
        command.Parameters.AddNullable("$processStart", SqliteMapping.FormatDateOrNull(instance.ProcessStartTimeUtc));
        command.Parameters.AddNullable("$executable", instance.ExecutablePathSnapshot);
        command.Parameters.AddWithValue("$generation", instance.Generation);
        command.Parameters.AddNullable("$started", SqliteMapping.FormatDateOrNull(instance.StartedUtc));
        command.Parameters.AddNullable("$stopped", SqliteMapping.FormatDateOrNull(instance.StoppedUtc));
        command.Parameters.AddNullable("$exitCode", instance.ExitCode);
        command.Parameters.AddNullable("$lastError", instance.LastError);
        command.Parameters.AddNullable("$logFile", instance.LogFilePath);
        command.Parameters.AddWithValue("$created", SqliteMapping.FormatDate(instance.CreatedUtc));
        command.Parameters.AddWithValue("$updated", SqliteMapping.FormatDate(instance.UpdatedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ServerInstances WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ServerInstance Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        ProfileId = reader.GetStringOrNull(2) is { } profileId ? Guid.Parse(profileId) : null,
        ProfileSnapshotJson = reader.GetStringOrNull(3),
        ModelId = reader.GetStringOrNull(4) is { } modelId ? Guid.Parse(modelId) : null,
        InstallationId = reader.GetStringOrNull(5) is { } installationId ? Guid.Parse(installationId) : null,
        State = (InstanceState)reader.GetInt32(6),
        Host = reader.GetString(7),
        Port = reader.GetInt32(8),
        ProcessId = reader.GetInt32OrNull(9),
        ProcessStartTimeUtc = reader.GetDateOrNull(10),
        ExecutablePathSnapshot = reader.GetStringOrNull(11),
        Generation = reader.GetInt64(12),
        StartedUtc = reader.GetDateOrNull(13),
        StoppedUtc = reader.GetDateOrNull(14),
        ExitCode = reader.GetInt32OrNull(15),
        LastError = reader.GetStringOrNull(16),
        LogFilePath = reader.GetStringOrNull(17),
        CreatedUtc = reader.GetDate(18),
        UpdatedUtc = reader.GetDate(19),
    };
}
