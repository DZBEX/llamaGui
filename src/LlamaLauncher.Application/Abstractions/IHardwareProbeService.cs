using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Abstractions;

/// <summary>Windows 枚举到的显示适配器信息（系统层面，不代表 llama.cpp 可用设备）。</summary>
public sealed record GpuAdapterInfo(
    string Name,
    string? Vendor,
    string? DriverVersion,
    long? ReportedAdapterRamBytes,
    string Source);

/// <summary>系统硬件信息。</summary>
public sealed record SystemHardwareInfo(
    string OsName,
    string OsVersion,
    string OsBuild,
    string Architecture,
    string CpuName,
    int LogicalProcessors,
    long? TotalPhysicalMemoryBytes,
    IReadOnlyList<GpuAdapterInfo> DisplayAdapters,
    bool NvidiaSmiAvailable,
    IReadOnlyList<GpuMetric> NvidiaMetrics);

/// <summary>硬件探测：OS/CPU/内存/显示适配器；nvidia-smi 为可选增强来源。</summary>
public interface IHardwareProbeService
{
    Task<SystemHardwareInfo> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>通过 nvidia-smi 采样 GPU 指标；不可用时返回空列表（不伪造数值）。</summary>
    Task<IReadOnlyList<GpuMetric>> SampleNvidiaAsync(CancellationToken cancellationToken = default);
}
