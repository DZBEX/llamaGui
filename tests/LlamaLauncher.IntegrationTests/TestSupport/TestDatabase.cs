using LlamaLauncher.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlamaLauncher.IntegrationTests.TestSupport;

/// <summary>在临时目录中创建并初始化测试数据库。</summary>
public static class TestDatabase
{
    public static async Task<SqliteDatabase> CreateAsync(TempDirectory directory)
    {
        var database = new SqliteDatabase(
            directory.Combine("data", "test.db"),
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync();
        return database;
    }
}
