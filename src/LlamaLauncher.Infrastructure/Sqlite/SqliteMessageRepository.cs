using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using Microsoft.Data.Sqlite;

namespace LlamaLauncher.Infrastructure.Sqlite;

/// <summary>ChatMessages 表仓储。</summary>
public sealed class SqliteMessageRepository : IMessageRepository
{
    private const string Columns =
        "Id, ConversationId, Role, Content, Status, Sequence, ParamsJson, MetricsJson, ErrorText, CreatedUtc, CompletedUtc";

    private readonly SqliteDatabase _database;

    public SqliteMessageRepository(SqliteDatabase database) => _database = database;

    public async Task<IReadOnlyList<ChatMessage>> GetByConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM ChatMessages WHERE ConversationId = $id ORDER BY Sequence;";
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ChatMessage>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<ChatMessage?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM ChatMessages WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task AddAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ChatMessages(Id, ConversationId, Role, Content, Status, Sequence, ParamsJson, MetricsJson,
                                     ErrorText, CreatedUtc, CompletedUtc)
            VALUES($id, $conversationId, $role, $content, $status, $sequence, $params, $metrics,
                   $error, $created, $completed);
            """;
        Bind(message, command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ChatMessages SET
                Content = $content,
                Status = $status,
                ParamsJson = $params,
                MetricsJson = $metrics,
                ErrorText = $error,
                CompletedUtc = $completed
            WHERE Id = $id;
            """;
        Bind(message, command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ChatMessages WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", messageId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteByConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ChatMessages WHERE ConversationId = $id;";
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> GetNextSequenceAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Sequence), -1) + 1 FROM ChatMessages WHERE ConversationId = $id;";
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<int> MarkStreamingAsInterruptedAsync(DateTimeOffset completedUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ChatMessages
            SET Status = $interrupted,
                CompletedUtc = COALESCE(CompletedUtc, $completed),
                ErrorText = COALESCE(ErrorText, '应用退出时生成未完成，已标记为中断。')
            WHERE Status IN ($pending, $streaming);
            """;
        command.Parameters.AddWithValue("$interrupted", (int)MessageStatus.Interrupted);
        command.Parameters.AddWithValue("$pending", (int)MessageStatus.Pending);
        command.Parameters.AddWithValue("$streaming", (int)MessageStatus.Streaming);
        command.Parameters.AddWithValue("$completed", SqliteMapping.FormatDate(completedUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(ChatMessage message, SqliteCommand command)
    {
        command.Parameters.AddWithValue("$id", message.Id.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", message.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$role", (int)message.Role);
        command.Parameters.AddWithValue("$content", message.Content);
        command.Parameters.AddWithValue("$status", (int)message.Status);
        command.Parameters.AddWithValue("$sequence", message.Sequence);
        command.Parameters.AddNullable("$params", SqliteMapping.SerializeOrNull(message.ParametersSnapshot));
        command.Parameters.AddNullable("$metrics", SqliteMapping.SerializeOrNull(message.Metrics));
        command.Parameters.AddNullable("$error", message.ErrorText);
        command.Parameters.AddWithValue("$created", SqliteMapping.FormatDate(message.CreatedUtc));
        command.Parameters.AddNullable("$completed", SqliteMapping.FormatDateOrNull(message.CompletedUtc));
    }

    private static ChatMessage Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        ConversationId = Guid.Parse(reader.GetString(1)),
        Role = (ChatRole)reader.GetInt32(2),
        Content = reader.GetString(3),
        Status = (MessageStatus)reader.GetInt32(4),
        Sequence = reader.GetInt64(5),
        ParametersSnapshot = SqliteMapping.DeserializeOrNull<ChatParameters>(reader.GetStringOrNull(6)),
        Metrics = SqliteMapping.DeserializeOrNull<ChatMetrics>(reader.GetStringOrNull(7)),
        ErrorText = reader.GetStringOrNull(8),
        CreatedUtc = reader.GetDate(9),
        CompletedUtc = reader.GetDateOrNull(10),
    };
}
