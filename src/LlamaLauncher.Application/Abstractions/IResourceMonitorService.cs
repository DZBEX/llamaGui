using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Abstractions;

/// <summary>被跟踪实例（用于进程 CPU/内存采样）。</summary>
public sealed record TrackedInstance(Guid InstanceId, string Name, int? ProcessId, string? ExecutablePath);

/// <summary>后台资源采样服务（防重叠、可暂停、周期可配置）。</summary>
public interface IResourceMonitorService : IAsyncDisposable
{
    /// <summary>采样周期（默认 2 秒）。</summary>
    TimeSpan Interval { get; set; }

    ResourceSnapshot? Latest { get; }

    /// <summary>每次采样完成后触发（后台线程）。</summary>
    event Action<ResourceSnapshot>? SnapshotAvailable;

    void SetTrackedInstances(IReadOnlyList<TrackedInstance> instances);

    void Start();

    void Stop();

    /// <summary>立即执行一次采样（不改变运行状态）。</summary>
    Task<ResourceSnapshot> SampleOnceAsync(CancellationToken cancellationToken = default);
}
