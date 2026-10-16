using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlamaLauncher.Application.Benchmarks;
using LlamaLauncher.Application.Chat;
using LlamaLauncher.Contracts;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Http;

/// <summary>
/// 面向 llama-server 的基准调用客户端：
/// - POST /completion（stream=true，cache_prompt=false，贪心采样），仅测量不计内容质量；
/// - 客户端实测首 token 延迟与总耗时；优先采用服务端 timings（prompt/predicted 速度）；
/// - 服务端未返回 timings 时，以内容帧数近似生成 token 数并在结果中如实使用。
/// </summary>
public sealed class LlamaServerBenchmarkClient : IBenchmarkClient, IDisposable
{
    private readonly HttpClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LlamaServerBenchmarkClient> _logger;

    public LlamaServerBenchmarkClient(TimeProvider timeProvider, ILogger<LlamaServerBenchmarkClient> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<BenchmarkCallResult> RunOnceAsync(
        BenchmarkCallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = BuildRequest(request);
        var startTimestamp = _timeProvider.GetTimestamp();

        HttpResponseMessage? response;
        try
        {
            response = await _client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return BenchmarkCallResult.Fail("连接服务超时。", ElapsedMs(startTimestamp));
        }
        catch (HttpRequestException ex)
        {
            return BenchmarkCallResult.Fail(DescribeConnectError(ex), ElapsedMs(startTimestamp));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                return BenchmarkCallResult.Fail(
                    ClassifyHttp((int)response.StatusCode, body),
                    ElapsedMs(startTimestamp));
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (request.Timeout > TimeSpan.Zero)
            {
                timeoutCts.CancelAfter(request.Timeout);
            }

            try
            {
                var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
                return await ReadStreamAsync(stream, startTimestamp, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return BenchmarkCallResult.Fail(
                    $"单次请求超过时限（{request.Timeout.TotalMinutes:0.#} 分钟）。",
                    ElapsedMs(startTimestamp));
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "读取基准响应流失败");
                return BenchmarkCallResult.Fail("读取响应流失败：" + ex.Message, ElapsedMs(startTimestamp));
            }
            catch (HttpRequestException ex)
            {
                return BenchmarkCallResult.Fail("连接中断：" + ex.Message, ElapsedMs(startTimestamp));
            }
        }
    }

    public void Dispose() => _client.Dispose();

    private async Task<BenchmarkCallResult> ReadStreamAsync(
        Stream stream,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var parser = new SseFrameParser();
        var buffer = new char[4096];
        CompletionTimings? timings = null;
        double? timeToFirstTokenMs = null;
        var contentFrames = 0;
        var sawStop = false;

        while (!sawStop)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            foreach (var payload in parser.Feed(new string(buffer, 0, read)))
            {
                var frame = CompletionStreamFrameParser.Parse(payload);
                if (frame is null)
                {
                    continue;
                }

                if (frame.Error is { Length: > 0 } error)
                {
                    return BenchmarkCallResult.Fail(error, ElapsedMs(startTimestamp), timeToFirstTokenMs);
                }

                if (frame.Timings is { } serverTimings)
                {
                    timings = serverTimings;
                }

                if (frame.HasContent)
                {
                    contentFrames++;
                    timeToFirstTokenMs ??= ElapsedMs(startTimestamp);
                }

                if (frame.IsStop)
                {
                    sawStop = true;
                    break;
                }
            }
        }

        var totalMs = ElapsedMs(startTimestamp);
        if (!sawStop && contentFrames == 0 && timings is null)
        {
            return BenchmarkCallResult.Fail("服务在返回任何数据前断开了连接。", totalMs);
        }

        return BuildResult(timings, timeToFirstTokenMs, totalMs, contentFrames);
    }

    private static BenchmarkCallResult BuildResult(
        CompletionTimings? timings,
        double? timeToFirstTokenMs,
        double totalMs,
        int contentFrames)
    {
        if (timings is not null)
        {
            var generated = timings.PredictedTokens ?? (contentFrames > 0 ? contentFrames : null);
            var generatedMs = timings.PredictedMs;
            var generatedSpeed = timings.PredictedTokensPerSecond;
            if (generatedSpeed is null && generated is { } count && generatedMs is { } ms && ms > 0)
            {
                generatedSpeed = count / ms * 1000.0;
            }

            return new BenchmarkCallResult(
                true,
                timings.PromptTokens,
                timings.PromptMs,
                timings.PromptTokensPerSecond,
                generated,
                generatedMs,
                generatedSpeed,
                timeToFirstTokenMs,
                totalMs,
                null);
        }

        // 无服务端 timings（旧版本）：用内容帧数量近似生成 token 数，速度以客户端实测计算。
        if (timeToFirstTokenMs is { } firstToken && contentFrames > 0)
        {
            var generatedMs = Math.Max(0, totalMs - firstToken);
            double? generatedSpeed = generatedMs > 0 ? contentFrames / generatedMs * 1000.0 : null;
            return new BenchmarkCallResult(
                true,
                null,
                null,
                null,
                contentFrames,
                generatedMs,
                generatedSpeed,
                firstToken,
                totalMs,
                null);
        }

        return new BenchmarkCallResult(true, null, null, null, null, null, null, timeToFirstTokenMs, totalMs, null);
    }

    private static HttpRequestMessage BuildRequest(BenchmarkCallRequest request)
    {
        var payload = new JsonObject
        {
            ["prompt"] = request.Prompt,
            ["n_predict"] = request.GenerateTokens,
            ["temperature"] = 0,
            ["top_k"] = 1,
            ["cache_prompt"] = false,
            ["stream"] = true,
        };

        var message = new HttpRequestMessage(
            HttpMethod.Post,
            request.BaseUrl.TrimEnd('/') + "/completion")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(request.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        }

        return message;
    }

    private double ElapsedMs(long startTimestamp)
        => _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds;

    private static async Task<string> SafeReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string DescribeConnectError(HttpRequestException ex)
    {
        if (ex.InnerException is SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "连接被拒绝：服务未运行或端口不正确。",
                SocketError.TimedOut => "连接超时。",
                _ => "无法连接服务：" + ex.Message,
            };
        }

        return "无法连接服务：" + ex.Message;
    }

    private static string ClassifyHttp(int statusCode, string body)
    {
        var serverMessage = ExtractServerMessage(body);
        return statusCode switch
        {
            400 => serverMessage ?? "请求被拒绝（HTTP 400），请确认服务版本支持 /completion 端点。",
            401 or 403 => serverMessage ?? "API Key 无效或缺失。",
            404 => serverMessage ?? "接口不存在（HTTP 404），请确认服务版本是否支持 /completion。",
            429 => serverMessage ?? "请求过于频繁（HTTP 429）。",
            >= 500 => serverMessage ?? $"服务内部错误（HTTP {statusCode}）。",
            _ => serverMessage ?? $"服务返回 HTTP {statusCode}。",
        };
    }

    private static string? ExtractServerMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ApiErrorResponse>(body);
            if (parsed?.Error?.Message is { Length: > 0 } message)
            {
                return message;
            }
        }
        catch (JsonException)
        {
        }

        return body.Length <= 300 ? body : body[..300];
    }
}
