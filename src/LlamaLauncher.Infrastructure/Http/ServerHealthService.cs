using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Http;

/// <summary>llama-server HTTP 健康检查与轻量 API 探测（区分加载中/就绪/错误/不可达）。</summary>
public sealed class ServerHealthService : IServerHealthService, IDisposable
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client;
    private readonly ILogger<ServerHealthService> _logger;

    public ServerHealthService(ILogger<ServerHealthService> logger, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        string baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Trim(baseUrl) + "/health");
        AddApiKey(request, apiKey);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(HealthTimeout);
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                return new HealthCheckResult(HealthStatusKind.Ready, BodySummary(body), (int)response.StatusCode);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new HealthCheckResult(
                    HealthStatusKind.Error,
                    "API Key 无效或缺失（HTTP " + (int)response.StatusCode + "）。",
                    (int)response.StatusCode);
            }

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                var lower = body.ToLowerInvariant();
                if (lower.Contains("loading") || lower.Contains("load"))
                {
                    return new HealthCheckResult(HealthStatusKind.Loading, BodySummary(body), 503);
                }

                return new HealthCheckResult(
                    HealthStatusKind.Error,
                    "服务不可用（HTTP 503）" + (string.IsNullOrWhiteSpace(body) ? string.Empty : "：" + BodySummary(body)),
                    503);
            }

            if (body.Contains("\"error\"", StringComparison.OrdinalIgnoreCase)
                || (int)response.StatusCode >= 500)
            {
                return new HealthCheckResult(
                    HealthStatusKind.Error,
                    $"服务返回错误（HTTP {(int)response.StatusCode}）：{BodySummary(body)}",
                    (int)response.StatusCode);
            }

            // 其他状态码：只要返回 2xx 之外的都按错误处理
            return new HealthCheckResult(
                HealthStatusKind.Error,
                $"健康检查返回 HTTP {(int)response.StatusCode}。",
                (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new HealthCheckResult(HealthStatusKind.Unknown, "连接超时，服务可能仍在加载。", null);
        }
        catch (HttpRequestException ex)
        {
            return new HealthCheckResult(HealthStatusKind.Unknown, $"无法连接：{Describe(ex)}", null);
        }
    }

    public async Task<(IReadOnlyList<ServerModelInfo>? Models, string? Error)> GetModelsAsync(
        string baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Trim(baseUrl) + "/v1/models");
        AddApiKey(request, apiKey);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ApiTimeout);
            using var response = await _client.SendAsync(request, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"HTTP {(int)response.StatusCode}：{BodySummary(body)}");
            }

            return (ParseModels(body), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return (null, $"请求失败：{ex.Message}");
        }
    }

    public async Task<(string? RawJson, string? Error)> GetPropsAsync(
        string baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Trim(baseUrl) + "/props");
        AddApiKey(request, apiKey);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ApiTimeout);
            using var response = await _client.SendAsync(request, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (body, null)
                : (null, $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return (null, $"请求失败：{ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> TestChatAsync(
        string baseUrl,
        string? apiKey,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = modelId,
            messages = new[] { new { role = "user", content = "ping" } },
            max_tokens = 1,
            stream = false,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, Trim(baseUrl) + "/v1/chat/completions")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        AddApiKey(request, apiKey);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ApiTimeout);
            using var response = await _client.SendAsync(request, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (true, $"聊天接口连通（HTTP {(int)response.StatusCode}）。");
            }

            return (false, $"聊天接口返回 HTTP {(int)response.StatusCode}：{BodySummary(body)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return (false, $"请求失败：{ex.Message}");
        }
    }

    public void Dispose() => _client.Dispose();

    private static void AddApiKey(HttpRequestMessage request, string? apiKey)
    {
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    private static string Trim(string baseUrl) => baseUrl.TrimEnd('/');

    private static string BodySummary(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0)
        {
            return "（空响应）";
        }

        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }

    private static string Describe(HttpRequestException ex)
    {
        if (ex.InnerException is System.Net.Sockets.SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.ConnectionRefused => "连接被拒绝（服务未监听或已退出）。",
                System.Net.Sockets.SocketError.HostUnreachable => "目标主机不可达。",
                System.Net.Sockets.SocketError.TimedOut => "连接超时。",
                _ => ex.Message,
            };
        }

        return ex.Message;
    }

    private static List<ServerModelInfo> ParseModels(string body)
    {
        var models = new List<ServerModelInfo>();
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                    var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    models.Add(new ServerModelInfo(id, name, item.GetRawText()));
                }
            }
        }
        catch (JsonException ex)
        {
            models.Add(new ServerModelInfo(null, null, "解析失败：" + ex.Message));
        }

        return models;
    }
}
