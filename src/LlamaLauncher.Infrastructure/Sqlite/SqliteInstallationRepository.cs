using System.Text.Json;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using Microsoft.Data.Sqlite;

namespace LlamaLauncher.Infrastructure.Sqlite;

/// <summary>LlamaCppInstallations 表仓储。</summary>
public sealed class SqliteInstallationRepository : IInstallationRepository
{
    private const string Columns =
        "Id, VersionLabel, BuildInfo, AssetName, SourceUrl, SourceKind, Directory, ExecutablePath, Sha256, " +
        "Sha256FromOfficialDigest, ArchiveSize, Status, IsDefault, CapabilitiesJson, LastProbedUtc, InstalledUtc, " +
        "Notes, BackendTagsJson";

    private readonly SqliteDatabase _database;

    public SqliteInstallationRepository(SqliteDatabase database) => _database = database;

    public async Task<IReadOnlyList<LlamaCppInstallation>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM LlamaCppInstallations ORDER BY InstalledUtc DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<LlamaCppInstallation>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<LlamaCppInstallation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM LlamaCppInstallations WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<LlamaCppInstallation?> FindByExecutablePathAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM LlamaCppInstallations WHERE ExecutablePath = $path COLLATE NOCASE;";
        command.Parameters.AddWithValue("$path", executablePath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task UpsertAsync(LlamaCppInstallation installation, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO LlamaCppInstallations(Id, VersionLabel, BuildInfo, AssetName, SourceUrl, SourceKind, Directory,
                                                  ExecutablePath, Sha256, Sha256FromOfficialDigest, ArchiveSize, Status,
                                                  IsDefault, CapabilitiesJson, LastProbedUtc, InstalledUtc, Notes, BackendTagsJson)
                VALUES($id, $version, $buildInfo, $assetName, $sourceUrl, $sourceKind, $directory,
                       $executablePath, $sha256, $sha256Official, $archiveSize, $status,
                       $isDefault, $capabilities, $lastProbed, $installed, $notes, $backendTags)
                ON CONFLICT(Id) DO UPDATE SET
                    VersionLabel = excluded.VersionLabel,
                    BuildInfo = excluded.BuildInfo,
                    AssetName = excluded.AssetName,
                    SourceUrl = excluded.SourceUrl,
                    SourceKind = excluded.SourceKind,
                    Directory = excluded.Directory,
                    ExecutablePath = excluded.ExecutablePath,
                    Sha256 = excluded.Sha256,
                    Sha256FromOfficialDigest = excluded.Sha256FromOfficialDigest,
                    ArchiveSize = excluded.ArchiveSize,
                    Status = excluded.Status,
                    IsDefault = excluded.IsDefault,
                    CapabilitiesJson = excluded.CapabilitiesJson,
                    LastProbedUtc = excluded.LastProbedUtc,
                    InstalledUtc = excluded.InstalledUtc,
                    Notes = excluded.Notes,
                    BackendTagsJson = excluded.BackendTagsJson;
                """;
            command.Parameters.AddWithValue("$id", installation.Id.ToString("D"));
            command.Parameters.AddWithValue("$version", installation.VersionLabel);
            command.Parameters.AddNullable("$buildInfo", installation.BuildInfo);
            command.Parameters.AddNullable("$assetName", installation.AssetName);
            command.Parameters.AddNullable("$sourceUrl", installation.SourceUrl);
            command.Parameters.AddWithValue("$sourceKind", (int)installation.SourceKind);
            command.Parameters.AddWithValue("$directory", installation.Directory);
            command.Parameters.AddWithValue("$executablePath", installation.ExecutablePath);
            command.Parameters.AddNullable("$sha256", installation.Sha256);
            command.Parameters.AddWithValue("$sha256Official", SqliteMapping.BoolInt(installation.Sha256FromOfficialDigest));
            command.Parameters.AddNullable("$archiveSize", installation.ArchiveSizeBytes);
            command.Parameters.AddWithValue("$status", (int)installation.Status);
            command.Parameters.AddWithValue("$isDefault", SqliteMapping.BoolInt(installation.IsDefault));
            command.Parameters.AddNullable("$capabilities", installation.CapabilitiesJson);
            command.Parameters.AddNullable("$lastProbed", SqliteMapping.FormatDateOrNull(installation.LastProbedUtc));
            command.Parameters.AddWithValue("$installed", SqliteMapping.FormatDate(installation.InstalledUtc));
            command.Parameters.AddNullable("$notes", installation.Notes);
            command.Parameters.AddNullable(
                "$backendTags",
                installation.BackendTags.Count == 0
                    ? null
                    : JsonSerializer.Serialize(installation.BackendTags, JsonDefaults.Storage));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (installation.IsDefault)
        {
            await using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "UPDATE LlamaCppInstallations SET IsDefault = 0 WHERE Id <> $id AND IsDefault = 1;";
            clear.Parameters.AddWithValue("$id", installation.Id.ToString("D"));
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM LlamaCppInstallations WHERE Id = $id;";
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
            clear.CommandText = "UPDATE LlamaCppInstallations SET IsDefault = 0 WHERE Id <> $id;";
            clear.Parameters.AddWithValue("$id", id.ToString("D"));
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var set = connection.CreateCommand())
        {
            set.Transaction = transaction;
            set.CommandText = "UPDATE LlamaCppInstallations SET IsDefault = 1 WHERE Id = $id;";
            set.Parameters.AddWithValue("$id", id.ToString("D"));
            var affected = await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
            {
                throw new InvalidOperationException("要设为默认的 llama.cpp 安装不存在。");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LlamaCppInstallation?> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM LlamaCppInstallations WHERE IsDefault = 1 LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    private static LlamaCppInstallation Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        VersionLabel = reader.GetString(1),
        BuildInfo = reader.GetStringOrNull(2),
        AssetName = reader.GetStringOrNull(3),
        SourceUrl = reader.GetStringOrNull(4),
        SourceKind = (InstallationSourceKind)reader.GetInt32(5),
        Directory = reader.GetString(6),
        ExecutablePath = reader.GetString(7),
        Sha256 = reader.GetStringOrNull(8),
        Sha256FromOfficialDigest = reader.GetInt32(9) != 0,
        ArchiveSizeBytes = reader.GetLongOrNull(10),
        Status = (InstallationStatus)reader.GetInt32(11),
        IsDefault = reader.GetInt32(12) != 0,
        CapabilitiesJson = reader.GetStringOrNull(13),
        LastProbedUtc = reader.GetDateOrNull(14),
        InstalledUtc = reader.GetDate(15),
        Notes = reader.GetStringOrNull(16),
        BackendTags = SqliteMapping.DeserializeOrNull<List<BackendKind>>(reader.GetStringOrNull(17)) ?? new(),
    };
}
