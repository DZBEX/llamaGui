using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Chat;
using LlamaLauncher.Contracts;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Http;

/// <summary>
/// 真实 SSE 流式聊天客户端：
/// - 基于 ResponseHeadersRead 增量读取，不在 UI 线程聚合；
/// - 首 token 超时与整次响应时限可配置（长时间生成本身不受短超时影响）；
/// - 对无效帧容错（连续无效达到阈值才判协议错误）；
/// - 用户取消抛出 OperationCanceledException，由上层保存为“中断”。
/// </summary>
public sealed class StreamingChatClient : IChatCompletionClient, IDisposable
{
    private const int MaxConsecutiveInvalidFrames = 5;

    private readonly HttpClient _client;
    private readonly ILogger<StreamingChatClient> _logger;

    public StreamingChatClient(ILogger<StreamingChatClient> logger)
    {
        _logger = logger;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var requestMessage = BuildRequest(request, stream: true);
        HttpResponseMessage? response = null;
        StreamReader? reader = null;
        CancellationTokenSource? firstTokenCts = null;

        try
        {
            var (sendResponse, sendError) = await TrySendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
            response = sendResponse;
            if (sendError is not null)
            {
                yield return new ChatStreamEvent(ChatStreamEventKind.Error, Error: sendError);
                yield break;
            }

            if (response is null)
            {
                yield return new ChatStreamEvent(
                    ChatStreamEventKind.Error,
                    Error: new ChatError(ChatErrorKind.Unknown, "内部错误：响应为空。"));
                yield break;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                yield return new ChatStreamEvent(
                    ChatStreamEventKind.Error,
                    Error: ClassifyHttp((int)response.StatusCode, body));
                yield break;
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            reader = new StreamReader(stream, Encoding.UTF8);
            var firstTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            firstTokenCts = firstTokenSource;
            if (request.Timeouts.FirstToken > TimeSpan.Zero)
            {
                firstTokenSource.CancelAfter(request.Timeouts.FirstToken);
            }

            using var totalCts = request.Timeouts.Total is { } total
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            if (totalCts is not null && request.Timeouts.Total is { } totalLimit)
            {
                totalCts.CancelAfter(totalLimit);
            }

            var parser = new SseFrameParser();
            var buffer = new char[4096];
            var completed = false;
            var receivedAnyContent = false;
            var consecutiveInvalid = 0;
            int? promptTokens = null;
            int? completionTokens = null;

            while (!completed)
            {
                var readToken = totalCts?.Token ?? firstTokenSource.Token;
                var outcome = await ReadChunkAsync(reader, buffer, readToken, cancellationToken).ConfigureAwait(false);
                switch (outcome.Kind)
                {
                    case ReadOutcomeKind.Eof:
                        yield return new ChatStreamEvent(
                            ChatStreamEventKind.Error,
                            Error: new ChatError(
                                ChatErrorKind.Connection,
                                receivedAnyContent
                                    ? "连接在收到结束标记前中断（已收到的内容按中断保存）。"
                                    : "服务在返回任何内容前断开了连接。"));
                        completed = true;
                        break;

                    case ReadOutcomeKind.Timeout:
                        yield return new ChatStreamEvent(
                            ChatStreamEventKind.Error,
                            Error: new ChatError(
                                ChatErrorKind.Timeout,
                                $"等待响应超时（{request.Timeouts.FirstToken.TotalSeconds:0} 秒内未收到数据）。"));
                        completed = true;
                        break;

                    case ReadOutcomeKind.Error:
                        yield return new ChatStreamEvent(ChatStreamEventKind.Error, Error: outcome.Error);
                        completed = true;
                        break;

                    default:
                        if (outcome.Count <= 0)
                        {
                            continue;
                        }

                        foreach (var payload in parser.Feed(new string(buffer, 0, outcome.Count)))
                        {
                            // 收到任何载荷后，首 token 超时计时停止
                            try
                            {
                                firstTokenCts?.CancelAfter(Timeout.InfiniteTimeSpan);
                            }
                            catch (ObjectDisposedException)
                            {
                            }

                            var parsed = ParseChunk(payload);
                            switch (parsed.Kind)
                            {
                                case ChunkKind.Delta:
                                    consecutiveInvalid = 0;
                                    receivedAnyContent = true;
                                    yield return new ChatStreamEvent(ChatStreamEventKind.Delta, parsed.Text);
                                    break;

                                case ChunkKind.Done:
                                    consecutiveInvalid = 0;
                                    completed = true;
                                    yield return new ChatStreamEvent(
                                        ChatStreamEventKind.Completed,
                                        PromptTokens: parsed.PromptTokens ?? promptTokens,
                                        CompletionTokens: parsed.CompletionTokens ?? completionTokens);
                                    break;

                                case ChunkKind.ServerError:
                                    consecutiveInvalid = 0;
                                    completed = true;
                                    yield return new ChatStreamEvent(
                                        ChatStreamEventKind.Error,
                                        Error: new ChatError(
                                            ChatErrorKind.ServerError,
                                            parsed.ErrorMessage ?? "服务返回错误帧。",
                                            Detail: payload));
                                    break;

                                case ChunkKind.Ignored:
                                    consecutiveInvalid = 0;
                                    break;

                                default:
                                    consecutiveInvalid++;
                                    if (consecutiveInvalid >= MaxConsecutiveInvalidFrames)
                                    {
                                        completed = true;
                                        yield return new ChatStreamEvent(
                                            ChatStreamEventKind.Error,
                                            Error: new ChatError(
                                                ChatErrorKind.Protocol,
                                                "连续收到无法解析的流数据帧，疑似协议不兼容。",
                                                Detail: payload));
                                    }

                                    break;
                            }

                            if (parsed.PromptTokens is not null)
                            {
                                promptTokens = parsed.PromptTokens;
                            }

                            if (parsed.CompletionTokens is not null)
                            {
                                completionTokens = parsed.CompletionTokens;
                            }

                            if (completed)
                            {
                                break;
                            }
                        }

                        break;
                }
            }
        }
        finally
        {
            firstTokenCts?.Dispose();
            reader?.Dispose();
            response?.Dispose();
        }
    }

    public async Task<(bool Success, string? Content, ChatError? Error)> CompleteAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        using var requestMessage = BuildRequest(request, stream: false);
        var (response, sendError) = await TrySendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
        if (sendError is not null)
        {
            return (false, null, sendError);
        }

        using (response)
        {
            var body = await SafeReadBodyAsync(response!, cancellationToken).ConfigureAwait(false);
            if (!response!.IsSuccessStatusCode)
            {
                return (false, null, ClassifyHttp((int)response.StatusCode, body));
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(body);
                var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
                return (true, content, null);
            }
            catch (JsonException ex)
            {
                return (false, null, new ChatError(ChatErrorKind.Protocol, "无法解析服务响应：" + ex.Message, Detail: body));
            }
        }
    }

    public void Dispose() => _client.Dispose();

    private HttpRequestMessage BuildRequest(ChatRequest request, bool stream)
    {
        var payload = new ChatCompletionRequest
        {
            Model = request.ModelId,
            Stream = stream,
            Temperature = request.Parameters.Temperature,
            TopP = request.Parameters.TopP,
            TopK = request.Parameters.TopK,
            MinP = request.Parameters.MinP,
            RepeatPenalty = request.Parameters.RepeatPenalty,
            Seed = request.Parameters.Seed,
            MaxTokens = request.Parameters.MaxTokens,
            Stop = request.Parameters.StopSequences.Count > 0 ? request.Parameters.StopSequences : null,
        };
        foreach (var turn in request.Messages)
        {
            payload.Messages.Add(new ChatMessageDto
            {
                Role = turn.Role switch
                {
                    ChatRole.System => "system",
                    ChatRole.Assistant => "assistant",
                    _ => "user",
                },
                Content = turn.Content,
            });
        }

        var message = new HttpRequestMessage(
            HttpMethod.Post,
            request.BaseUrl.TrimEnd('/') + "/v1/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };

        if (!string.IsNullOrEmpty(request.ApiKey))
        {
            message.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", request.ApiKey);
        }

        return message;
    }

    private async Task<(HttpResponseMessage? Response, ChatError? Error)> TrySendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return (response, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, new ChatError(ChatErrorKind.Timeout, "连接服务超时。"));
        }
        catch (HttpRequestException ex)
        {
            return (null, new ChatError(ChatErrorKind.Connection, DescribeConnectError(ex)));
        }
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
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
        if (ex.InnerException is System.Net.Sockets.SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.ConnectionRefused => "连接被拒绝：服务未运行或端口不正确。",
                System.Net.Sockets.SocketError.TimedOut => "连接超时。",
                _ => "无法连接服务：" + ex.Message,
            };
        }

        return "无法连接服务：" + ex.Message;
    }

    private static ChatError ClassifyHttp(int statusCode, string body)
    {
        var serverMessage = ExtractServerMessage(body);
        var detail = body.Length <= 2000 ? body : body[..2000];
        return statusCode switch
        {
            400 => new ChatError(ChatErrorKind.BadRequest, serverMessage ?? "请求被拒绝（HTTP 400）。", statusCode, detail),
            401 or 403 => new ChatError(ChatErrorKind.Unauthorized, serverMessage ?? "API Key 无效或缺失。", statusCode, detail),
            404 => new ChatError(ChatErrorKind.NotFound, serverMessage ?? "接口不存在（HTTP 404），请确认服务版本。", statusCode, detail),
            429 => new ChatError(ChatErrorKind.RateLimited, serverMessage ?? "请求过于频繁（HTTP 429）。", statusCode, detail),
            >= 500 => new ChatError(ChatErrorKind.ServerError, serverMessage ?? $"服务内部错误（HTTP {statusCode}）。", statusCode, detail),
            _ => new ChatError(ChatErrorKind.Unknown, serverMessage ?? $"服务返回 HTTP {statusCode}。", statusCode, detail),
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

    private enum ReadOutcomeKind
    {
        Data = 0,
        Eof = 1,
        Timeout = 2,
        Error = 3,
    }

    private sealed record ReadOutcome(ReadOutcomeKind Kind, int Count, ChatError? Error);

    private async Task<ReadOutcome> ReadChunkAsync(
        StreamReader reader,
        char[] buffer,
        CancellationToken readToken,
        CancellationToken userToken)
    {
        try
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), readToken).ConfigureAwait(false);
            // 读取返回 0 表示流已结束（HttpClient 对 Connection: close / 分块结束的统一处理）
            return read == 0
                ? new ReadOutcome(ReadOutcomeKind.Eof, 0, null)
                : new ReadOutcome(ReadOutcomeKind.Data, read, null);
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ReadOutcome(ReadOutcomeKind.Timeout, 0, null);
        }
        catch (ObjectDisposedException)
        {
            return new ReadOutcome(ReadOutcomeKind.Eof, 0, null);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "读取聊天流失败");
            return new ReadOutcome(
                ReadOutcomeKind.Error,
                0,
                new ChatError(ChatErrorKind.Connection, "读取响应流失败：" + ex.Message));
        }
        catch (HttpRequestException ex)
        {
            return new ReadOutcome(
                ReadOutcomeKind.Error,
                0,
                new ChatError(ChatErrorKind.Connection, "连接中断：" + ex.Message));
        }
    }

    private enum ChunkKind
    {
        Ignored = 0,
        Delta = 1,
        Done = 2,
        ServerError = 3,
        Invalid = 4,
    }

    private sealed record ParsedChunk(
        ChunkKind Kind,
        string? Text = null,
        int? PromptTokens = null,
        int? CompletionTokens = null,
        string? ErrorMessage = null);

    private static ParsedChunk ParseChunk(string payload)
    {
        var trimmed = payload.Trim();
        if (trimmed.Length == 0)
        {
            return new ParsedChunk(ChunkKind.Ignored);
        }

        if (trimmed == "[DONE]")
        {
            return new ParsedChunk(ChunkKind.Done);
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorElement))
            {
                var message = errorElement.ValueKind == JsonValueKind.Object
                    && errorElement.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString()
                        : errorElement.ToString();
                return new ParsedChunk(ChunkKind.ServerError, ErrorMessage: message);
            }

            string? text = null;
            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta)
                    && delta.ValueKind == JsonValueKind.Object
                    && delta.TryGetProperty("content", out var contentElement)
                    && contentElement.ValueKind == JsonValueKind.String)
                {
                    text = contentElement.GetString();
                }
            }

            int? promptTokens = null;
            int? completionTokens = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var promptElement)
                    && promptElement.TryGetInt32(out var promptValue))
                {
                    promptTokens = promptValue;
                }

                if (usage.TryGetProperty("completion_tokens", out var completionElement)
                    && completionElement.TryGetInt32(out var completionValue))
                {
                    completionTokens = completionValue;
                }
            }

            // llama.cpp 在最后一个 chunk 用 timings 返回真实统计（没有 OpenAI usage 字段时作为回退）。
            if (root.TryGetProperty("timings", out var timings) && timings.ValueKind == JsonValueKind.Object)
            {
                if (promptTokens is null
                    && timings.TryGetProperty("prompt_n", out var promptN)
                    && promptN.TryGetInt32(out var promptNValue))
                {
                    promptTokens = promptNValue;
                }

                if (completionTokens is null
                    && timings.TryGetProperty("predicted_n", out var predictedN)
                    && predictedN.TryGetInt32(out var predictedNValue))
                {
                    completionTokens = predictedNValue;
                }
            }

            if (text is not null)
            {
                return new ParsedChunk(ChunkKind.Delta, text, promptTokens, completionTokens);
            }

            if (promptTokens is not null || completionTokens is not null)
            {
                return new ParsedChunk(ChunkKind.Ignored, PromptTokens: promptTokens, CompletionTokens: completionTokens);
            }

            // 没有 delta 内容也没有 usage：可能只有 finish_reason 的空帧
            return new ParsedChunk(ChunkKind.Ignored);
        }
        catch (JsonException)
        {
            return new ParsedChunk(ChunkKind.Invalid);
        }
    }
}
