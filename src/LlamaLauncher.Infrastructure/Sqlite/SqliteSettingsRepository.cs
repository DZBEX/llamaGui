using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using Microsoft.Data.Sqlite;

namespace LlamaLauncher.Infrastructure.Sqlite;

/// <summary>AppSettings 表仓储。</summary>
public sealed class SqliteSettingsRepository : ISettingsRepository
{
    private readonly SqliteDatabase _database;

    public SqliteSettingsRepository(SqliteDatabase database) => _database = database;

    public async Task<AppSetting?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, ValueJson, UpdatedUtc FROM AppSettings WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AppSetting
        {
            Key = reader.GetString(0),
            ValueJson = reader.GetString(1),
            UpdatedUtc = SqliteMapping.ParseDate(reader.GetString(2)),
        };
    }

    public async Task<IReadOnlyList<AppSetting>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, ValueJson, UpdatedUtc FROM AppSettings;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<AppSetting>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new AppSetting
            {
                Key = reader.GetString(0),
                ValueJson = reader.GetString(1),
                UpdatedUtc = SqliteMapping.ParseDate(reader.GetString(2)),
            });
        }

        return results;
    }

    public async Task SetAsync(string key, string valueJson, DateTimeOffset updatedUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AppSettings(Key, ValueJson, UpdatedUtc)
            VALUES($key, $value, $updated)
            ON CONFLICT(Key) DO UPDATE SET ValueJson = excluded.ValueJson, UpdatedUtc = excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", valueJson);
        command.Parameters.AddWithValue("$updated", SqliteMapping.FormatDate(updatedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AppSettings WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
