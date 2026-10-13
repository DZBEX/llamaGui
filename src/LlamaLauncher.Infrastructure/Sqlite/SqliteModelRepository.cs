using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using Microsoft.Data.Sqlite;

namespace LlamaLauncher.Infrastructure.Sqlite;

/// <summary>Models 表仓储。</summary>
public sealed class SqliteModelRepository : IModelRepository
{
    private const string Columns =
        "Id, Name, FilePath, CanonicalPath, FileSize, FileLastWriteUtc, Status, MetadataStatus, " +
        "MetadataJson, MetadataError, Alias, TagsJson, Notes, DefaultProfileId, CreatedUtc, UpdatedUtc, LastCheckedUtc";

    private readonly SqliteDatabase _database;

    public SqliteModelRepository(SqliteDatabase database) => _database = database;

    public async Task<IReadOnlyList<ModelRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM Models ORDER BY Name COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ModelRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<ModelRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM Models WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<ModelRecord?> FindByCanonicalPathAsync(string canonicalPath, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM Models WHERE CanonicalPath = $path COLLATE NOCASE;";
        command.Parameters.AddWithValue("$path", canonicalPath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task UpsertAsync(ModelRecord model, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Models(Id, Name, FilePath, CanonicalPath, FileSize, FileLastWriteUtc, Status, MetadataStatus,
                               MetadataJson, MetadataError, Alias, TagsJson, Notes, DefaultProfileId, CreatedUtc, UpdatedUtc, LastCheckedUtc)
            VALUES($id, $name, $filePath, $canonicalPath, $size, $lastWrite, $status, $metadataStatus,
                   $metadataJson, $metadataError, $alias, $tagsJson, $notes, $defaultProfileId, $created, $updated, $lastChecked)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                FilePath = excluded.FilePath,
                CanonicalPath = excluded.CanonicalPath,
                FileSize = excluded.FileSize,
                FileLastWriteUtc = excluded.FileLastWriteUtc,
                Status = excluded.Status,
                MetadataStatus = excluded.MetadataStatus,
                MetadataJson = excluded.MetadataJson,
                MetadataError = excluded.MetadataError,
                Alias = excluded.Alias,
                TagsJson = excluded.TagsJson,
                Notes = excluded.Notes,
                DefaultProfileId = excluded.DefaultProfileId,
                CreatedUtc = excluded.CreatedUtc,
                UpdatedUtc = excluded.UpdatedUtc,
                LastCheckedUtc = excluded.LastCheckedUtc;
            """;
        command.Parameters.AddWithValue("$id", model.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", model.Name);
        command.Parameters.AddWithValue("$filePath", model.FilePath);
        command.Parameters.AddWithValue("$canonicalPath", model.CanonicalPath);
        command.Parameters.AddNullable("$size", model.FileSizeBytes);
        command.Parameters.AddNullable("$lastWrite", SqliteMapping.FormatDateOrNull(model.FileLastWriteUtc));
        command.Parameters.AddWithValue("$status", (int)model.Status);
        command.Parameters.AddWithValue("$metadataStatus", (int)model.MetadataStatus);
        command.Parameters.AddNullable("$metadataJson", SqliteMapping.SerializeOrNull(model.Metadata));
        command.Parameters.AddNullable("$metadataError", model.MetadataError);
        command.Parameters.AddNullable("$alias", model.Alias);
        command.Parameters.AddNullable("$tagsJson", model.Tags.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(model.Tags, JsonDefaults.Storage));
        command.Parameters.AddNullable("$notes", model.Notes);
        command.Parameters.AddNullable("$defaultProfileId", model.DefaultProfileId?.ToString("D"));
        command.Parameters.AddWithValue("$created", SqliteMapping.FormatDate(model.CreatedUtc));
        command.Parameters.AddWithValue("$updated", SqliteMapping.FormatDate(model.UpdatedUtc));
        command.Parameters.AddNullable("$lastChecked", SqliteMapping.FormatDateOrNull(model.LastCheckedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Models WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ModelRecord Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        FilePath = reader.GetString(2),
        CanonicalPath = reader.GetString(3),
        FileSizeBytes = reader.GetLongOrNull(4),
        FileLastWriteUtc = reader.GetDateOrNull(5),
        Status = (ModelFileStatus)reader.GetInt32(6),
        MetadataStatus = (MetadataParseStatus)reader.GetInt32(7),
        Metadata = SqliteMapping.DeserializeOrNull<GgufModelInfo>(reader.GetStringOrNull(8)),
        MetadataError = reader.GetStringOrNull(9),
        Alias = reader.GetStringOrNull(10),
        Tags = SqliteMapping.DeserializeOrNull<List<string>>(reader.GetStringOrNull(11)) ?? new(),
        Notes = reader.GetStringOrNull(12),
        DefaultProfileId = reader.GetStringOrNull(13) is { } profileId ? Guid.Parse(profileId) : null,
        CreatedUtc = reader.GetDate(14),
        UpdatedUtc = reader.GetDate(15),
        LastCheckedUtc = reader.GetDateOrNull(16),
    };
}
