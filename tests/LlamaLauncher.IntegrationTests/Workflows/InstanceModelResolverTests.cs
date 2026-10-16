using System.Text.Json.Nodes;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Instances;
using LlamaLauncher.Domain;
using LlamaLauncher.Infrastructure.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Workflows;

/// <summary>工作流模型解析：复用实例管理，正确反映就绪状态与上下文快照。</summary>
public sealed class InstanceModelResolverTests
{
    private static ManagedInstance CreateInstance(string name, int port, InstanceState state, Guid? modelId = null)
        => new(new ServerInstance
        {
            Id = Guid.NewGuid(),
            Name = name,
            Port = port,
            Host = "127.0.0.1",
            State = state,
            ModelId = modelId,
        });

    [Fact]
    public async Task 列表反映就绪状态与快照()
    {
        var ready = CreateInstance("就绪实例", 8081, InstanceState.Ready);
        var stopped = CreateInstance("停止实例", 8082, InstanceState.Stopped);
        var manager = new FakeInstanceManager(new[] { ready, stopped });
        var resolver = new InstanceModelResolver(manager, new FakeModelRepository(), NullLogger<InstanceModelResolver>.Instance);

        var targets = await resolver.ListAsync();

        Assert.Equal(2, targets.Count);
        Assert.True(targets[0].IsUsable);
        Assert.Equal("http://127.0.0.1:8081", targets[0].BaseUrl);
        Assert.False(targets[1].IsUsable);
        Assert.Contains("Stopped", targets[1].UnavailableReason);
        Assert.Equal(ready.Id.ToString("D"), targets[0].ContextSnapshot["instanceId"]!.GetValue<string>());
        Assert.Equal(8081, targets[0].ContextSnapshot["port"]!.GetValue<int>());
    }

    [Fact]
    public async Task 默认解析选择第一个就绪实例()
    {
        var stopped = CreateInstance("停止实例", 8082, InstanceState.Stopped);
        var ready = CreateInstance("就绪实例", 8081, InstanceState.Ready);
        var manager = new FakeInstanceManager(new[] { stopped, ready });
        var resolver = new InstanceModelResolver(manager, new FakeModelRepository(), NullLogger<InstanceModelResolver>.Instance);

        var target = await resolver.ResolveAsync(null);

        Assert.NotNull(target);
        Assert.Equal(ready.Id.ToString("D"), target!.InstanceId);
    }

    [Fact]
    public async Task 按实例ID解析与APIKey读取()
    {
        var ready = CreateInstance("就绪实例", 8081, InstanceState.Ready);
        var manager = new FakeInstanceManager(new[] { ready }) { ApiKey = "k-123" };
        var resolver = new InstanceModelResolver(manager, new FakeModelRepository(), NullLogger<InstanceModelResolver>.Instance);

        var byD = await resolver.ResolveAsync(ready.Id.ToString("D"));
        var byN = await resolver.ResolveAsync(ready.Id.ToString("N"));
        var missing = await resolver.ResolveAsync(Guid.NewGuid().ToString("D"));
        var key = await resolver.GetApiKeyAsync(ready.Id.ToString("D"));

        Assert.NotNull(byD);
        Assert.NotNull(byN);
        Assert.Null(missing);
        Assert.Equal("k-123", key);
    }

    /// <summary>最小实例管理替身（仅覆盖解析所需行为）。</summary>
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

    /// <summary>最小模型仓储替身。</summary>
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
}
