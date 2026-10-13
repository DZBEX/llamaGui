using System.Globalization;
using System.Text.Json;
using LlamaLauncher.Domain;
using Microsoft.Data.Sqlite;

namespace LlamaLauncher.Infrastructure.Sqlite;

/// <summary>行映射与通用 SQL 辅助。</summary>
internal static class SqliteMapping
{
    public static string? GetStringOrNull(this SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static int? GetIntOrNull(this SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static int? GetInt32OrNull(this SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static long? GetLongOrNull(this SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static DateTimeOffset? GetDateOrNull(this SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));

    public static DateTimeOffset GetDate(this SqliteDataReader reader, int ordinal)
        => ParseDate(reader.GetString(ordinal));

    public static string FormatDate(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static string? FormatDateOrNull(DateTimeOffset? value)
        => value is null ? null : FormatDate(value.Value);

    public static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static T? DeserializeOrNull<T>(string? json)
        where T : class
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, JsonDefaults.Storage);

    public static string? SerializeOrNull<T>(T? value)
        where T : class
        => value is null ? null : JsonSerializer.Serialize(value, JsonDefaults.Storage);

    public static string Bool(bool value) => value ? "1" : "0";

    public static void AddNullable(this SqliteParameterCollection parameters, string name, object? value)
        => parameters.AddWithValue(name, value ?? DBNull.Value);

    public static long BoolInt(bool value) => value ? 1 : 0;
}
