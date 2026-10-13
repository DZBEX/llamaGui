using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using Microsoft.Data.Sqlite;

namespace LlamaLauncher.Infrastructure.Sqlite;

/// <summary>Conversations 表仓储。</summary>
public sealed class SqliteConversationRepository : IConversationRepository
{
    private const string Columns =
        "Id, Title, InstanceId, ModelNameSnapshot, SystemPrompt, ParametersJson, CreatedUtc, UpdatedUtc";

    private readonly SqliteDatabase _database;

    public SqliteConversationRepository(SqliteDatabase database) => _database = database;

    public async Task<IReadOnlyList<Conversation>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM Conversations ORDER BY UpdatedUtc DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<Conversation>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<Conversation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM Conversations WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task UpsertAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Conversations(Id, Title, InstanceId, ModelNameSnapshot, SystemPrompt, ParametersJson, CreatedUtc, UpdatedUtc)
            VALUES($id, $title, $instanceId, $modelName, $systemPrompt, $parameters, $created, $updated)
            ON CONFLICT(Id) DO UPDATE SET
                Title = excluded.Title,
                InstanceId = excluded.InstanceId,
                ModelNameSnapshot = excluded.ModelNameSnapshot,
                SystemPrompt = excluded.SystemPrompt,
                ParametersJson = excluded.ParametersJson,
                CreatedUtc = excluded.CreatedUtc,
                UpdatedUtc = excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$id", conversation.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", conversation.Title);
        command.Parameters.AddNullable("$instanceId", conversation.InstanceId?.ToString("D"));
        command.Parameters.AddNullable("$modelName", conversation.ModelNameSnapshot);
        command.Parameters.AddNullable("$systemPrompt", conversation.SystemPrompt);
        command.Parameters.AddWithValue("$parameters", System.Text.Json.JsonSerializer.Serialize(conversation.Parameters, JsonDefaults.Storage));
        command.Parameters.AddWithValue("$created", SqliteMapping.FormatDate(conversation.CreatedUtc));
        command.Parameters.AddWithValue("$updated", SqliteMapping.FormatDate(conversation.UpdatedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var deleteMessages = connection.CreateCommand())
        {
            deleteMessages.Transaction = transaction;
            deleteMessages.CommandText = "DELETE FROM ChatMessages WHERE ConversationId = $id;";
            deleteMessages.Parameters.AddWithValue("$id", id.ToString("D"));
            await deleteMessages.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteConversation = connection.CreateCommand())
        {
            deleteConversation.Transaction = transaction;
            deleteConversation.CommandText = "DELETE FROM Conversations WHERE Id = $id;";
            deleteConversation.Parameters.AddWithValue("$id", id.ToString("D"));
            await deleteConversation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Conversation Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Title = reader.GetString(1),
        InstanceId = reader.GetStringOrNull(2) is { } instanceId ? Guid.Parse(instanceId) : null,
        ModelNameSnapshot = reader.GetStringOrNull(3),
        SystemPrompt = reader.GetStringOrNull(4),
        Parameters = SqliteMapping.DeserializeOrNull<ChatParameters>(reader.GetStringOrNull(5)) ?? new ChatParameters(),
        CreatedUtc = reader.GetDate(6),
        UpdatedUtc = reader.GetDate(7),
    };
}
