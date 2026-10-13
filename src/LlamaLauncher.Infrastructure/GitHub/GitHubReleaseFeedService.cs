using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Models;
using LlamaLauncher.Domain;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.GitHub;

/// <summary>
/// 官方 llama.cpp Releases 查询（api.github.com/repos/ggml-org/llama.cpp/releases）。
/// 只读取元数据，不硬编码任何 release 标签或资产名。
/// </summary>
public sealed class GitHubReleaseFeedService : IReleaseFeedService, IDisposable
{
    public const string OfficialRepository = "ggml-org/llama.cpp";

    private readonly HttpClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GitHubReleaseFeedService> _logger;

    public GitHubReleaseFeedService(TimeProvider timeProvider, ILogger<GitHubReleaseFeedService> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
        _client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LlamaLauncher", "0.9"));
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public string Repository { get; init; } = OfficialRepository;

    public async Task<ReleaseFeedResult> GetReleasesAsync(
        UpdateChannel channel,
        int maxReleases = 30,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var url = $"https://api.github.com/repos/{Repository}/releases?per_page={Math.Clamp(maxReleases, 1, 100)}";
        try
        {
            using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
            {
                var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
                    ? values.FirstOrDefault()
                    : null;
                var message = remaining == "0"
                    ? "GitHub API 请求频率已达上限，请稍后重试或直接打开官方发布页。"
                    : "GitHub API 拒绝了请求（可能触发速率限制）。";
                return new ReleaseFeedResult(false, Array.Empty<ReleaseInfo>(), message, now);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ReleaseFeedResult(
                    false,
                    Array.Empty<ReleaseInfo>(),
                    "GitHub 仓库不存在或不可访问。",
                    now);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ReleaseFeedResult(
                    false,
                    Array.Empty<ReleaseInfo>(),
                    $"GitHub 返回 HTTP {(int)response.StatusCode}。",
                    now);
            }

            var releases = ParseReleases(body, channel);
            _logger.LogInformation("已获取 {Count} 个发行版本（通道：{Channel}）", releases.Count, channel);
            return new ReleaseFeedResult(true, releases, null, now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return new ReleaseFeedResult(
                false,
                Array.Empty<ReleaseInfo>(),
                "无法连接 GitHub（请检查网络）。可打开官方发布页手动下载后通过“导入本地压缩包”安装。",
                now);
        }
    }

    public void Dispose() => _client.Dispose();

    private static List<ReleaseInfo> ParseReleases(string json, UpdateChannel channel)
    {
        var results = new List<ReleaseInfo>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var prerelease = element.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();
            if (channel == UpdateChannel.Stable && prerelease)
            {
                continue;
            }

            var tag = element.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString() ?? string.Empty
                : string.Empty;
            if (tag.Length == 0)
            {
                continue;
            }

            var release = new ReleaseInfo
            {
                TagName = tag,
                Name = element.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null,
                Prerelease = prerelease,
                PublishedAtUtc = element.TryGetProperty("published_at", out var publishedElement)
                    && publishedElement.ValueKind == JsonValueKind.String
                    && publishedElement.TryGetDateTimeOffset(out var published)
                        ? published
                        : default,
                BodyMarkdown = element.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : null,
                HtmlUrl = element.TryGetProperty("html_url", out var htmlElement)
                    ? htmlElement.GetString() ?? string.Empty
                    : string.Empty,
            };

            if (element.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var assetElement in assets.EnumerateArray())
                {
                    var name = assetElement.TryGetProperty("name", out var assetName)
                        ? assetName.GetString()
                        : null;
                    var downloadUrl = assetElement.TryGetProperty("browser_download_url", out var urlElement)
                        ? urlElement.GetString()
                        : null;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(downloadUrl))
                    {
                        continue;
                    }

                    release.Assets.Add(new ReleaseAsset
                    {
                        Name = name!,
                        SizeBytes = assetElement.TryGetProperty("size", out var sizeElement)
                            && sizeElement.ValueKind == JsonValueKind.Number
                            && sizeElement.TryGetInt64(out var size)
                                ? size
                                : null,
                        DownloadUrl = downloadUrl!,
                        Sha256Digest = assetElement.TryGetProperty("digest", out var digestElement)
                            ? digestElement.GetString()
                            : null,
                        BrowserUrl = downloadUrl,
                    });
                }
            }

            results.Add(release);
        }

        return results;
    }
}
