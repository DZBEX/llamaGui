using System.Text.Json;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using Microsoft.Data.Sqlite;

namespace LlamaLauncher.Infrastructure.Sqlite;

/// <summary>LaunchProfiles 表仓储。</summary>
public sealed class SqliteProfileRepository : IProfileRepository
{
    private const string Columns =
        "Id, Name, ModelId, ModelSnapshotJson, InstallationId, InstallationSnapshotJson, OptionsJson, " +
        "ExtraArguments, AllowExtraArgsOverrideManaged, EnvironmentJson, IsDefault, Notes, CreatedUtc, UpdatedUtc";

    private readonly SqliteDatabase _database;

    public SqliteProfileRepository(SqliteDatabase database) => _database = database;

    public async Task<IReadOnlyList<LaunchProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM LaunchProfiles ORDER BY Name COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<LaunchProfile>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<LaunchProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM LaunchProfiles WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<LaunchProfile?> FindByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM LaunchProfiles WHERE Name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$name", name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task UpsertAsync(LaunchProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertCoreAsync(connection, transaction, profile, cancellationToken).ConfigureAwait(false);
        if (profile.IsDefault)
        {
            await ClearOtherDefaultsAsync(connection, transaction, profile.Id, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertManyAsync(IReadOnlyList<LaunchProfile> profiles, CancellationToken cancellationToken = default)
    {
        if (profiles.Count == 0)
        {
            return;
        }

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var profile in profiles)
        {
            await UpsertCoreAsync(connection, transaction, profile, cancellationToken).ConfigureAwait(false);
            if (profile.IsDefault)
            {
                await ClearOtherDefaultsAsync(connection, transaction, profile.Id, cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM LaunchProfiles WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "UPDATE LaunchProfiles SET IsDefault = 0 WHERE Id <> $id;";
            clear.Parameters.AddWithValue("$id", id.ToString("D"));
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var set = connection.CreateCommand())
        {
            set.Transaction = transaction;
            set.CommandText = "UPDATE LaunchProfiles SET IsDefault = 1 WHERE Id = $id;";
            set.Parameters.AddWithValue("$id", id.ToString("D"));
            var affected = await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
            {
                throw new InvalidOperationException("要设为默认的配置档案不存在。");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LaunchProfile?> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM LaunchProfiles WHERE IsDefault = 1 LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    private static async Task UpsertCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LaunchProfile profile,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO LaunchProfiles(Id, Name, ModelId, ModelSnapshotJson, InstallationId, InstallationSnapshotJson,
                                       OptionsJson, ExtraArguments, AllowExtraArgsOverrideManaged, EnvironmentJson,
                                       IsDefault, Notes, CreatedUtc, UpdatedUtc)
            VALUES($id, $name, $modelId, $modelSnapshot, $installationId, $installationSnapshot,
                   $options, $extra, $allowOverride, $environment, $isDefault, $notes, $created, $updated)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                ModelId = excluded.ModelId,
                ModelSnapshotJson = excluded.ModelSnapshotJson,
                InstallationId = excluded.InstallationId,
                InstallationSnapshotJson = excluded.InstallationSnapshotJson,
                OptionsJson = excluded.OptionsJson,
                ExtraArguments = excluded.ExtraArguments,
                AllowExtraArgsOverrideManaged = excluded.AllowExtraArgsOverrideManaged,
                EnvironmentJson = excluded.EnvironmentJson,
                IsDefault = excluded.IsDefault,
                Notes = excluded.Notes,
                UpdatedUtc = excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddNullable("$modelId", profile.ModelId?.ToString("D"));
        command.Parameters.AddNullable("$modelSnapshot", SqliteMapping.SerializeOrNull(profile.ModelSnapshot));
        command.Parameters.AddNullable("$installationId", profile.InstallationId?.ToString("D"));
        command.Parameters.AddNullable("$installationSnapshot", SqliteMapping.SerializeOrNull(profile.InstallationSnapshot));
        command.Parameters.AddWithValue("$options", JsonSerializer.Serialize(profile.Options, JsonDefaults.Storage));
        command.Parameters.AddNullable("$extra", profile.ExtraArguments);
        command.Parameters.AddWithValue("$allowOverride", SqliteMapping.BoolInt(profile.AllowExtraArgsOverrideManaged));
        command.Parameters.AddNullable(
            "$environment",
            profile.EnvironmentVariables.Count == 0
                ? null
                : JsonSerializer.Serialize(profile.EnvironmentVariables, JsonDefaults.Storage));
        command.Parameters.AddWithValue("$isDefault", SqliteMapping.BoolInt(profile.IsDefault));
        command.Parameters.AddNullable("$notes", profile.Notes);
        command.Parameters.AddWithValue("$created", SqliteMapping.FormatDate(profile.CreatedUtc));
        command.Parameters.AddWithValue("$updated", SqliteMapping.FormatDate(profile.UpdatedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ClearOtherDefaultsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid keepId,
        CancellationToken cancellationToken)
    {
        await using var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "UPDATE LaunchProfiles SET IsDefault = 0 WHERE Id <> $id AND IsDefault = 1;";
        clear.Parameters.AddWithValue("$id", keepId.ToString("D"));
        await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static LaunchProfile Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        ModelId = reader.GetStringOrNull(2) is { } modelId ? Guid.Parse(modelId) : null,
        ModelSnapshot = SqliteMapping.DeserializeOrNull<ModelReferenceSnapshot>(reader.GetStringOrNull(3)),
        InstallationId = reader.GetStringOrNull(4) is { } installationId ? Guid.Parse(installationId) : null,
        InstallationSnapshot = SqliteMapping.DeserializeOrNull<InstallationReferenceSnapshot>(reader.GetStringOrNull(5)),
        Options = SqliteMapping.DeserializeOrNull<ServerLaunchOptions>(reader.GetString(6)) ?? new ServerLaunchOptions(),
        ExtraArguments = reader.GetStringOrNull(7),
        AllowExtraArgsOverrideManaged = reader.GetInt32(8) != 0,
        EnvironmentVariables = SqliteMapping.DeserializeOrNull<Dictionary<string, string>>(reader.GetStringOrNull(9))
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        IsDefault = reader.GetInt32(10) != 0,
        Notes = reader.GetStringOrNull(11),
        CreatedUtc = reader.GetDate(12),
        UpdatedUtc = reader.GetDate(13),
    };
}
