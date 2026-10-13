using LlamaLauncher.Infrastructure.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Hosting;

/// <summary>宿主启动阶段初始化数据库（建库、迁移）。失败会阻止应用启动并显示明确原因。</summary>
public sealed class DatabaseInitializationHostedService : IHostedService
{
    private readonly SqliteDatabase _database;
    private readonly ILogger<DatabaseInitializationHostedService> _logger;

    public DatabaseInitializationHostedService(SqliteDatabase database, ILogger<DatabaseInitializationHostedService> logger)
    {
        _database = database;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("数据库就绪：{Path}", _database.DatabasePath);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
