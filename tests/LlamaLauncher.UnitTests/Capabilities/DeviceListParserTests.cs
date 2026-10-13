using LlamaLauncher.Application.Capabilities;
using LlamaLauncher.Domain;
using Xunit;

namespace LlamaLauncher.UnitTests.Capabilities;

public sealed class DeviceListParserTests
{
    [Fact]
    public void ParsesAvailableDevicesSection()
    {
        var text = """
            load_backend: loaded CUDA backend
            Available devices:
              CUDA0: NVIDIA GeForce RTX 4090 (24564 MiB, 24123 MiB free)
              Vulkan0: AMD Radeon RX 7900 XTX (24576 MiB, 24000 MiB free)
              CPU: CPU (AVX2 = 1)
            """;

        var devices = DeviceListParser.Parse(text);

        Assert.Equal(3, devices.Count);
        Assert.Equal(BackendKind.Cuda, devices[0].Backend);
        Assert.Equal(0, devices[0].Index);
        Assert.Equal("NVIDIA GeForce RTX 4090", devices[0].Name);
        Assert.Equal(BackendKind.Vulkan, devices[1].Backend);
        Assert.Equal(BackendKind.Cpu, devices[2].Backend);
    }

    [Fact]
    public void ParsesVerboseCudaDeviceLines()
    {
        var text = """
            ggml_cuda_init: found 1 CUDA devices:
              Device 0: NVIDIA GeForce RTX 3080, compute capability 8.6, VMM: yes
            """;

        var devices = DeviceListParser.Parse(text);

        var device = Assert.Single(devices);
        Assert.Equal(BackendKind.Cuda, device.Backend);
        Assert.Equal(0, device.Index);
        Assert.Equal("NVIDIA GeForce RTX 3080", device.Name);
    }

    [Fact]
    public void IgnoresUnrelatedLines()
    {
        var text = "error: invalid argument: --list-devices";
        Assert.Empty(DeviceListParser.Parse(text));
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(DeviceListParser.Parse(null));
        Assert.Empty(DeviceListParser.Parse(string.Empty));
    }
}
