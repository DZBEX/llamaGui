using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlamaLauncher.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Http;

/// <summary>
/// /v1/systemone 决策模型 HTTP 客户端：
/// 非流式请求、超时/取消、响应大小限制、JSON 解析与结构化错误分类。
/// </summary>
public sealed class DecisionModelClient : IModelDecisionClient, IDisposable
{
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _client;
    private readonly ILogger<DecisionModelClient> _logger;

    public DecisionModelClient(ILogger<DecisionModelClient> logger, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        var effectiveHandler = handler ?? new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _client = new HttpClient(effectiveHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<DecisionCallResult> CallAsync(
        DecisionCallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var baseUrl = request.BaseUrl.TrimEnd('/');
        var payload = new JsonObject
        {
            ["state"] = request.State.DeepClone(),
            ["questions"] = request.Questions.DeepClone(),
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/systemone")
        {
            Content = new StringContent(payload.ToJsonString(CompactOptions), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout > TimeSpan.Zero)
        {
            cts.CancelAfter(request.Timeout);
        }

        HttpResponseMessage response;
        try
        {
            response = await _client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return DecisionCallResult.Fail(
                new DecisionCallError(
                    DecisionErrorKind.Timeout,
                    $"请求超时（{request.Timeout.TotalSeconds:0} 秒）。"),
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            return DecisionCallResult.Fail(
                new DecisionCallError(
                    DecisionErrorKind.ConnectionFailed,
                    ex.Message,
                    null,
                    ex.InnerException?.Message),
                stopwatch.Elapsed);
        }

        using (response)
        {
            string text;
            try
            {
                var (bytes, exceeded) = await ReadLimitedAsync(
                        response.Content,
                        request.MaxResponseBytes,
                        cts.Token)
                    .ConfigureAwait(false);
                if (exceeded)
                {
                    return DecisionCallResult.Fail(
                        new DecisionCallError(
                            DecisionErrorKind.ResponseTooLarge,
                            $"响应超过大小限制（{request.MaxResponseBytes / 1024} KB）。"),
                        stopwatch.Elapsed);
                }

                text = Encoding.UTF8.GetString(bytes);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return DecisionCallResult.Fail(
                    new DecisionCallError(
                        DecisionErrorKind.Timeout,
                        $"读取响应超时（{request.Timeout.TotalSeconds:0} 秒）。"),
                    stopwatch.Elapsed);
            }
            catch (HttpRequestException ex)
            {
                return DecisionCallResult.Fail(
                    new DecisionCallError(DecisionErrorKind.ConnectionFailed, ex.Message),
                    stopwatch.Elapsed);
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var summary = Summarize(text);
                var detail = status == (int)HttpStatusCode.NotFound
                    ? "服务未提供 /v1/systemone（需要 2026-10-02 之后、包含决策模型支持的 llama.cpp 构建）。"
                    : null;
                return DecisionCallResult.Fail(
                    new DecisionCallError(
                        DecisionErrorKind.HttpError,
                        summary,
                        status,
                        detail),
                    stopwatch.Elapsed);
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (JsonException ex)
            {
                return DecisionCallResult.Fail(
                    new DecisionCallError(
                        DecisionErrorKind.InvalidResponse,
                        "响应不是有效 JSON。",
                        null,
                        ex.Message),
                    stopwatch.Elapsed);
            }

            if (parsed is not JsonObject body)
            {
                return DecisionCallResult.Fail(
                    new DecisionCallError(
                        DecisionErrorKind.InvalidResponse,
                        "响应 JSON 顶层不是对象。"),
                    stopwatch.Elapsed);
            }

            if (body["answers"] is not JsonObject)
            {
                return DecisionCallResult.Fail(
                    new DecisionCallError(
                        DecisionErrorKind.InvalidResponse,
                        "响应缺少 answers 字段（不是决策模型响应）。",
                        null,
                        Summarize(text)),
                    stopwatch.Elapsed);
            }

            _logger.LogDebug(
                "决策调用成功：{BaseUrl}，耗时 {ElapsedMs:0} ms，响应 {Bytes} 字节。",
                baseUrl,
                stopwatch.Elapsed.TotalMilliseconds,
                text.Length);
            return DecisionCallResult.Ok(body, text, stopwatch.Elapsed);
        }
    }

    private static async Task<(byte[] Bytes, bool Exceeded)> ReadLimitedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var declared = content.Headers.ContentLength;
        if (declared is not null && declared > maxBytes)
        {
            return (Array.Empty<byte>(), true);
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(capacity: Math.Min(maxBytes, 64 * 1024));
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                return (Array.Empty<byte>(), true);
            }

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), false);
    }

    private static string Summarize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "（空响应）";
        }

        var trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }

    public void Dispose() => _client.Dispose();
}
