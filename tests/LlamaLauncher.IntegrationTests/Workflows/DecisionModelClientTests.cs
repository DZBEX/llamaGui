using System.Text.Json.Nodes;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Infrastructure.Http;
using LlamaLauncher.IntegrationTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Workflows;

/// <summary>决策模型 HTTP 客户端：请求构建、错误分类、超时、取消与大小限制（模拟服务）。</summary>
public sealed class DecisionModelClientTests
{
    private static DecisionCallRequest CreateRequest(string baseUrl, int maxBytes = 1_048_576, int timeoutMs = 30_000)
        => new()
        {
            BaseUrl = baseUrl,
            ApiKey = "secret-key",
            State = JsonValue.Create("客户要求退款"),
            Questions = new JsonObject
            {
                ["route"] = new JsonObject
                {
                    ["type"] = "choice",
                    ["instructions"] = "哪个部门？",
                    ["criteria"] = new JsonObject { ["billing"] = "账单", ["other"] = null },
                },
            },
            Timeout = TimeSpan.FromMilliseconds(timeoutMs),
            MaxResponseBytes = maxBytes,
        };

    private const string ValidResponse = """
        {"model":"test","answers":{"route":{"type":"choice","choice":"billing","probabilities":{"billing":0.9,"other":0.1},"confidence":0.8}},"usage":{"input_tokens":12,"output_tokens":0}}
        """;

    [Fact]
    public async Task 成功调用并解析答案()
    {
        using var server = new LocalJsonHttpServer(_ => (200, ValidResponse));
        using var client = new DecisionModelClient(NullLogger<DecisionModelClient>.Instance);

        var result = await client.CallAsync(CreateRequest(server.Url()));

        Assert.True(result.Success);
        Assert.NotNull(result.Response);
        Assert.Equal("billing", result.Response!["answers"]!["route"]!["choice"]!.GetValue<string>());
        Assert.NotNull(result.RawJson);

        var request = server.Requests.Single();
        Assert.Contains("POST /v1/systemone", request);
        Assert.Contains("Bearer secret-key", request);
        Assert.Contains("客户要求退款", request);
        Assert.Contains("哪个部门", request);
    }

    [Fact]
    public async Task HTTP错误分类并附带状态码()
    {
        using var server = new LocalJsonHttpServer(_ => (500, """{"error":{"message":"boom"}}"""));
        using var client = new DecisionModelClient(NullLogger<DecisionModelClient>.Instance);

        var result = await client.CallAsync(CreateRequest(server.Url()));

        Assert.False(result.Success);
        Assert.Equal(DecisionErrorKind.HttpError, result.Error!.Kind);
        Assert.Equal(500, result.Error.HttpStatusCode);
    }

    [Fact]
    public async Task 未提供systemone端点时给出升级提示()
    {
        using var server = new LocalJsonHttpServer(_ => (404, "not found"));
        using var client = new DecisionModelClient(NullLogger<DecisionModelClient>.Instance);

        var result = await client.CallAsync(CreateRequest(server.Url()));

        Assert.False(result.Success);
        Assert.Equal(404, result.Error!.HttpStatusCode);
        Assert.NotNull(result.Error.Detail);
        Assert.Contains("llama.cpp", result.Error.Detail!);
    }

    [Fact]
    public async Task 无效JSON与缺少答案被识别()
    {
        using (var server = new LocalJsonHttpServer(_ => (200, "{ not json")))
        using (var client = new DecisionModelClient(NullLogger<DecisionModelClient>.Instance))
        {
            var result = await client.CallAsync(CreateRequest(server.Url()));
            Assert.False(result.Success);
            Assert.Equal(DecisionErrorKind.InvalidResponse, result.Error!.Kind);
        }

        using (var server = new LocalJsonHttpServer(_ => (200, """{"model":"x"}""")))
        using (var client = new DecisionModelClient(NullLogger<DecisionModelClient>.Instance))
        {
            var result = await client.CallAsync(CreateRequest(server.Url()));
            Assert.False(result.Success);
            Assert.Equal(DecisionErrorKind.InvalidResponse, result.Error!.Kind);
            Assert.Contains("answers", result.Error.Message);
        }
    }

    [Fact]
    public async Task 超大响应被拒绝()
    {
        var big = """{"answers":{"a":" """ + new string('x', 4096) + """ "}}""";
        using var server = new LocalJsonHttpServer(_ => (200, big));
        using var client = new DecisionModelClient(NullLogger<DecisionModelClient>.Instance);

        var result = await client.CallAsync(CreateRequest(server.Url(), maxBytes: 1024));

        Assert.False(result.Success);
        Assert.Equal(DecisionErrorKind.ResponseTooLarge, result.Error!.Kind);
    }

    [Fact]
    public async Task 超时被分类()
    {
        using var server = new LocalJsonHttpServer(_ => (200, ValidResponse), delayMs: 3000);
        using var client = new DecisionModelClient(NullLogger<DecisionModelClient>.Instance);

        var result = await client.CallAsync(CreateRequest(server.Url(), timeoutMs: 300));

        Assert.False(result.Success);
        Assert.Equal(DecisionErrorKind.Timeout, result.Error!.Kind);
    }

    [Fact]
    public async Task 连接失败被分类()
    {
        // 用抛连接异常的处理器模拟“服务未启动/端口不可达”，避免依赖本机端口行为。
        using var client = new DecisionModelClient(
            NullLogger<DecisionModelClient>.Instance,
            new ThrowingHandler(new HttpRequestException("Connection refused (127.0.0.1:19001)")));
        var result = await client.CallAsync(CreateRequest("http://127.0.0.1:19001", timeoutMs: 2000));

        Assert.False(result.Success);
        Assert.Equal(DecisionErrorKind.ConnectionFailed, result.Error!.Kind);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(_exception);
    }

    [Fact]
    public async Task 用户取消抛出取消异常()
    {
        using var server = new LocalJsonHttpServer(_ => (200, ValidResponse), delayMs: 5000);
        using var client = new DecisionModelClient(NullLogger<DecisionModelClient>.Instance);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(150);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CallAsync(CreateRequest(server.Url()), cts.Token));
    }
}
