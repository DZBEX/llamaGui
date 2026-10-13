namespace LlamaLauncher.Domain;

/// <summary>一次资源采样快照。无法获得的指标为 null 并标注来源。</summary>
public sealed class ResourceSnapshot
{
    public DateTimeOffset TimestampUtc { get; set; }

    public double? SystemCpuPercent { get; set; }

    public long? SystemMemoryTotalBytes { get; set; }

    public long? SystemMemoryUsedBytes { get; set; }

    public List<GpuMetric> Gpus { get; set; } = new();

    public List<ProcessMetrics> Processes { get; set; } = new();
}

/// <summary>GPU 指标（来源与有效性显式标注；无法可靠获取时为 null）。</summary>
public sealed class GpuMetric
{
    public string Name { get; set; } = string.Empty;

    /// <summary>指标来源（如 nvidia-smi、不可用）。</summary>
    public string Source { get; set; } = "不可用";

    public bool IsAvailable { get; set; }

    public double? UtilizationPercent { get; set; }

    public long? MemoryUsedBytes { get; set; }

    public long? MemoryTotalBytes { get; set; }

    public string? Note { get; set; }
}

/// <summary>单个实例进程的资源指标。</summary>
public sealed class ProcessMetrics
{
    public Guid InstanceId { get; set; }

    public string InstanceName { get; set; } = string.Empty;

    public int? Pid { get; set; }

    public double? CpuPercent { get; set; }

    public long? WorkingSetBytes { get; set; }

    public long? PrivateBytes { get; set; }

    public string? Note { get; set; }
}
