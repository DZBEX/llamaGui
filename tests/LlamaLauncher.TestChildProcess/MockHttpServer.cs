// 模拟 llama-server 的最小 HTTP 接口，供集成测试使用：
//   GET  /health                 → ok / loading model / error（由环境变量控制）
//   GET  /v1/models              → 固定模型列表
//   GET  /props                  → 固定属性
//   POST /v1/chat/completions    → SSE 流式 / JSON / 错误（由环境变量控制）
// 通过 LLAMALAUNCHER_TEST_ENABLE_HTTP=1 与 --port <n> 启用。

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LlamaLauncher.TestChildProcess;

internal static class MockHttpServer
{
    private static readonly DateTime StartTimeUtc = DateTime.UtcNow;

    public static void Start(string host, int port)
    {
        var address = host switch
        {
            "0.0.0.0" => IPAddress.Any,
            "::" => IPAddress.IPv6Any,
            "localhost" => IPAddress.Loopback,
            _ => IPAddress.TryParse(host, out var ip) ? ip : IPAddress.Loopback,
        };

        var listener = new TcpListener(address, port);
        listener.Start();
        Console.Error.WriteLine($"[mock-http] listening on {address}:{port}");
        _ = Task.Run(async () =>
        {
            while (true)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptSocketAsync();
                }
                catch (Exception)
                {
                    break;
                }

                _ = Task.Run(() => HandleConnection(socket));
            }
        });
    }

    private static void HandleConnection(Socket socket)
    {
        using (socket)
        {
            try
            {
                using var stream = new NetworkStream(socket, ownsSocket: false);
                var (method, path, body) = ReadRequest(stream);
                if (method is null || path is null)
                {
                    return;
                }

                switch (path!.Split('?')[0])
                {
                    case "/health":
                        HandleHealth(stream);
                        break;
                    case "/v1/models":
                        WriteResponse(
                            stream,
                            200,
                            "application/json",
                            """{"object":"list","data":[{"id":"test-model","object":"model","owned_by":"llama.cpp"}]}""");
                        break;
                    case "/props":
                        WriteResponse(
                            stream,
                            200,
                            "application/json",
                            """{"default_generation_settings":{"n_ctx":4096},"model_path":"test.gguf","total_slots":1}""");
                        break;
                    case "/v1/chat/completions" when method == "POST":
                        HandleChat(stream, body);
                        break;
                    default:
                        WriteResponse(stream, 404, "application/json", """{"error":"not found"}""");
                        break;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Console.Error.WriteLine("[mock-http] connection error: " + ex.Message);
                }
                catch
                {
                }
            }
        }
    }

    private static void HandleHealth(Stream stream)
    {
        var mode = Environment.GetEnvironmentVariable("LLAMALAUNCHER_TEST_HEALTH_MODE") ?? "ok";
        var readyAfterMs = GetInt("LLAMALAUNCHER_TEST_HEALTH_READY_AFTER_MS", 0);
        var elapsedMs = (int)(DateTime.UtcNow - StartTimeUtc).TotalMilliseconds;

        switch (mode)
        {
            case "error":
                WriteResponse(stream, 500, "application/json", """{"error":{"message":"failed to load model"}}""");
                break;
            case "loading":
                WriteResponse(stream, 503, "application/json", """{"status":"loading model"}""");
                break;
            case "ready-then-loading":
                WriteResponse(
                    stream,
                    elapsedMs < 2000 ? 200 : 503,
                    "application/json",
                    elapsedMs < 2000 ? """{"status":"ok"}""" : """{"status":"loading model"}""");
                break;
            default:
                if (elapsedMs < readyAfterMs)
                {
                    WriteResponse(stream, 503, "application/json", """{"status":"loading model"}""");
                }
                else
                {
                    WriteResponse(stream, 200, "application/json", """{"status":"ok"}""");
                }

                break;
        }
    }

    private static void HandleChat(Stream stream, string requestBody)
    {
        var mode = Environment.GetEnvironmentVariable("LLAMALAUNCHER_TEST_CHAT_MODE") ?? "stream";
        var text = Environment.GetEnvironmentVariable("LLAMALAUNCHER_TEST_CHAT_TEXT") ?? "你好，这是一条测试回复。";
        var delayMs = GetInt("LLAMALAUNCHER_TEST_CHAT_CHUNK_DELAY_MS", 20);

        switch (mode)
        {
            case "401":
                WriteResponse(stream, 401, "application/json", """{"error":{"message":"Invalid API Key","code":401}}""");
                return;
            case "500":
                WriteResponse(stream, 500, "application/json", """{"error":{"message":"internal server error"}}""");
                return;
            case "json":
                WriteResponse(
                    stream,
                    200,
                    "application/json",
                    """{"id":"chatcmpl-test","object":"chat.completion","model":"test-model","choices":[{"index":0,"message":{"role":"assistant","content":"固定回复"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":3,"total_tokens":8}}""");
                return;
            case "bad-stream":
                WriteSseRaw(stream, "data: {not-json}\n\ndata: [DONE]\n\n");
                return;
            case "timings-stream":
            {
                // 模拟 llama.cpp：增量 content + 最后一个 chunk 携带 timings（没有 OpenAI usage 字段）。
                var builder = new StringBuilder();
                foreach (var chunk in SplitIntoChunks(text))
                {
                    builder.Append("data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"")
                        .Append(JsonEscape(chunk))
                        .Append("\"},\"finish_reason\":null}]}\n\n");
                }

                builder.Append("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"timings\":{\"prompt_n\":9,\"predicted_n\":42,\"predicted_per_second\":77.5}}\n\n");
                builder.Append("data: [DONE]\n\n");
                WriteSseRaw(stream, builder.ToString());
                return;
            }
            case "truncated-stream":
                // 发送部分内容后直接断开（不做 [DONE] 结束，模拟连接中断）
                WriteSseChunks(stream, new[] { "半截回复" }, delayMs, sendDone: false);
                return;
            default:
                if (requestBody.Contains("\"stream\":true", StringComparison.OrdinalIgnoreCase)
                    || requestBody.Contains("\"stream\": true", StringComparison.OrdinalIgnoreCase))
                {
                    WriteSseChunks(stream, SplitIntoChunks(text), delayMs);
                }
                else
                {
                    WriteResponse(
                        stream,
                        200,
                        "application/json",
                        """{"id":"chatcmpl-test","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"固定回复"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":3,"total_tokens":8}}""");
                }

                return;
        }
    }

    private static IEnumerable<string> SplitIntoChunks(string text)
    {
        // 按 1-3 个字符切块，模拟真实 token 流
        var index = 0;
        while (index < text.Length)
        {
            var size = Math.Min(3, text.Length - index);
            yield return text.Substring(index, size);
            index += size;
        }
    }

    private static void WriteSseChunks(Stream stream, IEnumerable<string> chunks, int delayMs, bool sendDone = true)
    {
        WriteSseHeader(stream);
        var first = true;
        foreach (var chunk in chunks)
        {
            if (!first && delayMs > 0)
            {
                Thread.Sleep(delayMs);
            }

            first = false;
            var payload =
                "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" +
                JsonEscape(chunk) +
                "\"},\"finish_reason\":null}]}\n\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            stream.Write(bytes);
            stream.Flush();
        }

        if (sendDone)
        {
            var done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
            stream.Write(done);
            stream.Flush();
        }
    }

    private static void WriteSseRaw(Stream stream, string raw)
    {
        WriteSseHeader(stream);
        var bytes = Encoding.UTF8.GetBytes(raw);
        stream.Write(bytes);
        stream.Flush();
    }

    private static void WriteSseHeader(Stream stream)
    {
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Connection: close\r\n\r\n");
        stream.Write(header);
        stream.Flush();
    }

    private static (string? Method, string? Path, string Body) ReadRequest(Stream stream)
    {
        var buffer = new byte[8192];
        var received = new StringBuilder();
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return (null, null, string.Empty);
            }

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            headerEnd = received.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }

        var text = received.ToString();
        var headerPart = text[..headerEnd];
        var lines = headerPart.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            return (null, null, string.Empty);
        }

        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line[15..].Trim(), out contentLength);
            }
        }

        var bodyStart = headerEnd + 4;
        var body = text.Length > bodyStart ? text[bodyStart..] : string.Empty;
        while (body.Length < contentLength)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            body += Encoding.UTF8.GetString(buffer, 0, read);
        }

        return (requestLine[0], requestLine[1], body);
    }

    private static void WriteResponse(Stream stream, int status, string contentType, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header =
            $"HTTP/1.1 {status} {StatusText(status)}\r\n" +
            $"Content-Type: {contentType}; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n";
        stream.Write(Encoding.ASCII.GetBytes(header));
        stream.Write(bodyBytes);
        stream.Flush();
    }

    private static string StatusText(int status) => status switch
    {
        200 => "OK",
        401 => "Unauthorized",
        404 => "Not Found",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "OK",
    };

    private static string JsonEscape(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static int GetInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
}
