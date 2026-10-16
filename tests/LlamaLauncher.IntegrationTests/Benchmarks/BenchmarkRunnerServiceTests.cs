using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Benchmarks;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Domain;
using LlamaLauncher.Domain.Benchmarks;
using LlamaLauncher.Infrastructure.Sqlite;
using LlamaLauncher.IntegrationTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Benchmarks;

/// <summary>基准测试编排：样本计划、失败中止、取消、持久化与并发保护。</summary>
public sealed class BenchmarkRunnerServiceTests
{
    [Fact]
    public async Task Run_CompletesAllSamplesAndPersists()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var store = new SqliteBenchmarkRunStore(database, NullLogger<SqliteBenchmarkRunStore>.Instance);
        var instance = CreateInstance("测试实例", 8123);
        var client = new FakeBenchmarkClient();
        var manager = new FakeInstanceManager(new[] { instance }) { ApiKey = "secret-key" };
        var runner = CreateRunner(client, store, manager);
        var progress = new List<BenchmarkProgress>();
        var options = new BenchmarkOptions
        {
            PromptTokens = new List<int> { 2048, 512 },
            GenerateTokens = 64,
            Repetitions = 2,
            WarmupRuns = 1,
            CooldownSeconds = 0,
        };

        var record = await runner.RunAsync(instance.Id, options, progress.Add);

        Assert.Equal(BenchmarkRunState.Succeeded, record.State);
        Assert.Null(record.ErrorSummary);
        Assert.Equal(6, record.Samples.Count);
        Assert.Equal(2, record.Samples.Count(s => s.IsWarmup));
        Assert.All(record.Samples, s => Assert.True(s.Succeeded));
        // 档位升序执行；每档先预热再重复。
        Assert.Equal(
            new[] { 512, 512, 512, 2048, 2048, 2048 },
            record.Samples.Select(s => s.TargetPromptTokens));
        Assert.Equal(
            new[] { 0, 1, 2, 0, 1, 2 },
            record.Samples.Select(s => s.Repetition));
        Assert.Equal("测试实例", record.InstanceNameSnapshot);
        Assert.NotNull(record.EndedUtc);
        Assert.NotNull(record.DurationMs);

        // 请求参数与 API Key 传递；每个样本提示前缀唯一（用于绕过服务端缓存）。
        Assert.Equal(6, client.Requests.Count);
        Assert.All(client.Requests, r => Assert.Equal("secret-key", r.ApiKey));
        Assert.All(client.Requests, r => Assert.Equal(64, r.GenerateTokens));
        Assert.All(client.Requests, r => Assert.StartsWith("Benchmark probe ", r.Prompt));
        Assert.Equal(
            client.Requests.Count,
            client.Requests.Select(r => r.Prompt).Distinct().Count());
        Assert.Equal("http://127.0.0.1:8123", client.Requests[0].BaseUrl);

        // 进度上报覆盖预热与正式测量。
        Assert.NotEmpty(progress);
        Assert.Equal(6, progress.Max(p => p.CompletedSamples));
        Assert.Contains(progress, p => p.StageText.Contains("预热"));
        Assert.Contains(progress, p => p.StageText.Contains("第 2/2 次"));

        // 增量持久化：结束后记录完整可读。
        var stored = await store.GetAsync(record.RunId);
        Assert.NotNull(stored);
        Assert.Equal(record.Samples.Count, stored!.Samples.Count);
        Assert.Equal(record.State, stored.State);
        var summary = Assert.Single(await store.ListAsync());
        Assert.Equal(record.RunId, summary.RunId);
        Assert.Equal(instance.Id, summary.InstanceId);
    }

    [Fact]
    public async Task Run_WhenConsecutiveFailures_StopsEarlyAndMarksFailed()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var store = new SqliteBenchmarkRunStore(database, NullLogger<SqliteBenchmarkRunStore>.Instance);
        var instance = CreateInstance("失败实例", 8124);
        var client = new FakeBenchmarkClient(_ => BenchmarkCallResult.Fail("连接被拒绝：服务未运行。"));
        var runner = CreateRunner(client, store, new FakeInstanceManager(new[] { instance }));
        var options = new BenchmarkOptions
        {
            PromptTokens = new List<int> { 512, 2048 },
            Repetitions = 2,
            WarmupRuns = 1,
        };

        var record = await runner.RunAsync(instance.Id, options);

        Assert.Equal(BenchmarkRunState.Failed, record.State);
        Assert.Equal(2, record.Samples.Count); // 连续 2 次失败后提前中止，不再跑剩余 4 个样本
        Assert.All(record.Samples, s => Assert.False(s.Succeeded));
        Assert.Contains("连续 2 次调用失败", record.ErrorSummary);
        Assert.Contains("连接被拒绝", record.ErrorSummary);
    }

    [Fact]
    public async Task Run_WithPartialSuccess_KeepsSucceededWithEarlyStopNote()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var store = new SqliteBenchmarkRunStore(database, NullLogger<SqliteBenchmarkRunStore>.Instance);
        var instance = CreateInstance("部分失败实例", 8125);
        var client = new FakeBenchmarkClient(
            call => call == 0 ? FakeBenchmarkClient.SuccessResult() : BenchmarkCallResult.Fail("服务内部错误（HTTP 500）。"));
        var runner = CreateRunner(client, store, new FakeInstanceManager(new[] { instance }));
        var options = new BenchmarkOptions
        {
            PromptTokens = new List<int> { 512, 1024 },
            Repetitions = 2,
            WarmupRuns = 0,
        };

        var record = await runner.RunAsync(instance.Id, options);

        Assert.Equal(BenchmarkRunState.Succeeded, record.State);
        Assert.Equal(3, record.Samples.Count);
        Assert.Single(record.Samples, s => s.Succeeded);
        Assert.StartsWith("提前中止：", record.ErrorSummary);
    }

    [Fact]
    public async Task Run_WhenCanceled_KeepsPartialSamplesAndMarksCanceled()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var store = new SqliteBenchmarkRunStore(database, NullLogger<SqliteBenchmarkRunStore>.Instance);
        var instance = CreateInstance("取消实例", 8126);
        using var cts = new CancellationTokenSource();
        var callCount = 0;
        var client = new FakeBenchmarkClient
        {
            BeforeRespond = () =>
            {
                // 第二次调用开始前取消（模拟用户在测量过程中点击“取消”）。
                callCount++;
                if (callCount >= 2)
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            },
        };
        var runner = CreateRunner(client, store, new FakeInstanceManager(new[] { instance }));
        var options = new BenchmarkOptions
        {
            PromptTokens = new List<int> { 512 },
            Repetitions = 3,
            WarmupRuns = 0,
        };

        var record = await runner.RunAsync(instance.Id, options, cancellationToken: cts.Token);

        Assert.Equal(BenchmarkRunState.Canceled, record.State);
        Assert.Single(record.Samples);
        Assert.Contains("已取消", record.ErrorSummary);

        // 取消后的部分记录仍会落库。
        var stored = await store.GetAsync(record.RunId);
        Assert.NotNull(stored);
        Assert.Equal(BenchmarkRunState.Canceled, stored!.State);
        Assert.Single(stored.Samples);
    }

    [Fact]
    public async Task Run_WhenInstanceNotReady_Throws()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var store = new SqliteBenchmarkRunStore(database, NullLogger<SqliteBenchmarkRunStore>.Instance);
        var instance = CreateInstance("停止实例", 8127, InstanceState.Stopped);
        var runner = CreateRunner(
            new FakeBenchmarkClient(),
            store,
            new FakeInstanceManager(new[] { instance }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(instance.Id, new BenchmarkOptions()));

        Assert.Contains("只有“运行中”的实例", exception.Message);
    }

    [Fact]
    public async Task Run_WithInvalidOptions_Throws()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var store = new SqliteBenchmarkRunStore(database, NullLogger<SqliteBenchmarkRunStore>.Instance);
        var instance = CreateInstance("实例", 8128);
        var runner = CreateRunner(
            new FakeBenchmarkClient(),
            store,
            new FakeInstanceManager(new[] { instance }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(instance.Id, new BenchmarkOptions { PromptTokens = new List<int>() }));

        Assert.Contains("至少需要一个提示长度档位", exception.Message);
    }

    [Fact]
    public async Task Run_WhileAnotherRunActive_Throws()
    {
        using var directory = new TempDirectory();
        var database = await TestDatabase.CreateAsync(directory);
        var store = new SqliteBenchmarkRunStore(database, NullLogger<SqliteBenchmarkRunStore>.Instance);
        var instance = CreateInstance("并发实例", 8129);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeBenchmarkClient { BeforeRespond = () => gate.Task };
        var runner = CreateRunner(client, store, new FakeInstanceManager(new[] { instance }));
        var options = new BenchmarkOptions
        {
            PromptTokens = new List<int> { 256 },
            Repetitions = 1,
            WarmupRuns = 0,
        };

        var firstRun = runner.RunAsync(instance.Id, options);
        await WaitUntilAsync(() => client.Requests.Count >= 1, TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(instance.Id, options));
        Assert.Contains("已有一次基准测试正在运行", exception.Message);

        gate.SetResult();
        var record = await firstRun;
        Assert.Equal(BenchmarkRunState.Succeeded, record.State);
    }

    private static BenchmarkRunnerService CreateRunner(
        IBenchmarkClient client,
        SqliteBenchmarkRunStore store,
        FakeInstanceManager manager)
        => new(
            client,
            store,
            manager,
            new FakeModelRepository(),
            new FakeInstallationRepository(),
            TimeProvider.System,
            NullLogger<BenchmarkRunnerService>.Instance);

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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待条件超时。");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>可编程基准客户端：记录请求，返回脚本化结果，可在响应前阻塞。</summary>
    private sealed class FakeBenchmarkClient : IBenchmarkClient
    {
        private readonly Func<int, BenchmarkCallResult> _factory;
        private int _calls;

        public FakeBenchmarkClient(Func<int, BenchmarkCallResult>? factory = null)
            => _factory = factory ?? (_ => SuccessResult());

        public List<BenchmarkCallRequest> Requests { get; } = new();

        public Func<Task>? BeforeRespond { get; init; }

        public static BenchmarkCallResult SuccessResult() => new(
            true,
            PromptTokens: 512,
            PromptMs: 100,
            PromptTokensPerSecond: 5120,
            GeneratedTokens: 64,
            GeneratedMs: 500,
            GeneratedTokensPerSecond: 128,
            TimeToFirstTokenMs: 80,
            TotalMs: 700,
            Error: null);

        public async Task<BenchmarkCallResult> RunOnceAsync(
            BenchmarkCallRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (BeforeRespond is not null)
            {
                await BeforeRespond();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return _factory(_calls++);
        }
    }

    private sealed class FakeInstanceManager : IInstanceManager
    {
        private readonly List<ManagedInstance> _instances;

        public FakeInstanceManager(IEnumerable<ManagedInstance> instances) => _instances = instances.ToList();

        public string? ApiKey { get; set; }

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
            => Task.FromResult(ApiKey);
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
