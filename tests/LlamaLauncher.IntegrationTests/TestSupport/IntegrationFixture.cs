using System.Net;
using System.Net.Sockets;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Application.Chat;
using LlamaLauncher.Application.Services;
using LlamaLauncher.Infrastructure.Gguf;
using LlamaLauncher.Infrastructure.Http;
using LlamaLauncher.Infrastructure.Logging;
using LlamaLauncher.Infrastructure.Net;
using LlamaLauncher.Infrastructure.Processes;
using LlamaLauncher.Infrastructure.Probing;
using LlamaLauncher.Infrastructure.Sqlite;
using LlamaLauncher.Infrastructure.Updates;
using LlamaLauncher.Application.Updates;
using LlamaLauncher.Infrastructure.Backup;
using LlamaLauncher.Infrastructure.Diagnostics;
using LlamaLauncher.Infrastructure.SystemInfo;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlamaLauncher.IntegrationTests.TestSupport;

/// <summary>组装真实实现（临时数据库 + 受控子进程）的集成测试夹具。</summary>
public sealed class IntegrationFixture : IDisposable
{
    public IntegrationFixture()
    {
        Directory = new TempDirectory();
        TimeProvider = new FakeTimeProvider();
        Paths = new TestAppPaths(Directory.Path);
        Paths.EnsureCreated();
        Database = TestDatabase.CreateAsync(Directory).GetAwaiter().GetResult();

        Models = new SqliteModelRepository(Database);
        Profiles = new SqliteProfileRepository(Database);
        InstallationsRepo = new SqliteInstallationRepository(Database);
        InstancesRepo = new SqliteInstanceRepository(Database);
        History = new SqliteStartupHistoryRepository(Database);
        Conversations = new SqliteConversationRepository(Database);
        Messages = new SqliteMessageRepository(Database);
        UpdateOperations = new SqliteUpdateOperationRepository(Database);
        Settings = new SqliteSettingsRepository(Database);

        Secrets = new InMemorySecretStore();
        LogStore = new InstanceLogStore(
            Directory.Combine("logs"),
            TimeProvider,
            NullLogger<InstanceLogStore>.Instance);
        ProcessRunner = new ServerProcessRunner();
        Inspector = new WindowsProcessInspector();
        ProbeRunner = new ExternalProcessRunner(TimeProvider, NullLogger<ExternalProcessRunner>.Instance);
        Probe = new LlamaBinaryProbeService(
            ProbeRunner,
            TimeProvider,
            NullLogger<LlamaBinaryProbeService>.Instance,
            new LlamaProbeTimeouts(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15)));
        Health = new ServerHealthService(NullLogger<ServerHealthService>.Instance);
        PortProbe = new TcpPortProbe();
        ModelCatalog = new ModelCatalogService(
            Models,
            new GgufMetadataReader(TimeProvider),
            TimeProvider,
            NullLogger<ModelCatalogService>.Instance);
        InstallationService = new LlamaInstallationService(
            InstallationsRepo,
            Profiles,
            Probe,
            TimeProvider,
            NullLogger<LlamaInstallationService>.Instance);
        Manager = new InstanceManager(
            InstancesRepo,
            Profiles,
            Models,
            InstallationsRepo,
            Probe,
            ProcessRunner,
            Inspector,
            PortProbe,
            Health,
            Secrets,
            LogStore,
            History,
            TimeProvider,
            ManagerLogger);
        ConversationsService = new ConversationService(
            Conversations,
            Messages,
            TimeProvider,
            NullLogger<ConversationService>.Instance);
        ChatClient = new StreamingChatClient(NullLogger<StreamingChatClient>.Instance);
        ChatService = new ChatSessionService(
            ConversationsService,
            Messages,
            ChatClient,
            Manager,
            Models,
            Health,
            TimeProvider,
            NullLogger<ChatSessionService>.Instance);
        ManifestStore = new InstallationManifestStore(
            Paths,
            TimeProvider,
            NullLogger<InstallationManifestStore>.Instance);
        Hardware = new HardwareProbeService(ProbeRunner, NullLogger<HardwareProbeService>.Instance);
        SettingsService = new LlamaLauncher.Application.Services.SettingsService(
            Settings,
            TimeProvider,
            NullLogger<LlamaLauncher.Application.Services.SettingsService>.Instance);
        ProfileService = new LaunchProfileService(
            Profiles,
            Models,
            InstallationsRepo,
            InstancesRepo,
            Probe,
            Secrets,
            TimeProvider,
            NullLogger<LaunchProfileService>.Instance);
        Backup = new BackupRestoreService(
            Database,
            Paths,
            TimeProvider,
            NullLogger<BackupRestoreService>.Instance);
        Diagnostics = new DiagnosticsService(
            Paths,
            Database,
            InstallationsRepo,
            InstancesRepo,
            UpdateOperations,
            SettingsService,
            Hardware,
            TimeProvider,
            NullLogger<DiagnosticsService>.Instance);
    }

    public CaptureLogger<InstanceManager> ManagerLogger { get; } = new();

    public TempDirectory Directory { get; }

    public FakeTimeProvider TimeProvider { get; }

    public SqliteDatabase Database { get; }

    public SqliteModelRepository Models { get; }

    public SqliteProfileRepository Profiles { get; }

    public SqliteInstallationRepository InstallationsRepo { get; }

    public SqliteInstanceRepository InstancesRepo { get; }

    public SqliteStartupHistoryRepository History { get; }

    public SqliteConversationRepository Conversations { get; }

    public SqliteMessageRepository Messages { get; }

    public SqliteUpdateOperationRepository UpdateOperations { get; }

    public SqliteSettingsRepository Settings { get; }

    public InMemorySecretStore Secrets { get; }

    public InstanceLogStore LogStore { get; }

    public ServerProcessRunner ProcessRunner { get; }

    public WindowsProcessInspector Inspector { get; }

    public ExternalProcessRunner ProbeRunner { get; }

    public LlamaBinaryProbeService Probe { get; }

    public ServerHealthService Health { get; }

    public TcpPortProbe PortProbe { get; }

    public ModelCatalogService ModelCatalog { get; }

    public LlamaInstallationService InstallationService { get; }

    public InstanceManager Manager { get; }

    public ConversationService ConversationsService { get; }

    public StreamingChatClient ChatClient { get; }

    public ChatSessionService ChatService { get; }

    public TestAppPaths Paths { get; }

    public InstallationManifestStore ManifestStore { get; }

    public HardwareProbeService Hardware { get; }

    public LlamaLauncher.Application.Services.SettingsService SettingsService { get; }

    public LaunchProfileService ProfileService { get; }

    public BackupRestoreService Backup { get; }

    public DiagnosticsService Diagnostics { get; }

    public static string ChildExecutablePath => Probing.ExternalProcessRunnerTests.ChildExecutablePath;

    /// <summary>创建一个最小有效 GGUF 文件。</summary>
    public string CreateModelFile(string name = "test-model.gguf")
    {
        var path = Directory.Combine(name);
        File.WriteAllBytes(
            path,
            new GgufBuilder()
                .AddString("general.name", "集成测试模型")
                .AddString("general.architecture", "llama")
                .AddUInt32("general.file_type", 15)
                .Build());
        return path;
    }

    /// <summary>获取一个当前可用的本地端口。</summary>
    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>创建完整装配的更新服务（ Feed/下载服务可由测试注入，其余使用真实实现）。</summary>
    public LlamaUpdateService CreateUpdateService(
        LlamaLauncher.Application.Abstractions.IReleaseFeedService feed,
        LlamaLauncher.Application.Abstractions.IDownloadService downloads)
        => new(
            feed,
            downloads,
            new SafeZipExtractor(),
            Probe,
            InstallationsRepo,
            Profiles,
            InstancesRepo,
            UpdateOperations,
            ManifestStore,
            Paths,
            TimeProvider,
            NullLogger<LlamaUpdateService>.Instance);

    public void SetEnvironment(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

    public void Dispose()
    {
        try
        {
            Manager.StopAllAsync(force: true).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }

        try
        {
            Manager.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
        }

        try
        {
            LogStore.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }

        Health.Dispose();
        ChatClient.Dispose();
        Directory.Dispose();
    }
}
