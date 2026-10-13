using System.Diagnostics;
using LlamaLauncher.IntegrationTests.TestSupport;
using LlamaLauncher.Infrastructure.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Infrastructure;

public sealed class HealthServiceTests
{
    private sealed class MockServer : IDisposable
    {
        private readonly string? _originalEnable;
        private readonly Process _process;

        public MockServer(IDictionary<string, string?> environment)
        {
            Port = IntegrationFixture.GetFreePort();
            _originalEnable = Environment.GetEnvironmentVariable("LLAMALAUNCHER_TEST_ENABLE_HTTP");
            _environment = environment;
            Environment.SetEnvironmentVariable("LLAMALAUNCHER_TEST_ENABLE_HTTP", "1");
            foreach (var (name, value) in environment)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = IntegrationFixture.ChildExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(Port.ToString());
            _process = Process.Start(startInfo)!;
            WaitForListening();
        }

        private readonly IDictionary<string, string?> _environment;

        public int Port { get; }

        public string BaseUrl => $"http://127.0.0.1:{Port}";

        private void WaitForListening()
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    client.Connect("127.0.0.1", Port);
                    return;
                }
                catch (Exception)
                {
                    Thread.Sleep(100);
                }
            }

            throw new TimeoutException("模拟服务器未在预期时间内开始监听。");
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
            }

            _process.Dispose();
            Environment.SetEnvironmentVariable("LLAMALAUNCHER_TEST_ENABLE_HTTP", _originalEnable);
            foreach (var name in _environment.Keys)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    [Fact]
    public async Task HealthyServer_ReportsReady()
    {
        using var server = new MockServer(new Dictionary<string, string?>());
        using var service = new ServerHealthService(NullLogger<ServerHealthService>.Instance);

        var result = await service.CheckHealthAsync(server.BaseUrl, null);

        Assert.Equal(LlamaLauncher.Domain.HealthStatusKind.Ready, result.Status);
        Assert.Equal(200, result.HttpStatusCode);
    }

    [Fact]
    public async Task LoadingServer_ReportsLoading()
    {
        using var server = new MockServer(new Dictionary<string, string?>
        {
            ["LLAMALAUNCHER_TEST_HEALTH_MODE"] = "loading",
        });
        using var service = new ServerHealthService(NullLogger<ServerHealthService>.Instance);

        var result = await service.CheckHealthAsync(server.BaseUrl, null);

        Assert.Equal(LlamaLauncher.Domain.HealthStatusKind.Loading, result.Status);
        Assert.Equal(503, result.HttpStatusCode);
    }

    [Fact]
    public async Task ErrorServer_ReportsError()
    {
        using var server = new MockServer(new Dictionary<string, string?>
        {
            ["LLAMALAUNCHER_TEST_HEALTH_MODE"] = "error",
        });
        using var service = new ServerHealthService(NullLogger<ServerHealthService>.Instance);

        var result = await service.CheckHealthAsync(server.BaseUrl, null);

        Assert.Equal(LlamaLauncher.Domain.HealthStatusKind.Error, result.Status);
        Assert.Contains("error", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnreachableServer_ReportsUnknown()
    {
        using var service = new ServerHealthService(NullLogger<ServerHealthService>.Instance);
        var port = IntegrationFixture.GetFreePort();

        var result = await service.CheckHealthAsync($"http://127.0.0.1:{port}", null);

        Assert.Equal(LlamaLauncher.Domain.HealthStatusKind.Unknown, result.Status);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public async Task ModelsEndpoint_IsParsed()
    {
        using var server = new MockServer(new Dictionary<string, string?>());
        using var service = new ServerHealthService(NullLogger<ServerHealthService>.Instance);

        var (models, error) = await service.GetModelsAsync(server.BaseUrl, null);

        Assert.Null(error);
        Assert.NotNull(models);
        var model = Assert.Single(models!);
        Assert.Equal("test-model", model.Id);
    }

    [Fact]
    public async Task TestChat_ReturnsSuccess()
    {
        using var server = new MockServer(new Dictionary<string, string?>
        {
            ["LLAMALAUNCHER_TEST_CHAT_MODE"] = "json",
        });
        using var service = new ServerHealthService(NullLogger<ServerHealthService>.Instance);

        var (success, message) = await service.TestChatAsync(server.BaseUrl, null, "test-model");

        Assert.True(success, message);
    }

    [Fact]
    public async Task Props_ReturnsRawJson()
    {
        using var server = new MockServer(new Dictionary<string, string?>());
        using var service = new ServerHealthService(NullLogger<ServerHealthService>.Instance);

        var (json, error) = await service.GetPropsAsync(server.BaseUrl, null);

        Assert.Null(error);
        Assert.Contains("model_path", json);
    }
}
