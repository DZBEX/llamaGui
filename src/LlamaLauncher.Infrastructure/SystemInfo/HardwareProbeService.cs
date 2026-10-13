using System.Globalization;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace LlamaLauncher.Infrastructure.SystemInfo;

/// <summary>
/// 硬件探测：OS/CPU 通过注册表，内存通过 GlobalMemoryStatusEx，
/// 显示适配器通过 WMI（失败时降级为“不可用”），nvidia-smi 为可选增强来源。
/// </summary>
public sealed class HardwareProbeService : IHardwareProbeService
{
    private readonly IProbeProcessRunner _processRunner;
    private readonly ILogger<HardwareProbeService> _logger;
    private string? _nvidiaSmiPath;
    private bool _nvidiaLookupDone;

    public HardwareProbeService(IProbeProcessRunner processRunner, ILogger<HardwareProbeService> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<SystemHardwareInfo> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var (osName, osVersion, osBuild) = ReadOsInfo();
        var cpuName = ReadCpuName();
        var memory = NativeSystemMetrics.GetMemoryStatus();
        var adapters = ReadDisplayAdapters();
        var nvidiaMetrics = await SampleNvidiaAsync(cancellationToken).ConfigureAwait(false);

        return new SystemHardwareInfo(
            osName,
            osVersion,
            osBuild,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            cpuName,
            Environment.ProcessorCount,
            memory?.Total,
            adapters,
            nvidiaMetrics.Count > 0,
            nvidiaMetrics);
    }

    public async Task<IReadOnlyList<GpuMetric>> SampleNvidiaAsync(CancellationToken cancellationToken = default)
    {
        var path = ResolveNvidiaSmiPath();
        if (path is null)
        {
            return Array.Empty<GpuMetric>();
        }

        try
        {
            var result = await _processRunner.RunAsync(
                path,
                new[]
                {
                    "--query-gpu=name,utilization.gpu,memory.used,memory.total,driver_version",
                    "--format=csv,noheader,nounits",
                },
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return Array.Empty<GpuMetric>();
            }

            var metrics = new List<GpuMetric>();
            foreach (var line in result.StandardOutput.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length < 5)
                {
                    continue;
                }

                metrics.Add(new GpuMetric
                {
                    Name = parts[0],
                    Source = "nvidia-smi",
                    IsAvailable = true,
                    UtilizationPercent = ParseDouble(parts[1]),
                    MemoryUsedBytes = ToBytes(ParseDouble(parts[2])),
                    MemoryTotalBytes = ToBytes(ParseDouble(parts[3])),
                    Note = "驱动 " + parts[4],
                });
            }

            return metrics;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "nvidia-smi 采样失败");
            return Array.Empty<GpuMetric>();
        }
    }

    private string? ResolveNvidiaSmiPath()
    {
        if (_nvidiaLookupDone)
        {
            return _nvidiaSmiPath;
        }

        _nvidiaLookupDone = true;
        try
        {
            var candidates = new List<string>();
            var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(Path.Combine(dir.Trim(), "nvidia-smi.exe"));
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            candidates.Add(Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"));
            if (Environment.GetEnvironmentVariable("SystemRoot") is { } systemRoot)
            {
                candidates.Add(Path.Combine(systemRoot, "System32", "nvidia-smi.exe"));
            }

            _nvidiaSmiPath = candidates.FirstOrDefault(File.Exists);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "定位 nvidia-smi 失败");
            _nvidiaSmiPath = null;
        }

        return _nvidiaSmiPath;
    }

    private static (string Name, string Version, string Build) ReadOsInfo()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var productName = key?.GetValue("ProductName") as string ?? "Windows";
            var displayVersion = key?.GetValue("DisplayVersion") as string
                ?? key?.GetValue("ReleaseId") as string
                ?? string.Empty;
            var build = key?.GetValue("CurrentBuildNumber") as string ?? string.Empty;
            var ubr = key?.GetValue("UBR")?.ToString();
            if (!string.IsNullOrEmpty(ubr))
            {
                build += "." + ubr;
            }

            return (productName, displayVersion, build);
        }
        catch (Exception)
        {
            return (Environment.OSVersion.VersionString, string.Empty, string.Empty);
        }
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string ?? "未知 CPU";
        }
        catch (Exception)
        {
            return "未知 CPU";
        }
    }

    private List<GpuAdapterInfo> ReadDisplayAdapters()
    {
        var adapters = new List<GpuAdapterInfo>();
        if (!OperatingSystem.IsWindows())
        {
            return adapters;
        }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, AdapterCompatibility, DriverVersion, AdapterRAM FROM Win32_VideoController");
            foreach (var item in searcher.Get())
            {
                using var obj = item;
                var name = obj["Name"] as string ?? "未知适配器";
                var vendor = obj["AdapterCompatibility"] as string;
                var driver = obj["DriverVersion"] as string;
                uint? ram = obj["AdapterRAM"] is uint value ? value : null;
                adapters.Add(new GpuAdapterInfo(
                    name,
                    vendor,
                    driver,
                    ram,
                    "WMI Win32_VideoController（AdapterRAM 为粗略值，部分驱动不规范）"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WMI 查询显示适配器失败");
        }

        return adapters;
    }

    private static double? ParseDouble(string text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? ToBytes(double? megabytes)
        => megabytes is null ? null : (long)(megabytes.Value * 1024 * 1024);
}
