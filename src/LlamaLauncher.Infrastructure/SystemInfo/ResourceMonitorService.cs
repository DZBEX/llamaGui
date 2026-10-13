using System.Collections.Concurrent;
using System.Diagnostics;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.SystemInfo;

/// <summary>
/// 后台资源采样：系统 CPU/内存 + 各实例进程 CPU/工作集/私有内存 + 可选 nvidia-smi 指标。
/// 单循环防重叠；无法获取的指标保持 null 并标注来源；采样周期可配置（默认 2 秒）。
/// </summary>
public sealed class ResourceMonitorService : IResourceMonitorService
{
    private readonly IHardwareProbeService _hardware;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ResourceMonitorService> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ConcurrentDictionary<Guid, TrackedInstance> _tracked = new();
    private readonly ConcurrentDictionary<int, ProcessSampleState> _processStates = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private (ulong Idle, ulong Kernel, ulong User)? _lastSystemTimes;

    public ResourceMonitorService(
        IHardwareProbeService hardware,
        TimeProvider timeProvider,
        ILogger<ResourceMonitorService> logger)
    {
        _hardware = hardware;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);

    public ResourceSnapshot? Latest { get; private set; }

    public event Action<ResourceSnapshot>? SnapshotAvailable;

    public void SetTrackedInstances(IReadOnlyList<TrackedInstance> instances)
    {
        _tracked.Clear();
        foreach (var instance in instances)
        {
            _tracked[instance.InstanceId] = instance;
        }
    }

    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        _loopTask = Task.Run(() => LoopAsync(_loopCts.Token));
        _logger.LogInformation("资源监控已启动，采样周期 {Interval}", Interval);
    }

    public void Stop()
    {
        _loopCts?.Cancel();
        _loopTask = null;
        _loopCts?.Dispose();
        _loopCts = null;
        _lastSystemTimes = null;
        _logger.LogInformation("资源监控已停止");
    }

    public async Task<ResourceSnapshot> SampleOnceAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = new ResourceSnapshot { TimestampUtc = _timeProvider.GetUtcNow() };

        // 系统 CPU：两次 GetSystemTimes 的差值（第一次采样无法得到百分比）
        var times = NativeSystemMetrics.GetSystemTimes();
        if (times is { } current && _lastSystemTimes is { } previous)
        {
            var idleDelta = current.Idle - previous.Idle;
            var kernelDelta = current.Kernel - previous.Kernel;
            var userDelta = current.User - previous.User;
            var total = kernelDelta + userDelta;
            if (total > 0 && idleDelta <= total)
            {
                snapshot.SystemCpuPercent = Math.Round(100.0 * (total - idleDelta) / total, 1);
            }
        }

        _lastSystemTimes = times;

        var memory = NativeSystemMetrics.GetMemoryStatus();
        if (memory is { } mem)
        {
            snapshot.SystemMemoryTotalBytes = mem.Total;
            snapshot.SystemMemoryUsedBytes = mem.Total - mem.Available;
        }

        foreach (var tracked in _tracked.Values)
        {
            snapshot.Processes.Add(SampleProcess(tracked));
        }

        try
        {
            var gpus = await _hardware.SampleNvidiaAsync(cancellationToken).ConfigureAwait(false);
            snapshot.Gpus.AddRange(gpus);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GPU 采样失败");
        }

        if (snapshot.Gpus.Count == 0)
        {
            snapshot.Gpus.Add(new GpuMetric
            {
                Name = "（无可用 GPU 指标来源）",
                Source = "不可用",
                IsAvailable = false,
                Note = "未检测到 nvidia-smi 或系统无 NVIDIA 设备；其他厂商 GPU 的利用率暂不提供。",
            });
        }

        Latest = snapshot;
        return snapshot;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _shutdownCts.Dispose();
        _loopCts?.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var now = _timeProvider.GetUtcNow();
                if (Latest is { } latest && now - latest.TimestampUtc < Interval)
                {
                    continue;
                }

                try
                {
                    var snapshot = await SampleOnceAsync(cancellationToken).ConfigureAwait(false);
                    SnapshotAvailable?.Invoke(snapshot);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "资源采样失败");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    private ProcessMetrics SampleProcess(TrackedInstance tracked)
    {
        var metrics = new ProcessMetrics
        {
            InstanceId = tracked.InstanceId,
            InstanceName = tracked.Name,
            Pid = tracked.ProcessId,
        };

        if (tracked.ProcessId is not { } pid || pid <= 0)
        {
            metrics.Note = "实例未运行。";
            return metrics;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                metrics.Note = "进程已退出。";
                return metrics;
            }

            metrics.WorkingSetBytes = process.WorkingSet64;
            try
            {
                metrics.PrivateBytes = process.PrivateMemorySize64;
            }
            catch (Exception)
            {
                metrics.PrivateBytes = null;
            }

            TimeSpan totalCpu;
            try
            {
                totalCpu = process.TotalProcessorTime;
            }
            catch (Exception)
            {
                metrics.Note = "无法读取进程 CPU 时间。";
                return metrics;
            }

            var state = _processStates.GetOrAdd(pid, _ => new ProcessSampleState());
            var now = DateTime.UtcNow;
            lock (state.Gate)
            {
                if (state.LastCpuTime is { } lastCpu && state.LastSampleUtc is { } lastAt)
                {
                    var elapsedMs = (now - lastAt).TotalMilliseconds;
                    var cpuMs = (totalCpu - lastCpu).TotalMilliseconds;
                    if (elapsedMs > 50 && cpuMs >= 0)
                    {
                        metrics.CpuPercent = Math.Round(
                            Math.Min(100.0 * cpuMs / (elapsedMs * Environment.ProcessorCount), 100.0),
                            1);
                    }
                }

                state.LastCpuTime = totalCpu;
                state.LastSampleUtc = now;
            }
        }
        catch (ArgumentException)
        {
            metrics.Note = "进程不存在（可能已退出）。";
        }
        catch (Exception ex)
        {
            metrics.Note = "读取进程指标失败：" + ex.Message;
        }

        return metrics;
    }

    private sealed class ProcessSampleState
    {
        public object Gate { get; } = new();

        public TimeSpan? LastCpuTime { get; set; }

        public DateTime? LastSampleUtc { get; set; }
    }
}
