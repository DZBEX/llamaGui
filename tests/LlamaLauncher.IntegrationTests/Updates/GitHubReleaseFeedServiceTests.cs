using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Domain;
using LlamaLauncher.Infrastructure.GitHub;
using LlamaLauncher.IntegrationTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Updates;

/// <summary>
/// GitHub Releases 查询的缓存与速率限制行为（假 HTTP 处理器，不发真实请求）：
/// 新鲜窗口复用、ETag 条件请求（304）、限额恢复时间与冷却、缓存回退、可选令牌、跨重启缓存。
/// </summary>
public sealed class GitHubReleaseFeedServiceTests : IDisposable
{
    internal sealed record CapturedRequest(string Url, string? IfNoneMatch, string? Authorization);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

        public List<CapturedRequest> Requests { get; } = new();

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responses.Enqueue(responder);

        public void EnqueueJson(string json, string? etag = null)
            => Enqueue(_ => Json(json, etag));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("If-None-Match", out var etags) ? etags.FirstOrDefault() : null,
                request.Headers.Authorization?.ToString()));
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("测试未预设更多响应（不应再发请求）。");
            }

            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private const string SampleJson = """
        [
          {
            "tag_name": "b10618",
            "name": "b10618",
            "prerelease": false,
            "published_at": "2026-10-15T10:00:00Z",
            "body": "notes",
            "html_url": "https://github.com/ggml-org/llama.cpp/releases/tag/b10618",
            "assets": [
              {
                "name": "llama-b10618-bin-win-cpu-x64.zip",
                "size": 123,
                "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/b10618/llama-b10618-bin-win-cpu-x64.zip",
                "digest": "sha256:abc"
              }
            ]
          },
          {
            "tag_name": "b10619-pre",
            "name": "b10619-pre",
            "prerelease": true,
            "published_at": "2026-10-16T10:00:00Z",
            "body": null,
            "html_url": "https://github.com/ggml-org/llama.cpp/releases/tag/b10619-pre",
            "assets": []
          }
        ]
        """;

    private readonly TempDirectory _directory = new();
    private readonly TestAppPaths _paths;
    private readonly FakeTimeProvider _time = new();
    private readonly InMemorySecretStore _secrets = new();

    public GitHubReleaseFeedServiceTests() => _paths = new TestAppPaths(_directory.Path);

    public void Dispose() => _directory.Dispose();

    private GitHubReleaseFeedService CreateService(StubHandler handler)
        => new(
            _time,
            NullLogger<GitHubReleaseFeedService>.Instance,
            _paths,
            _secrets,
            handler);

    private static HttpResponseMessage Json(string json, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    private static HttpResponseMessage RateLimited(DateTimeOffset resetUtc)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                "{\"message\":\"API rate limit exceeded\"}",
                Encoding.UTF8,
                "application/json"),
        };
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", resetUtc.ToUnixTimeSeconds().ToString());
        return response;
    }

    [Fact]
    public async Task Success_ParsesAndCaches_ThenReusesWithinWindowForBothChannels()
    {
        var handler = new StubHandler();
        handler.EnqueueJson(SampleJson, etag: "\"etag-1\"");
        using var service = CreateService(handler);

        var stable = await service.GetReleasesAsync(UpdateChannel.Stable);
        var prerelease = await service.GetReleasesAsync(UpdateChannel.Prerelease);

        Assert.True(stable.Success);
        Assert.False(stable.FromCache);
        Assert.Null(stable.Warning);
        Assert.Single(stable.Releases);
        Assert.Equal("b10618", stable.Releases[0].TagName);

        // 预发布通道与稳定通道共享同一份数据：窗口内不再发请求
        Assert.True(prerelease.Success);
        Assert.True(prerelease.FromCache);
        Assert.Equal(2, prerelease.Releases.Count);
        Assert.Single(handler.Requests);

        var cacheFile = Path.Combine(_paths.CacheDirectory, "github-releases-ggml-org-llama-cpp.json");
        Assert.True(File.Exists(cacheFile));
    }

    [Fact]
    public async Task AfterReuseWindow_SendsConditionalRequest_AndServesNotModifiedFromCache()
    {
        var handler = new StubHandler();
        handler.EnqueueJson(SampleJson, etag: "\"etag-1\"");
        using var service = CreateService(handler);
        await service.GetReleasesAsync(UpdateChannel.Stable);

        _time.Advance(TimeSpan.FromSeconds(61));
        handler.Enqueue(_ => NotModified());
        var result = await service.GetReleasesAsync(UpdateChannel.Stable);

        Assert.True(result.Success);
        Assert.True(result.FromCache);
        Assert.Null(result.Warning);
        Assert.Equal("b10618", result.Releases[0].TagName);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("\"etag-1\"", handler.Requests[1].IfNoneMatch);
        Assert.Equal(_time.GetUtcNow(), result.FetchedUtc);
    }

    [Fact]
    public async Task RateLimited_WithCache_ReturnsCachedDataWithRecoveryTime_AndStopsRequests()
    {
        var handler = new StubHandler();
        handler.EnqueueJson(SampleJson, etag: "\"etag-1\"");
        using var service = CreateService(handler);
        await service.GetReleasesAsync(UpdateChannel.Stable);

        _time.Advance(TimeSpan.FromSeconds(61));
        var reset = _time.GetUtcNow() + TimeSpan.FromMinutes(23);
        handler.Enqueue(_ => RateLimited(reset));
        var limited = await service.GetReleasesAsync(UpdateChannel.Stable);

        Assert.True(limited.Success);
        Assert.True(limited.FromCache);
        Assert.NotNull(limited.Warning);
        Assert.Contains("频率已达上限", limited.Warning);
        Assert.Contains("缓存数据", limited.Warning);
        Assert.Contains("恢复", limited.Warning);

        // 恢复时间之前不再发起请求（冷却状态来自响应头）
        _time.Advance(TimeSpan.FromMinutes(1));
        var stillLimited = await service.GetReleasesAsync(UpdateChannel.Stable);
        Assert.True(stillLimited.Success);
        Assert.NotNull(stillLimited.Warning);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RateLimited_WithoutCache_ReturnsErrorWithRecoveryTime_AndStopsRequests()
    {
        var handler = new StubHandler();
        var reset = _time.GetUtcNow() + TimeSpan.FromMinutes(10);
        handler.Enqueue(_ => RateLimited(reset));
        using var service = CreateService(handler);

        var result = await service.GetReleasesAsync(UpdateChannel.Stable);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("频率已达上限", result.Error);
        Assert.Contains("恢复", result.Error);
        Assert.Contains("令牌", result.Error);
        Assert.Empty(result.Releases);

        var again = await service.GetReleasesAsync(UpdateChannel.Stable);
        Assert.False(again.Success);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NetworkError_FallsBackToCacheWithWarning()
    {
        var handler = new StubHandler();
        handler.EnqueueJson(SampleJson, etag: "\"etag-1\"");
        using var service = CreateService(handler);
        await service.GetReleasesAsync(UpdateChannel.Stable);

        _time.Advance(TimeSpan.FromSeconds(61));
        handler.Enqueue(_ => throw new HttpRequestException("offline"));
        var result = await service.GetReleasesAsync(UpdateChannel.Stable);

        Assert.True(result.Success);
        Assert.True(result.FromCache);
        Assert.NotNull(result.Warning);
        Assert.Contains("无法连接 GitHub", result.Warning);
        Assert.Contains("缓存数据", result.Warning);
        Assert.Single(result.Releases);
    }

    [Fact]
    public async Task NetworkError_WithoutCache_ReturnsOfflineMessage()
    {
        var handler = new StubHandler();
        handler.Enqueue(_ => throw new HttpRequestException("offline"));
        using var service = CreateService(handler);

        var result = await service.GetReleasesAsync(UpdateChannel.Stable);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("无法连接 GitHub", result.Error);
        Assert.Contains("导入本地压缩包", result.Error);
    }

    [Fact]
    public async Task ConfiguredToken_IsSentAsBearerAuthorization()
    {
        await _secrets.SetAsync(SecretNames.GitHubToken, "ghp_TESTTOKEN");
        var handler = new StubHandler();
        handler.EnqueueJson(SampleJson, etag: "\"etag-1\"");
        using var service = CreateService(handler);

        var result = await service.GetReleasesAsync(UpdateChannel.Stable);

        Assert.True(result.Success);
        Assert.Equal("Bearer ghp_TESTTOKEN", Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task CacheAndEtag_SurviveRestart()
    {
        var handler = new StubHandler();
        handler.EnqueueJson(SampleJson, etag: "\"etag-1\"");
        using (var service = CreateService(handler))
        {
            await service.GetReleasesAsync(UpdateChannel.Stable);
        }

        // 新实例（同一数据目录）：窗口内直接读磁盘缓存，不发请求
        var restartedHandler = new StubHandler();
        using var restarted = CreateService(restartedHandler);
        var cached = await restarted.GetReleasesAsync(UpdateChannel.Stable);

        Assert.True(cached.Success);
        Assert.True(cached.FromCache);
        Assert.Empty(restartedHandler.Requests);
        Assert.Equal("b10618", cached.Releases[0].TagName);

        // 窗口过后用持久化的 ETag 发条件请求
        _time.Advance(TimeSpan.FromSeconds(61));
        restartedHandler.Enqueue(_ => NotModified());
        var revalidated = await restarted.GetReleasesAsync(UpdateChannel.Stable);

        Assert.True(revalidated.Success);
        Assert.Equal("\"etag-1\"", Assert.Single(restartedHandler.Requests).IfNoneMatch);
    }

    private static HttpResponseMessage NotModified()
        => new(HttpStatusCode.NotModified)
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
        };
}
