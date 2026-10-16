using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Exchange;
using LlamaLauncher.Application.Services;
using LlamaLauncher.Infrastructure.Gguf;
using LlamaLauncher.Infrastructure.Backup;
using LlamaLauncher.Infrastructure.Diagnostics;
using LlamaLauncher.Infrastructure.GitHub;
using LlamaLauncher.Infrastructure.Hosting;
using LlamaLauncher.Infrastructure.Http;
using LlamaLauncher.Infrastructure.Logging;
using LlamaLauncher.Infrastructure.Net;
using LlamaLauncher.Infrastructure.Processes;
using LlamaLauncher.Infrastructure.Probing;
using LlamaLauncher.Infrastructure.Security;
using LlamaLauncher.Infrastructure.Sqlite;
using LlamaLauncher.Infrastructure.SystemInfo;
using LlamaLauncher.Infrastructure.Updates;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Application.Chat;
using LlamaLauncher.Application.Updates;
using LlamaLauncher.Application.Models;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure;

/// <summary>基础设施层依赖注入注册入口。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>注册数据库、进程、HTTP、硬件、更新等基础设施服务。</summary>
    public static IServiceCollection AddLlamaLauncherCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 时间抽象（测试中可替换）
        services.TryAddSingleton(TimeProvider.System);

        // 目录与数据库
        var paths = new AppPaths(AppContext.BaseDirectory);
        paths.EnsureCreated();
        services.AddSingleton<IAppPaths>(paths);
        services.AddLogging(logging => logging.AddProvider(new AppFileLoggerProvider(paths.LogsDirectory)));
        services.AddSingleton(sp => new SqliteDatabase(
            sp.GetRequiredService<IAppPaths>().DatabasePath,
            sp.GetRequiredService<ILogger<SqliteDatabase>>()));
        services.AddHostedService<DatabaseInitializationHostedService>();

        // 仓储
        services.AddSingleton<IModelRepository, SqliteModelRepository>();
        services.AddSingleton<IProfileRepository, SqliteProfileRepository>();
        services.AddSingleton<IInstallationRepository, SqliteInstallationRepository>();
        services.AddSingleton<IInstanceRepository, SqliteInstanceRepository>();
        services.AddSingleton<IStartupHistoryRepository, SqliteStartupHistoryRepository>();
        services.AddSingleton<IConversationRepository, SqliteConversationRepository>();
        services.AddSingleton<IMessageRepository, SqliteMessageRepository>();
        services.AddSingleton<IUpdateOperationRepository, SqliteUpdateOperationRepository>();
        services.AddSingleton<ISettingsRepository, SqliteSettingsRepository>();

        // 应用服务
        services.AddSingleton<IGgufMetadataReader, GgufMetadataReader>();
        services.AddSingleton<IModelCatalogService, ModelCatalogService>();
        services.AddSingleton<IProfileExchangeService, ProfileExchangeService>();
        services.AddSingleton<IProbeProcessRunner, ExternalProcessRunner>();
        services.AddSingleton<ILlamaBinaryProbeService, LlamaBinaryProbeService>();
        services.AddSingleton<ILlamaInstallationService, LlamaInstallationService>();
        services.AddSingleton<IServerProcessRunner, ServerProcessRunner>();
        services.AddSingleton<IProcessInspector, WindowsProcessInspector>();
        services.AddSingleton<IPortProbeService, TcpPortProbe>();
        services.AddSingleton<IServerHealthService>(sp =>
            new ServerHealthService(sp.GetRequiredService<ILogger<ServerHealthService>>()));
        services.AddSingleton<ISecretStore, DpApiSecretStore>();
        services.AddSingleton<IInstanceLogStore>(sp => new InstanceLogStore(
            paths.LogsDirectory,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<InstanceLogStore>>()));
        services.AddSingleton<IInstanceManager, InstanceManager>();
        services.AddSingleton<IHardwareProbeService, HardwareProbeService>();
        services.AddSingleton<IResourceMonitorService, ResourceMonitorService>();
        services.AddSingleton<IChatCompletionClient>(sp =>
            new StreamingChatClient(sp.GetRequiredService<ILogger<StreamingChatClient>>()));
        services.AddSingleton<IConversationService, ConversationService>();
        services.AddSingleton<IChatSessionService, ChatSessionService>();
        services.AddSingleton<IReleaseFeedService>(sp =>
            new GitHubReleaseFeedService(
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<GitHubReleaseFeedService>>(),
                sp.GetRequiredService<IAppPaths>(),
                sp.GetRequiredService<ISecretStore>()));
        services.AddSingleton<IDownloadService>(sp =>
            new HttpDownloadService(sp.GetRequiredService<ILogger<HttpDownloadService>>()));
        services.AddSingleton<IZipExtractor, SafeZipExtractor>();
        services.AddSingleton<IInstallationManifestStore>(sp =>
            new InstallationManifestStore(
                paths,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<InstallationManifestStore>>()));
        services.AddSingleton<ILlamaUpdateService, LlamaUpdateService>();
        services.AddSingleton<ILaunchProfileService, LaunchProfileService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IBackupRestoreService, BackupRestoreService>();
        services.AddSingleton<IDiagnosticsService, DiagnosticsService>();

        return services;
    }
}
