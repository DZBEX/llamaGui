using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using Microsoft.Data.Sqlite;

namespace LlamaLauncher.Infrastructure.Sqlite;

/// <summary>UpdateOperations 表仓储。</summary>
public sealed class SqliteUpdateOperationRepository : IUpdateOperationRepository
{
    private const string Columns =
        "Id, Kind, FromVersion, ToVersion, AssetName, AssetUrl, AssetSize, Stage, Error, Sha256, " +
        "Sha256Verified, Sha256FromOfficialDigest, RolledBack, StagingDirectory, InstalledDirectory, StartedUtc, CompletedUtc";

    private readonly SqliteDatabase _database;

    public SqliteUpdateOperationRepository(SqliteDatabase database) => _database = database;

    public async Task AddAsync(UpdateOperation operation, CancellationToken cancellationToken = default)
        => await UpsertAsync(operation, cancellationToken).ConfigureAwait(false);

    public async Task UpdateAsync(UpdateOperation operation, CancellationToken cancellationToken = default)
        => await UpsertAsync(operation, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<UpdateOperation>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM UpdateOperations ORDER BY StartedUtc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<UpdateOperation>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    private async Task UpsertAsync(UpdateOperation operation, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO UpdateOperations(Id, Kind, FromVersion, ToVersion, AssetName, AssetUrl, AssetSize, Stage, Error,
                                         Sha256, Sha256Verified, Sha256FromOfficialDigest, RolledBack, StagingDirectory,
                                         InstalledDirectory, StartedUtc, CompletedUtc)
            VALUES($id, $kind, $fromVersion, $toVersion, $assetName, $assetUrl, $assetSize, $stage, $error,
                   $sha256, $sha256Verified, $sha256Official, $rolledBack, $staging, $installed, $started, $completed)
            ON CONFLICT(Id) DO UPDATE SET
                Kind = excluded.Kind,
                FromVersion = excluded.FromVersion,
                ToVersion = excluded.ToVersion,
                AssetName = excluded.AssetName,
                AssetUrl = excluded.AssetUrl,
                AssetSize = excluded.AssetSize,
                Stage = excluded.Stage,
                Error = excluded.Error,
                Sha256 = excluded.Sha256,
                Sha256Verified = excluded.Sha256Verified,
                Sha256FromOfficialDigest = excluded.Sha256FromOfficialDigest,
                RolledBack = excluded.RolledBack,
                StagingDirectory = excluded.StagingDirectory,
                InstalledDirectory = excluded.InstalledDirectory,
                StartedUtc = excluded.StartedUtc,
                CompletedUtc = excluded.CompletedUtc;
            """;
        command.Parameters.AddWithValue("$id", operation.Id.ToString("D"));
        command.Parameters.AddWithValue("$kind", (int)operation.Kind);
        command.Parameters.AddNullable("$fromVersion", operation.FromVersion);
        command.Parameters.AddNullable("$toVersion", operation.ToVersion);
        command.Parameters.AddNullable("$assetName", operation.AssetName);
        command.Parameters.AddNullable("$assetUrl", operation.AssetUrl);
        command.Parameters.AddNullable("$assetSize", operation.AssetSizeBytes);
        command.Parameters.AddWithValue("$stage", (int)operation.Stage);
        command.Parameters.AddNullable("$error", operation.Error);
        command.Parameters.AddNullable("$sha256", operation.Sha256);
        command.Parameters.AddWithValue("$sha256Verified", SqliteMapping.BoolInt(operation.Sha256Verified));
        command.Parameters.AddWithValue("$sha256Official", SqliteMapping.BoolInt(operation.Sha256FromOfficialDigest));
        command.Parameters.AddWithValue("$rolledBack", SqliteMapping.BoolInt(operation.RolledBack));
        command.Parameters.AddNullable("$staging", operation.StagingDirectory);
        command.Parameters.AddNullable("$installed", operation.InstalledDirectory);
        command.Parameters.AddWithValue("$started", SqliteMapping.FormatDate(operation.StartedUtc));
        command.Parameters.AddNullable("$completed", SqliteMapping.FormatDateOrNull(operation.CompletedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static UpdateOperation Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Kind = (UpdateOperationKind)reader.GetInt32(1),
        FromVersion = reader.GetStringOrNull(2),
        ToVersion = reader.GetStringOrNull(3),
        AssetName = reader.GetStringOrNull(4),
        AssetUrl = reader.GetStringOrNull(5),
        AssetSizeBytes = reader.GetLongOrNull(6),
        Stage = (UpdateStage)reader.GetInt32(7),
        Error = reader.GetStringOrNull(8),
        Sha256 = reader.GetStringOrNull(9),
        Sha256Verified = reader.GetInt32(10) != 0,
        Sha256FromOfficialDigest = reader.GetInt32(11) != 0,
        RolledBack = reader.GetInt32(12) != 0,
        StagingDirectory = reader.GetStringOrNull(13),
        InstalledDirectory = reader.GetStringOrNull(14),
        StartedUtc = reader.GetDate(15),
        CompletedUtc = reader.GetDateOrNull(16),
    };
}
