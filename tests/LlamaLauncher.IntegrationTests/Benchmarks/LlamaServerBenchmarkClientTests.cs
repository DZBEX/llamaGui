using System.Net;
using System.Net.Sockets;
using System.Text;
using LlamaLauncher.Application.Benchmarks;
using LlamaLauncher.Infrastructure.Http;
using LlamaLauncher.IntegrationTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Benchmarks;

/// <summary>基准 HTTP 客户端：真实 SSE 流解析、请求参数、错误分类与超时。</summary>
public sealed class LlamaServerBenchmarkClientTests
{
    [Fact]
    public async Task RunOnce_ParsesStreamingTimingsAndSendsExpectedRequest()
    {
        var sse = string.Join(
            string.Empty,
            "data: {\"content\":\"Hello\"}\n\n",
            "data: {\"content\":\" world\"}\n\n",
            "data: {\"content\":\"\",\"stop\":true,\"timings\":{\"prompt_n\":512,\"prompt_ms\":100.0," +
            "\"prompt_per_second\":5120.0,\"predicted_n\":2,\"predicted_ms\":50.0," +
            "\"predicted_per_second\":40.0}}\n\n");
        using var server = new LocalJsonHttpServer(_ => (200, sse));
        using var client = CreateClient();

        var result = await client.RunOnceAsync(new BenchmarkCallRequest(
            server.Url(string.Empty),
            "api-key-1",
            "PROMPT-TEXT",
            128,
            TimeSpan.FromSeconds(30)));

        Assert.True(result.Success, result.Error);
        Assert.Equal(512, result.PromptTokens);
        Assert.Equal(100.0, result.PromptMs);
        Assert.Equal(5120.0, result.PromptTokensPerSecond);
        Assert.Equal(2, result.GeneratedTokens);
        Assert.Equal(40.0, result.GeneratedTokensPerSecond);
        Assert.NotNull(result.TimeToFirstTokenMs);
        Assert.NotNull(result.TotalMs);
        Assert.Null(result.Error);

        var request = Assert.Single(server.Requests);
        Assert.Contains("POST /completion", request);
        Assert.Contains("Bearer api-key-1", request);
        Assert.Contains("\"prompt\":\"PROMPT-TEXT\"", request);
        Assert.Contains("\"n_predict\":128", request);
        Assert.Contains("\"cache_prompt\":false", request);
        Assert.Contains("\"stream\":true", request);
        Assert.Contains("\"temperature\":0", request);
    }

    [Fact]
    public async Task RunOnce_ClassifiesHttpErrorWithServerMessage()
    {
        using var server = new LocalJsonHttpServer(
            _ => (404, "{\"error\":{\"code\":404,\"message\":\"File Not Found\"}}"));
        using var client = CreateClient();

        var result = await client.RunOnceAsync(new BenchmarkCallRequest(
            server.Url(string.Empty),
            null,
            "p",
            8,
            TimeSpan.FromSeconds(10)));

        Assert.False(result.Success);
        Assert.Equal("File Not Found", result.Error);
        Assert.NotNull(result.TotalMs);
    }

    [Fact]
    public async Task RunOnce_RecognizesServerErrorFrame()
    {
        using var server = new LocalJsonHttpServer(
            _ => (200, "data: {\"error\":{\"message\":\"context size exceeded\"}}\n\n"));
        using var client = CreateClient();

        var result = await client.RunOnceAsync(new BenchmarkCallRequest(
            server.Url(string.Empty),
            null,
            "p",
            8,
            TimeSpan.FromSeconds(10)));

        Assert.False(result.Success);
        Assert.Equal("context size exceeded", result.Error);
    }

    [Fact]
    public async Task RunOnce_WithoutTimings_FallsBackToClientMeasurements()
    {
        var sse = string.Join(
            string.Empty,
            "data: {\"content\":\"a\"}\n\n",
            "data: {\"content\":\"b\"}\n\n",
            "data: {\"content\":\"c\"}\n\n",
            "data: {\"content\":\"\",\"stop\":true}\n\n");
        using var server = new LocalJsonHttpServer(_ => (200, sse));
        using var client = CreateClient();

        var result = await client.RunOnceAsync(new BenchmarkCallRequest(
            server.Url(string.Empty),
            null,
            "p",
            8,
            TimeSpan.FromSeconds(10)));

        Assert.True(result.Success, result.Error);
        Assert.Null(result.PromptTokens);
        Assert.Equal(3, result.GeneratedTokens);
        Assert.NotNull(result.TimeToFirstTokenMs);
        Assert.NotNull(result.TotalMs);
        Assert.True(result.GeneratedTokensPerSecond is null or > 0);
    }

    [Fact]
    public async Task RunOnce_TimesOutWhenStreamStalls()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serveTask = Task.Run(async () =>
        {
            try
            {
                using var socket = await listener.AcceptSocketAsync(serverCts.Token);
                var requestBuffer = new byte[8192];
                await socket.ReceiveAsync(requestBuffer, serverCts.Token);
                var header =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/event-stream\r\n" +
                    "Transfer-Encoding: chunked\r\n\r\n";
                await socket.SendAsync(Encoding.ASCII.GetBytes(header), serverCts.Token);
                // 只发送响应头，不发送任何数据体（模拟服务卡住）。
                await Task.Delay(TimeSpan.FromSeconds(10), serverCts.Token);
            }
            catch (Exception)
            {
                // 连接在测试结束后关闭属预期行为。
            }
        });

        using var client = CreateClient();
        var result = await client.RunOnceAsync(new BenchmarkCallRequest(
            $"http://127.0.0.1:{port}",
            null,
            "p",
            8,
            TimeSpan.FromMilliseconds(500)));

        Assert.False(result.Success);
        Assert.Contains("超过时限", result.Error);
        listener.Stop();
        await Task.WhenAny(serveTask, Task.Delay(1000));
    }

    private static LlamaServerBenchmarkClient CreateClient()
        => new(TimeProvider.System, NullLogger<LlamaServerBenchmarkClient>.Instance);
}
