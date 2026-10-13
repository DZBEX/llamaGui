using System.Text.RegularExpressions;
using LlamaLauncher.Domain;

namespace LlamaLauncher.Application.Capabilities;

/// <summary>解析 --list-devices / 设备初始化日志中的可用推理设备。</summary>
public static partial class DeviceListParser
{
    [GeneratedRegex(@"^\s*(?<backend>[A-Za-z]+)\s*(?<index>\d+)?\s*:\s*(?<rest>.+)$")]
    private static partial Regex BackendLinePattern();

    [GeneratedRegex(@"^\s*Device\s+(?<index>\d+)\s*:\s*(?<rest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceLinePattern();

    public static List<DetectedDevice> Parse(string? text)
    {
        var devices = new List<DetectedDevice>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return devices;
        }

        var lines = text.Split('\n');
        var inAvailableSection = text.Contains("Available devices", StringComparison.OrdinalIgnoreCase);
        var sectionStarted = !inAvailableSection;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (inAvailableSection && !sectionStarted)
            {
                if (line.Contains("Available devices", StringComparison.OrdinalIgnoreCase))
                {
                    sectionStarted = true;
                }

                continue;
            }

            var backendMatch = BackendLinePattern().Match(line);
            if (backendMatch.Success)
            {
                var backendText = backendMatch.Groups["backend"].Value;
                var backend = MapBackend(backendText);
                if (backend != BackendKind.Unknown)
                {
                    int? index = backendMatch.Groups["index"].Success
                        ? int.Parse(backendMatch.Groups["index"].Value)
                        : null;
                    var rest = backendMatch.Groups["rest"].Value.Trim();
                    devices.Add(new DetectedDevice
                    {
                        Index = index,
                        Name = TrimDeviceName(rest),
                        Backend = backend,
                        RawLine = line.Trim(),
                    });
                    continue;
                }
            }

            var deviceMatch = DeviceLinePattern().Match(line);
            if (deviceMatch.Success)
            {
                var rest = deviceMatch.Groups["rest"].Value.Trim();
                devices.Add(new DetectedDevice
                {
                    Index = int.Parse(deviceMatch.Groups["index"].Value),
                    Name = TrimDeviceName(rest),
                    Backend = InferBackendFromName(rest),
                    RawLine = line.Trim(),
                });
            }
        }

        return devices
            .GroupBy(d => (d.Backend, d.Index, d.Name))
            .Select(g => g.First())
            .ToList();
    }

    private static BackendKind MapBackend(string text) => text.ToUpperInvariant() switch
    {
        "CUDA" => BackendKind.Cuda,
        "VULKAN" => BackendKind.Vulkan,
        "SYCL" => BackendKind.Sycl,
        "ROCM" or "HIP" => BackendKind.Rocm,
        "OPENVINO" => BackendKind.OpenVino,
        "CPU" => BackendKind.Cpu,
        "METAL" or "OPENCL" => BackendKind.Other,
        _ => BackendKind.Unknown,
    };

    private static BackendKind InferBackendFromName(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            return BackendKind.Cuda;
        }

        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return BackendKind.Rocm;
        }

        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return BackendKind.Unknown;
        }

        return BackendKind.Unknown;
    }

    private static string TrimDeviceName(string rest)
    {
        // 去除 “(24564 MiB, 24123 MiB free)” 之类的尾注，保留设备名
        var index = rest.IndexOf(" (", StringComparison.Ordinal);
        var name = index > 0 ? rest[..index] : rest;
        var commaIndex = name.IndexOf(", compute capability", StringComparison.OrdinalIgnoreCase);
        if (commaIndex > 0)
        {
            name = name[..commaIndex];
        }

        return name.Trim();
    }
}
