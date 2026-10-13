using System.Security.Cryptography;
using System.Text;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Security;

/// <summary>秘密存储失败（解密失败、平台不支持等）。</summary>
public sealed class SecretStoreException : Exception
{
    public SecretStoreException(string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
    }
}

/// <summary>
/// 基于 Windows DPAPI（CurrentUser）的秘密存储；密文保存在 SQLite 的 Secrets 表。
/// 普通日志、导出、诊断包不会包含明文。
/// </summary>
public sealed class DpApiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LlamaLauncher.SecretStore.v1");
    private readonly SqliteDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DpApiSecretStore> _logger;

    public DpApiSecretStore(
        SqliteDatabase database,
        TimeProvider timeProvider,
        ILogger<DpApiSecretStore> logger)
    {
        _database = database;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Secrets WHERE Name = $name;";
        command.Parameters.AddWithValue("$name", name);
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        return count > 0;
    }

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProtectedBlob FROM Secrets WHERE Name = $name;";
        command.Parameters.AddWithValue("$name", name);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not byte[] blob)
        {
            return null;
        }

        try
        {
            var plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException ex)
        {
            throw new SecretStoreException(
                $"无法解密秘密“{name}”：密文由其他 Windows 用户或机器创建，请重新配置。",
                ex);
        }
    }

    public async Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new SecretStoreException("秘密值不能为空。");
        }

        byte[] blob;
        try
        {
            blob = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value),
                Entropy,
                DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new SecretStoreException($"保护秘密失败：{ex.Message}", ex);
        }

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Secrets(Name, ProtectedBlob, UpdatedUtc)
            VALUES($name, $blob, $updated)
            ON CONFLICT(Name) DO UPDATE SET ProtectedBlob = excluded.ProtectedBlob, UpdatedUtc = excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$blob", blob);
        command.Parameters.AddWithValue(
            "$updated",
            _timeProvider.GetUtcNow().ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("已更新秘密 {Name}（DPAPI 保护，不记录明文）", name);
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Secrets WHERE Name = $name;";
        command.Parameters.AddWithValue("$name", name);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("已删除秘密 {Name}", name);
    }

    public async Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM Secrets ORDER BY Name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var names = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
