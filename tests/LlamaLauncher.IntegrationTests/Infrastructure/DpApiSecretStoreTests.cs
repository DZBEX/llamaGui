using LlamaLauncher.IntegrationTests.TestSupport;
using LlamaLauncher.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Infrastructure;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DpApiSecretStoreTests
{
    [Fact]
    public async Task RoundTrip_ProtectsAndRestores()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var store = new DpApiSecretStore(database, new FakeTimeProvider(), NullLogger<DpApiSecretStore>.Instance);

        await store.SetAsync("test.key", "my-secret-value");
        Assert.True(await store.ExistsAsync("test.key"));
        Assert.Equal("my-secret-value", await store.GetAsync("test.key"));

        await store.SetAsync("test.key", "rotated-value");
        Assert.Equal("rotated-value", await store.GetAsync("test.key"));

        Assert.Contains("test.key", await store.ListNamesAsync());
        await store.DeleteAsync("test.key");
        Assert.False(await store.ExistsAsync("test.key"));
        Assert.Null(await store.GetAsync("test.key"));
    }

    [Fact]
    public async Task Database_DoesNotContainPlaintextSecret()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var store = new DpApiSecretStore(database, new FakeTimeProvider(), NullLogger<DpApiSecretStore>.Instance);
        const string secret = "plaintext-check-8734658";
        await store.SetAsync(SecretNamesForTest, secret);

        // 直接读取数据库文件字节，确认不存在明文
        await using (var connection = await database.OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(FULL);";
            await command.ExecuteNonQueryAsync();
        }

        byte[] bytes;
        await using (var stream = new FileStream(
            database.DatabasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite))
        {
            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes);
        }

        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public async Task EmptyValue_IsRejected()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var store = new DpApiSecretStore(database, new FakeTimeProvider(), NullLogger<DpApiSecretStore>.Instance);
        await Assert.ThrowsAsync<SecretStoreException>(() => store.SetAsync("x", string.Empty));
    }

    private const string SecretNamesForTest = "test.plaintext-check";
}
