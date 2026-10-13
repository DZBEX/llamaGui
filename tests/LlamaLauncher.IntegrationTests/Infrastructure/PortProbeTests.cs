using System.Net;
using System.Net.Sockets;
using LlamaLauncher.IntegrationTests.TestSupport;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Infrastructure;

public sealed class PortProbeTests
{
    [Fact]
    public async Task FreePort_IsAvailable()
    {
        var probe = new LlamaLauncher.Infrastructure.Net.TcpPortProbe();
        var port = IntegrationFixture.GetFreePort();

        var result = await probe.CheckAsync("127.0.0.1", port);

        Assert.True(result.IsAvailable);
    }

    [Fact]
    public async Task OccupiedPort_IsReportedInUse()
    {
        var probe = new LlamaLauncher.Infrastructure.Net.TcpPortProbe();
        var port = IntegrationFixture.GetFreePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        var result = await probe.CheckAsync("127.0.0.1", port);

        Assert.False(result.IsAvailable);
        Assert.NotNull(result.Detail);
        Assert.Contains(port.ToString(), result.Detail);
    }

    [Fact]
    public async Task InvalidPortRange_IsReported()
    {
        var probe = new LlamaLauncher.Infrastructure.Net.TcpPortProbe();
        var result = await probe.CheckAsync("127.0.0.1", 70000);
        Assert.False(result.IsAvailable);
    }
}
