using System.IO;
using LlamaLauncher.App.Services;
using LlamaLauncher.App.ViewModels;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Benchmarks;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Domain;
using LlamaLauncher.Domain.Benchmarks;
using LlamaLauncher.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.UiTests;

/// <summary>
/// 性能基准页关键命令测试：真实 SQLite 仓储 + 真实运行编排 + 伪基准客户端，
/// 覆盖运行流程、结果聚合、参数校验、历史删除与报告复制。
/// </summary>
[Collection("数据目录串行")]
public sealed class BenchmarkViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteDatabase _database;
    private readonly SqliteBenchmarkRunStore _store;

    public BenchmarkViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "LlamaLauncherBenchmarkVmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _database = new SqliteDatabase(Path.Combine(_root, "test.db"), NullLogger<SqliteDatabase>.Instance);
        _database.InitializeAsync().GetAwaiter().GetResult();
        _store = new SqliteBenchmarkRunStore(_database, NullLogger<SqliteBenchmarkRunStore>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    [Fact]
    public async Task RunBenchmark_CompletesAndShowsResultsAndHistory()
    {
        var instance = CreateInstance("基准实例", 8130);
        var clipboard = new RecordingClipboardService();
        var notifications = new RecordingNotificationService();
        using var viewModel = CreateViewModel(
            new FakeBenchmarkClient(),
            new[] { instance },
            clipboard,
            notifications);
        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.PromptTokensText = "256";
        viewModel.GenerateTokens = 32;
        viewModel.Repetitions = 2;
        viewModel.WarmupRuns = 1;

        await viewModel.RunBenchmarkCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsRunning);
        Assert.Single(viewModel.CurrentGroups);
        Assert.Equal(3, viewModel.CurrentSamples.Count);
        var group = Assert.Single(viewModel.CurrentGroups);
        Assert.Equal("256", group.PromptText);
        Assert.Equal("2 成功", group.SampleText);
        var record = viewModel.ResolveRecord(null);
        Assert.NotNull(record);
        Assert.Equal(BenchmarkRunState.Succeeded, record!.State);
        Assert.Equal(3, record.Samples.Count);
        Assert.Single(viewModel.History);
        Assert.Equal(1, viewModel.ProgressValue);
        Assert.Contains(notifications.Messages, m => m.Contains("基准测试完成"));

        // 复制报告：Markdown 包含标题与样本内容。
        await viewModel.CopyReportCommand.ExecuteAsync("current");
        Assert.Contains("# 模型性能基准报告", clipboard.LastText);
        Assert.Contains("| 256 |", clipboard.LastText);
    }

    [Fact]
    public async Task RunBenchmark_WhenNoReadyInstance_NotifiesAndDoesNotCreateRecord()
    {
        var instance = CreateInstance("停止实例", 8131, InstanceState.Stopped);
        var notifications = new RecordingNotificationService();
        using var viewModel = CreateViewModel(
            new FakeBenchmarkClient(),
            new[] { instance },
            new RecordingClipboardService(),
            notifications);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        await viewModel.RunBenchmarkCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.History);
        Assert.Empty(await _store.ListAsync());
        Assert.Contains(notifications.Messages, m => m.Contains("运行中"));
    }

    [Fact]
    public async Task RunBenchmark_WithInvalidConfig_ShowsWarning()
    {
        var instance = CreateInstance("基准实例", 8132);
        var notifications = new RecordingNotificationService();
        var dialogs = new RecordingDialogService();
        using var viewModel = CreateViewModel(
            new FakeBenchmarkClient(),
            new[] { instance },
            new RecordingClipboardService(),
            notifications,
            dialogs);
        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.PromptTokensText = "abc";

        await viewModel.RunBenchmarkCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.History);
        Assert.Contains(notifications.Messages, m => m.Contains("基准配置无效"));
        Assert.Contains(dialogs.Warnings, w => w.Message.Contains("无法解析提示长度"));
    }

    [Fact]
    public async Task DeleteHistory_WhenConfirmed_RemovesRecord()
    {
        var instance = CreateInstance("基准实例", 8133);
        var dialogs = new RecordingDialogService { ConfirmResult = true };
        using var viewModel = CreateViewModel(
            new FakeBenchmarkClient(),
            new[] { instance },
            new RecordingClipboardService(),
            new RecordingNotificationService(),
            dialogs);
        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.PromptTokensText = "128";
        viewModel.Repetitions = 1;
        viewModel.WarmupRuns = 0;
        await viewModel.RunBenchmarkCommand.ExecuteAsync(null);
        Assert.Single(viewModel.History);

        await viewModel.DeleteHistoryCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.History);
        Assert.Empty(await _store.ListAsync());
    }

    [Fact]
    public void BuildOptions_DeduplicatesAndSortsPromptLengths()
    {
        using var viewModel = CreateViewModel(
            new FakeBenchmarkClient(),
            Array.Empty<ManagedInstance>(),
            new RecordingClipboardService(),
            new RecordingNotificationService());
        viewModel.PromptTokensText = "2048, 512, 512";

        var (options, issues) = viewModel.BuildOptions();

        Assert.Empty(issues);
        Assert.NotNull(options);
        Assert.Equal(new[] { 512, 2048 }, options!.PromptTokens);
    }

    private BenchmarkViewModel CreateViewModel(
        IBenchmarkClient client,
        IEnumerable<ManagedInstance> instances,
        IClipboardService clipboard,
        INotificationService notifications,
        IDialogService? dialogs = null)
    {
        var manager = new FakeInstanceManager(instances);
        var runner = new BenchmarkRunnerService(
            client,
            _store,
            manager,
            new FakeModelRepository(),
            new FakeInstallationRepository(),
            TimeProvider.System,
            NullLogger<BenchmarkRunnerService>.Instance);
        return new BenchmarkViewModel(
            runner,
            _store,
            manager,
            clipboard,
            notifications,
            dialogs ?? new RecordingDialogService(),
            NullLogger<BenchmarkViewModel>.Instance);
    }

    private static ManagedInstance CreateInstance(
        string name,
        int port,
        InstanceState state = InstanceState.Ready)
        => new(new ServerInstance
        {
            Id = Guid.NewGuid(),
            Name = name,
            Port = port,
            Host = "127.0.0.1",
            State = state,
        });

    private sealed class FakeBenchmarkClient : IBenchmarkClient
    {
        public Task<BenchmarkCallResult> RunOnceAsync(
            BenchmarkCallRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new BenchmarkCallResult(
                true,
                PromptTokens: 256,
                PromptMs: 100,
                PromptTokensPerSecond: 2560,
                GeneratedTokens: 32,
                GeneratedMs: 200,
                GeneratedTokensPerSecond: 160,
                TimeToFirstTokenMs: 60,
                TotalMs: 320,
                Error: null));
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;

        public List<(string Title, string Message)> Warnings { get; } = new();

        public Task<bool> ConfirmAsync(ConfirmDialogOptions options) => Task.FromResult(ConfirmResult);

        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;

        public Task ShowWarningAsync(string title, string message)
        {
            Warnings.Add((title, message));
            return Task.CompletedTask;
        }

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<string?> PromptAsync(string title, string message, string initialValue = "")
            => Task.FromResult<string?>(null);

        public Task<string?> PromptWithConfirmationAsync(string title, string message, string requiredConfirmationKeyword)
            => Task.FromResult<string?>(null);

        public Task<(bool Confirmed, bool CheckboxChecked)> ConfirmWithCheckboxAsync(
            ConfirmDialogOptions options,
            string checkboxText,
            bool defaultChecked = false)
            => Task.FromResult((false, false));

        public string? PickOpenFile(string title, string filter, string? initialDirectory = null) => null;

        public string? PickFolder(string title, string? initialDirectory = null) => null;

        public string? PickSaveFile(string title, string filter, string? defaultFileName = null, string? initialDirectory = null)
            => null;
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<string> Messages { get; } = new();

        public event Action<string?>? StatusChanged;

        public void SetStatus(string message) => Messages.Add(message);

        public void ClearStatus() => StatusChanged?.Invoke(null);

        public void Notify(string message) => Messages.Add(message);
    }

    private sealed class RecordingClipboardService : IClipboardService
    {
        public string? LastText { get; private set; }

        public Task<(bool Success, string? Error)> SetTextAsync(string text)
        {
            LastText = text;
            return Task.FromResult((true, (string?)null));
        }
    }

    private sealed class FakeInstanceManager : IInstanceManager
    {
        private readonly List<ManagedInstance> _instances;

        public FakeInstanceManager(IEnumerable<ManagedInstance> instances) => _instances = instances.ToList();

        public event EventHandler<Guid>? InstanceChanged
        {
            add { }
            remove { }
        }

        public Task<RecoveryReport> InitializeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new RecoveryReport(0, 0, Array.Empty<string>()));

        public IReadOnlyList<ManagedInstance> GetInstances() => _instances;

        public ManagedInstance? Get(Guid instanceId) => _instances.FirstOrDefault(i => i.Id == instanceId);

        public Task<ManagedInstance> CreateAsync(string name, Guid profileId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StartResult> StartAsync(
            Guid instanceId,
            bool waitForReady = false,
            bool lanAccessConfirmed = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task StopAsync(Guid instanceId, bool force = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StartResult> RestartAsync(Guid instanceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RemoveAsync(Guid instanceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task StopAllAsync(bool force = false, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string?> GetInstanceApiKeyAsync(Guid instanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeModelRepository : IModelRepository
    {
        public Task<IReadOnlyList<ModelRecord>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ModelRecord>>(Array.Empty<ModelRecord>());

        public Task<ModelRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<ModelRecord?>(null);

        public Task<ModelRecord?> FindByCanonicalPathAsync(string canonicalPath, CancellationToken cancellationToken = default)
            => Task.FromResult<ModelRecord?>(null);

        public Task UpsertAsync(ModelRecord model, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeInstallationRepository : IInstallationRepository
    {
        public Task<IReadOnlyList<LlamaCppInstallation>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LlamaCppInstallation>>(Array.Empty<LlamaCppInstallation>());

        public Task<LlamaCppInstallation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<LlamaCppInstallation?>(null);

        public Task<LlamaCppInstallation?> FindByExecutablePathAsync(string executablePath, CancellationToken cancellationToken = default)
            => Task.FromResult<LlamaCppInstallation?>(null);

        public Task UpsertAsync(LlamaCppInstallation installation, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<LlamaCppInstallation?> GetDefaultAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<LlamaCppInstallation?>(null);
    }
}
